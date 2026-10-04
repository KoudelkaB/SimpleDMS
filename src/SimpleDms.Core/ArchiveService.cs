using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SimpleDms.Core;

public sealed record ArchiveChoice(string Name, string? Workbook, string? Folder)
{
    public override string ToString() => Name + (Workbook == null ? " (nový registr)" : "") + (Folder == null ? " (vytvoří se složka dokumentů)" : Folder != Name ? " — složka: " + Folder : "");
}
// The archive lives in a local folder that a sync client (Google Drive for desktop, Insync, rclone)
// mirrors to the cloud. Every operation is a plain file operation; uploading happens in the background.
public sealed class ArchiveService(LocalStore store)
{
    readonly SemaphoreSlim writer = new(1, 1);
    public static IReadOnlyList<ArchiveChoice> Discover(string root)
    {
        if (!Directory.Exists(root)) throw new InvalidOperationException("Složka není dostupná: " + root);
        var folders = Directory.EnumerateDirectories(root).Select(Path.GetFileName).OfType<string>().ToList();
        var choices = new List<ArchiveChoice>();
        // "~$name.xlsx" is Excel's lock file of an open workbook.
        foreach (var name in Directory.EnumerateFiles(root, "*.xlsx").Select(Path.GetFileName).OfType<string>().Where(x => !x.StartsWith("~$")).Select(x => x[..^5]).Order())
        {
            var matches = folders.Where(x => x == name).ToList();
            // Existing archives may use underscores where their workbook uses spaces.
            if (matches.Count == 0) matches = folders.Where(x => x.Replace('_', ' ') == name.Replace('_', ' ')).ToList();
            if (matches.Count == 0) choices.Add(new(name, name, null)); else choices.AddRange(matches.Select(f => new ArchiveChoice(name, name, f)));
        }
        return choices;
    }
    public ArchiveProfile Open(string root, ArchiveChoice choice, bool canCreate)
    {
        if (string.IsNullOrWhiteSpace(choice.Name) || choice.Name != ArchivePaths.SafeName(choice.Name)) throw new InvalidOperationException("Název archivu obsahuje nepovolené znaky.");
        if (!Directory.Exists(root)) throw new InvalidOperationException("Složka není dostupná: " + root);
        var p = new ArchiveProfile(Path.GetFullPath(root), choice.Name, choice.Folder ?? choice.Name);
        if ((!File.Exists(p.WorkbookPath) || !Directory.Exists(p.DocumentsPath)) && !canCreate) throw new InvalidOperationException("Chybějící registr nebo složku dokumentů může založit jen uživatel se zápisem.");
        if (File.Exists(p.WorkbookPath)) _ = new WorkbookCatalog(ReadShared(p.WorkbookPath));
        else using (var file = new FileStream(p.WorkbookPath, FileMode.CreateNew, FileAccess.Write)) file.Write(WorkbookCatalog.Create());
        Directory.CreateDirectory(p.DocumentsPath);
        return p;
    }
    static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream(); stream.CopyTo(memory); return memory.ToArray();
    }
    public string CachedWorkbook(ArchiveProfile p) => Path.Combine(store.ArchiveDirectory(p), "catalog.xlsx");
    void Cache(ArchiveProfile p, byte[] bytes) { var path = CachedWorkbook(p); File.WriteAllBytes(path + ".tmp", bytes); File.Move(path + ".tmp", path, true); }
    // Cached is true when the synchronized folder is unavailable and the last known copy is shown.
    public (WorkbookCatalog Catalog, bool Cached) Load(ArchiveProfile p)
    {
        byte[] bytes;
        try { bytes = ReadShared(p.WorkbookPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (File.Exists(CachedWorkbook(p))) return (new WorkbookCatalog(File.ReadAllBytes(CachedWorkbook(p))), true);
            throw new InvalidOperationException("Registr není dostupný: " + e.Message);
        }
        var catalog = new WorkbookCatalog(bytes); Cache(p, bytes); return (catalog, false);
    }
    public static DateTime Stamp(ArchiveProfile p) { try { return File.GetLastWriteTimeUtc(p.WorkbookPath); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return default; } }
    // Read, change and write the workbook under one exclusive handle: if Excel or the sync client holds
    // the file, the change fails instead of being lost. Writing in place keeps the Drive file identity.
    async Task<T> EditAsync<T>(ArchiveProfile p, Func<WorkbookCatalog, Task<T>> mutate, CancellationToken ct)
    {
        await writer.WaitAsync(ct);
        try
        {
            FileStream stream;
            try { stream = new FileStream(p.WorkbookPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (UnauthorizedAccessException) { throw new InvalidOperationException("Do registru nelze zapisovat. Zkontrolujte oprávnění ke sdílené složce."); }
            catch (IOException e) when (e is not (FileNotFoundException or DirectoryNotFoundException))
            { throw new InvalidOperationException("Registr je otevřený v jiné aplikaci (např. Excel) nebo jej právě zapisuje synchronizační klient. Zavřete jej a zkuste to znovu."); }
            await using (stream)
            {
                var original = new byte[stream.Length]; await stream.ReadExactlyAsync(original, ct);
                var catalog = new WorkbookCatalog(original);
                var result = await mutate(catalog);
                var updated = catalog.Save(); _ = new WorkbookCatalog(updated);
                Backup(p, original);
                // Past this point the write must not be interrupted half way.
                stream.Position = 0; await stream.WriteAsync(updated, CancellationToken.None); stream.SetLength(updated.Length); stream.Flush(true);
                // The register is written; a failed cache copy must not undo the operation.
                try { Cache(p, updated); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                return result;
            }
        }
        finally { writer.Release(); }
    }
    void Backup(ArchiveProfile p, byte[] bytes)
    {
        var folder = Path.Combine(store.ArchiveDirectory(p), "backups"); Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + ".xlsx"), bytes);
        foreach (var old in Directory.EnumerateFiles(folder, "*.xlsx").Order().SkipLast(50)) File.Delete(old);
    }
    // Folders like "100241_Title" reserve their number even when the register does not list them yet.
    static IEnumerable<string> ReservedCodes(ArchiveProfile p) => Directory.EnumerateFileSystemEntries(p.DocumentsPath)
        .Select(Path.GetFileName).OfType<string>().Where(x => Regex.IsMatch(x, "^[0-9]{6}(?![0-9])")).Select(x => x[..6]);
    public static string PreviewCode(WorkbookCatalog catalog, ArchiveProfile p, string category)
    {
        try { return ArchivePaths.NextCode(catalog.Records, category, Directory.Exists(p.DocumentsPath) ? ReservedCodes(p) : []); }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException) { return ""; }
    }
    // As in the legacy register, a document is a single item named by its code: one file becomes
    // "<code>.<ext>", one folder becomes the folder "<code>", several items go into a folder "<code>".
    // A folder is treated like a file: it is copied whole, including its subfolders.
    public async Task<DocumentRecord> AddAsync(ArchiveProfile p, DocumentDraft draft, IEnumerable<string> items, string? existingCode = null, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (existingCode == null && string.IsNullOrWhiteSpace(draft.Title)) throw new InvalidOperationException("Vyplňte název dokumentu.");
        var sources = Items(items);
        if (existingCode != null && sources.Count == 0) throw new InvalidOperationException("Vyberte přílohy k doplnění.");
        if (!Directory.Exists(p.DocumentsPath)) throw new InvalidOperationException("Složka dokumentů není dostupná: " + p.DocumentsPath);
        var created = new List<string>(); (string From, string To)? moved = null;
        try
        {
            return await Task.Run(() => EditAsync(p, async catalog =>
            {
                if (catalog.Warnings.Any(w => w.Contains("neúplné evidenční číslo"))) throw new InvalidOperationException("Registr obsahuje neúplná evidenční čísla.");
                var old = existingCode == null ? null : catalog.Records.SingleOrDefault(r => r.Code == existingCode) ?? throw new InvalidOperationException("Původní záznam už neexistuje.");
                var code = existingCode ?? ArchivePaths.NextCode(catalog.Records, draft.Category, ReservedCodes(p));
                var relative = "";
                if (sources.Count > 0)
                {
                    var copier = new Copier(sources, progress, ct);
                    var existing = old == null ? null : LocalPath(p, old);
                    if (existing == null && sources.Count == 1)
                    {
                        var source = sources[0]; var folder = Directory.Exists(source);
                        var target = Free(Path.Combine(p.DocumentsPath, folder ? code : code + Path.GetExtension(source)));
                        created.Add(target); await copier.CopyAsync(source, target);
                        relative = "/" + Path.GetFileName(target);
                    }
                    else
                    {
                        var folder = existing != null && Directory.Exists(existing) ? existing : Free(Path.Combine(p.DocumentsPath, code));
                        if (!Directory.Exists(folder)) { Directory.CreateDirectory(folder); created.Add(folder); }
                        // A document that was a single file becomes a folder holding it and the new items.
                        if (existing != null && File.Exists(existing)) { var to = Path.Combine(folder, Path.GetFileName(existing)); File.Move(existing, to); moved = (existing, to); }
                        foreach (var source in sources)
                        {
                            var name = ArchivePaths.SafeName(Path.GetFileName(source)); var target = Path.Combine(folder, name);
                            if (File.Exists(target) && File.Exists(source) && await SameAsync(source, target, ct)) continue;
                            for (var i = 2; File.Exists(target) || Directory.Exists(target); i++) target = Path.Combine(folder, Path.GetFileNameWithoutExtension(name) + $" ({i})" + Path.GetExtension(name));
                            created.Add(target); await copier.CopyAsync(source, target);
                        }
                        relative = "/" + Path.GetRelativePath(p.DocumentsPath, folder).Replace('\\', '/');
                    }
                }
                if (old != null) catalog.SetAttachments(code, relative, p.DocumentsPath);
                else catalog.Append(code, draft, relative, relative.Length > 0 ? p.DocumentsPath : "");
                return catalog.Records.Single(r => r.Code == code);
            }, ct), ct);
        }
        catch
        {
            // Undo only our own changes: first return a moved legacy file (it may sit in a created folder), then remove what this call created.
            try
            {
                if (moved is { } m) File.Move(m.To, m.From);
                foreach (var path in Enumerable.Reverse(created)) if (Directory.Exists(path)) Directory.Delete(path, true); else if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }
    static string Free(string path) => File.Exists(path) || Directory.Exists(path)
        ? throw new InvalidOperationException($"Ve složce dokumentů už existuje {Path.GetFileName(path)}, který registr neuvádí. Zkontrolujte jej.") : path;
    static readonly EnumerationOptions Tree = new() { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
    // Copies files and whole folders, reporting progress over all files of the operation.
    sealed class Copier(IReadOnlyList<string> sources, IProgress<string>? progress, CancellationToken ct)
    {
        readonly int total = sources.Sum(x => Directory.Exists(x) ? Directory.EnumerateFiles(x, "*", Tree).Count() : 1);
        int done;
        public async Task CopyAsync(string source, string target)
        {
            if (!Directory.Exists(source)) { await File(source, target); return; }
            Directory.CreateDirectory(target);
            foreach (var directory in Directory.EnumerateDirectories(source, "*", Tree))
                Directory.CreateDirectory(Path.Combine(target, Relative(source, directory)));
            foreach (var file in Directory.EnumerateFiles(source, "*", Tree))
                await File(file, Path.Combine(target, Relative(source, file)));
        }
        static string Relative(string root, string path) => Path.Combine(Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar).Select(ArchivePaths.SafeName).ToArray());
        async Task File(string source, string target)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Kopírování {++done}/{total}: {Path.GetFileName(source)}");
            await CopyFileAsync(source, target, ct);
        }
    }
    static async Task CopyFileAsync(string source, string target, CancellationToken ct)
    {
        try
        {
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, true);
            await input.CopyToAsync(output, ct);
        }
        catch { try { File.Delete(target); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } throw; }
    }
    static async Task<bool> SameAsync(string a, string b, CancellationToken ct)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        async Task<byte[]> Hash(string path) { await using var s = File.OpenRead(path); return await SHA256.HashDataAsync(s, ct); }
        var first = await Hash(a); var second = await Hash(b); return first.AsSpan().SequenceEqual(second);
    }
    static List<string> Items(IEnumerable<string> inputs)
    {
        var result = inputs.Select(x => Path.GetFullPath(x).TrimEnd(Path.DirectorySeparatorChar)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var item in result)
        {
            if (!File.Exists(item) && !Directory.Exists(item)) throw new InvalidOperationException("Příloha již není na disku: " + item);
            if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Zástupce nebo symbolický odkaz nelze přidat. Vyberte vlastní soubor nebo složku.");
            if (Directory.Exists(item) && !Directory.EnumerateFiles(item, "*", Tree).Any()) throw new InvalidOperationException($"Složka {Path.GetFileName(item)} neobsahuje žádné soubory.");
        }
        if (result.GroupBy(x => ArchivePaths.SafeName(Path.GetFileName(x)), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new InvalidOperationException("Přílohy mají shodný název. Přejmenujte je.");
        return result;
    }
    public Task SetPendingAsync(ArchiveProfile p, string code, bool pending, CancellationToken ct = default)
        => Task.Run(() => EditAsync(p, c => { c.SetPending(code, pending); return Task.FromResult(0); }, ct), ct);
    public string? LocalPath(ArchiveProfile p, DocumentRecord record)
    {
        try
        {
            var relative = record.RelativePath.Trim().TrimStart('/', '\\');
            if (relative.Length > 0)
            {
                var path = ArchivePaths.ResolveLocal(p.DocumentsPath, relative);
                if (File.Exists(path) || Directory.Exists(path)) return path;
            }
            if (Directory.Exists(p.DocumentsPath))
                return Directory.EnumerateFileSystemEntries(p.DocumentsPath, record.Code + "*").FirstOrDefault(x => Regex.IsMatch(Path.GetFileName(x), "^" + record.Code + "(?![0-9])"));
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException) { }
        return null;
    }
    // Optional: once the sync client has uploaded new folders, look up their Drive IDs and store them in Q.
    // Only the archive root and its documents folder are listed, a few requests per pass.
    public async Task<int> LinkDriveIdsAsync(ArchiveProfile p, IDriveClient client, CancellationToken ct = default)
    {
        if (!p.DriveLinked) return 0;
        var (catalog, cached) = await Task.Run(() => Load(p), ct);
        var missing = catalog.Records.Where(r => r.DriveId.Length == 0 && r.Electronic).ToList();
        if (missing.Count == 0 || cached) return 0;
        var drive = new ArchiveDriveClient(client, p.DriveRootId!);
        var documents = (await drive.ListAsync(p.DriveRootId!, ct)).Where(x => x.IsFolder && x.Name == p.DocumentsName).ToList();
        if (documents.Count != 1) throw new InvalidOperationException($"Ve složce Google Drive {(documents.Count == 0 ? "chybí" : "je vícekrát")} složka {p.DocumentsName}. Zkontrolujte odkaz na root archivu.");
        var children = (await drive.ListAsync(documents[0].Id, ct)).Where(x => x.MimeType != "application/vnd.google-apps.shortcut").ToList();
        var found = new Dictionary<string, string>();
        foreach (var record in missing)
        {
            var name = record.RelativePath.Trim().TrimStart('/');
            var matches = name.Length > 0 && !name.Contains('/') ? children.Where(x => x.Name == name).ToList() : [];
            if (matches.Count == 0) matches = children.Where(x => Regex.IsMatch(x.Name, "^" + record.Code + "(?![0-9])")).ToList();
            if (matches.Count == 1) found[record.Code] = matches[0].Id;
        }
        if (found.Count == 0) return 0;
        return await Task.Run(() => EditAsync(p, c =>
        {
            var changed = 0;
            foreach (var (code, id) in found) if (c.Records.Any(r => r.Code == code && r.DriveId.Length == 0)) { c.SetDriveId(code, id); changed++; }
            return Task.FromResult(changed);
        }, ct), ct);
    }
    public static async Task<string> VerifyDriveRootAsync(ArchiveProfile p, IDriveClient client, string rootId, CancellationToken ct = default)
    {
        var root = await client.GetAsync(rootId, ct);
        if (!root.IsFolder) throw new InvalidOperationException("Odkaz musí určovat složku.");
        var children = await new ArchiveDriveClient(client, rootId).ListAsync(rootId, ct);
        if (!children.Any(x => x.IsFolder && x.Name == p.DocumentsName)) throw new InvalidOperationException($"Složka Google Drive „{root.Name}“ neobsahuje složku {p.DocumentsName}. Vložte odkaz na složku, ve které leží registr {p.Name}.xlsx.");
        return root.Name;
    }
}
