using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SimpleDms.Core;

public sealed record DocumentRecord(int Row, string Code, string Title, string Reference, string Author,
    string Validity, bool Electronic, string RelativePath, string DriveUrl, string LocalUrl, string Notes,
    string DriveId, bool Pending)
{
    public string Category => Code[..2];
    public string State => Pending ? "Rozpracovaný" : "Dokončený";
    public override string ToString() => $"{Code}   {Title}";
    public bool Matches(string query) => query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .All(word => SearchText.Fold($"{Code} {Title} {Reference} {Author} {Validity} {Notes}")
            .Contains(SearchText.Fold(word), StringComparison.Ordinal));
}
public static class SearchText
{
    public static string Fold(string value) => string.Concat(value.Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)).ToUpperInvariant();
}
public sealed record DocumentDraft(string Category, string Title, string Reference = "", string Author = "",
    string Validity = "", string Notes = "", bool Pending = false);
// The archive is a local folder kept in sync by Google Drive for desktop, Insync or rclone.
// Drive fields are optional and serve only to fill Google IDs (Q) for links and QR codes.
public sealed record ArchiveProfile(string Root, string Name, string DocumentsName,
    string? DriveRootId = null, string AccountId = "", string AccountEmail = "")
{
    public string WorkbookPath => Path.Combine(Root, Name + ".xlsx");
    public string DocumentsPath => Path.Combine(Root, DocumentsName);
    public bool DriveLinked => !string.IsNullOrEmpty(DriveRootId) && AccountId.Length > 0;
    public string Key
    {
        get
        {
            var root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar);
            if (OperatingSystem.IsWindows()) root = root.ToUpperInvariant();
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(root + "|" + Name)))[..24];
        }
    }
}
public sealed class AppSettings
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public bool ReadOnly { get; set; }
    public string Printer { get; set; } = "";
    public ArchiveProfile? Archive { get; set; }
    public LabelProfile Labels { get; set; } = new();
    public LabelSheet Sheet { get; set; } = new();
    public List<LabelItem> LabelQueue { get; set; } = [];
    public Dictionary<string, LabelProfile> LabelProfiles { get; set; } = [];
    public Dictionary<string, LabelSheet> LabelSheets { get; set; } = [];
    public Dictionary<string, List<LabelItem>> ArchiveLabelQueues { get; set; } = [];
}
public sealed record LabelItem(string Code, string Title, string Url = "", string Id = "")
{
    public string Key { get; init; } = Guid.NewGuid().ToString("N");
    public override string ToString() => Code + "   " + Title;
}
public sealed class LocalStore
{
    public string Root { get; }
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public LocalStore(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SimpleDMS");
        Directory.CreateDirectory(Root);
    }
    public T? Read<T>(string name) => File.Exists(PathFor(name)) ? JsonSerializer.Deserialize<T>(File.ReadAllText(PathFor(name)), Json) : default;
    public void Write<T>(string name, T value)
    {
        var path = PathFor(name);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
        File.Move(temp, path, true);
    }
    public string PathFor(string name) => Path.Combine(Root, name);
    public string ArchiveDirectory(ArchiveProfile archive)
    {
        var dir = PathFor(archive.Key);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
public static partial class ArchivePaths
{
    [GeneratedRegex("^[A-Za-z0-9_-]+$")] private static partial Regex IdPattern();
    [GeneratedRegex("^[0-9]{2}$")] private static partial Regex CategoryPattern();
    public static string ParseRoot(string input)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "drive.google.com")
            throw new InvalidOperationException("Zadejte odkaz na složku https://drive.google.com/drive/folders/…");
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var index = Array.IndexOf(parts, "folders");
        if (index < 0 || index + 1 >= parts.Length || !IdPattern().IsMatch(parts[index + 1]))
            throw new InvalidOperationException("Odkaz neobsahuje ID složky Google Drive.");
        return parts[index + 1];
    }
    public static string NextCode(IEnumerable<DocumentRecord> rows, string category, IEnumerable<string>? reserved = null)
    {
        if (!CategoryPattern().IsMatch(category)) throw new InvalidOperationException("Kategorie musí mít dvě číslice.");
        int maximum = rows.Select(x => x.Code).Concat(reserved ?? []).Where(x => x.Length == 6 && x.StartsWith(category, StringComparison.Ordinal))
            .Select(x => int.Parse(x[2..], CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Max();
        if (maximum >= 9999) throw new InvalidOperationException("Číselná řada kategorie je vyčerpaná.");
        return category + (maximum + 1).ToString("D4", CultureInfo.InvariantCulture);
    }
    public static string SafeName(string name)
    {
        var invalid = "<>:\"/\\|?*";
        var result = string.Concat(name.Select(c => c < 32 || invalid.Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(result) || result is "." or "..") result = "Dokument";
        if (Regex.IsMatch(result, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])($|\\.)", RegexOptions.IgnoreCase)) result = "_" + result;
        return result.Length > 120 ? result[..120] : result;
    }
    public static string ResolveLocal(string root, string relative)
    {
        relative = relative.Replace('\\', '/');
        if (Path.IsPathRooted(relative) || relative.Split('/').Any(x => x is ".." or "."))
            throw new InvalidOperationException("Příloha obsahuje neplatnou relativní cestu.");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Příloha je mimo místní archiv.");
        var current = fullRoot.TrimEnd(Path.DirectorySeparatorChar);
        foreach (var segment in Path.GetRelativePath(current, full).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Příloha vede přes symbolický odkaz. Vyberte skutečný místní kořen.");
        }
        return full;
    }
}
