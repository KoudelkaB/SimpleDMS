using System.Security.Cryptography;
using System.Text;

namespace SimpleDms.Core;

public sealed record ArchiveChoice(string Name, DriveItem? Workbook, DriveItem? Folder)
{
    public override string ToString() => Name + (Workbook == null ? " (nový registr)" : "") + (Folder != null && Folder.Name != Name ? " — složka: " + Folder.Name : "");
}
public sealed class AddJournal
{
    public string Operation { get; set; } = Guid.NewGuid().ToString("N");
    public string Code { get; set; } = "";
    public DocumentDraft Draft { get; set; } = new("", "");
    public List<string> Files { get; set; } = [];
    public Dictionary<string, string> FileHashes { get; set; } = [];
    public Dictionary<string, string> Uploaded { get; set; } = [];
    public Dictionary<string, string> RelativeFiles { get; set; } = [];
    public Dictionary<string, string> Subfolders { get; set; } = [];
    public string FolderId { get; set; } = "";
    public string FolderName { get; set; } = "";
    public string ExpectedHash { get; set; } = "";
    public bool Attach { get; set; }
    public bool Complete { get; set; }
}
public sealed record LocalEntry(string Id, string RelativePath, string Md5, string Version, bool Available);
public sealed class SyncManifest
{
    public Dictionary<string, LocalEntry> Entries { get; set; } = [];
    public DateTimeOffset? CompletedAt { get; set; }
    public List<string> Errors { get; set; } = [];
}
public sealed class ArchiveService(IDriveClient drive, LocalStore store)
{
    readonly SemaphoreSlim writer = new(1, 1);
    public async Task<IReadOnlyList<ArchiveChoice>> DiscoverAsync(string root, CancellationToken ct = default)
    {
        if (!(await drive.GetAsync(root, ct)).IsFolder) throw new InvalidOperationException("Odkaz musí určovat složku.");
        var children = await drive.ListAsync(root, ct);
        var workbooks = children.Where(x => x.Name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) && !x.IsFolder).ToList();
        var folders = children.Where(x => x.IsFolder).ToList();
        var choices = new List<ArchiveChoice>();
        foreach (var book in workbooks)
        {
            var name = book.Name[..^5];
            var matches = folders.Where(x => x.Name == name).ToList();
            // Existing archives may use underscores where their workbook uses spaces.
            if (matches.Count == 0) matches = folders.Where(x => x.Name.Replace('_', ' ') == name.Replace('_', ' ')).ToList();
            if (matches.Count == 0) choices.Add(new(name, book, null)); else choices.AddRange(matches.Select(f => new ArchiveChoice(name, book, f)));
        }
        if (workbooks.Count == 0) choices.AddRange(folders.Select(f => new ArchiveChoice(f.Name, null, f)));
        return choices;
    }
    public async Task<ArchiveProfile> OpenAsync(string root, ArchiveChoice choice, string account, string email, bool readOnly, CancellationToken ct = default)
    {
        var parent = await drive.GetAsync(root, ct);
        if (!parent.IsFolder) throw new InvalidOperationException("Kořen archivu musí být složka.");
        if (string.IsNullOrWhiteSpace(choice.Name) || choice.Name != ArchivePaths.SafeName(choice.Name)) throw new InvalidOperationException("Název nového archivu obsahuje nepovolené znaky.");
        var book = choice.Workbook; var folder = choice.Folder;
        if (book == null || folder == null)
        {
            var children = await drive.ListAsync(root, ct);
            book ??= children.SingleOrDefault(x => !x.IsFolder && x.Name == choice.Name + ".xlsx");
            folder ??= children.SingleOrDefault(x => x.IsFolder && x.Name == choice.Name);
        }
        if ((book == null || folder == null) && (readOnly || !parent.CanAddChildren)) throw new InvalidOperationException("Chybějící registr nebo složku může doplnit správce s oprávněním zápisu.");
        foreach (var item in new[] { book, folder }.OfType<DriveItem>()) if (!item.Parents.Contains(root)) throw new InvalidOperationException("Vybraná položka nepatří do rootu archivu.");
        var creation = "archive-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root + ":" + choice.Name)));
        byte[] bytes;
        if (book != null) { bytes = await drive.DownloadAsync(book.Id, ct); _ = new WorkbookCatalog(bytes); }
        else { bytes = WorkbookCatalog.Create(); book = await drive.UploadAsync(root, choice.Name + ".xlsx", bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", creation + "-book", ct); }
        folder ??= await drive.CreateFolderAsync(root, choice.Name, creation + "-documents", ct);
        var profile = new ArchiveProfile(root, book.Id, folder.Id, book.Name[..^5], account, email, !readOnly && book.CanEdit && folder.CanAddChildren, DocumentsName: folder.Name);
        await CacheAsync(profile, bytes, ct); return profile;
    }
    public string CachedWorkbook(ArchiveProfile p) => Path.Combine(store.ArchiveDirectory(p), "catalog.xlsx");
    string FileFor(ArchiveProfile p, string name) => Path.Combine(store.ArchiveDirectory(p), name);
    T? Read<T>(ArchiveProfile p, string name) => File.Exists(FileFor(p, name)) ? System.Text.Json.JsonSerializer.Deserialize<T>(File.ReadAllText(FileFor(p, name)), LocalStore.Json) : default;
    void Write<T>(ArchiveProfile p, string name, T value)
    {
        var path = FileFor(p, name); File.WriteAllText(path + ".tmp", System.Text.Json.JsonSerializer.Serialize(value, LocalStore.Json)); File.Move(path + ".tmp", path, true);
    }
    async Task CacheAsync(ArchiveProfile p, byte[] bytes, CancellationToken ct)
    { var path = CachedWorkbook(p); await File.WriteAllBytesAsync(path + ".tmp", bytes, ct); File.Move(path + ".tmp", path, true); }
    public WorkbookCatalog? LoadOffline(ArchiveProfile p)
    {
        if (p.LocalRoot != null && !p.ManagedCopy)
        {
            var path = ArchivePaths.ResolveLocal(p.LocalRoot, p.Name + ".xlsx");
            if (File.Exists(path)) return new WorkbookCatalog(File.ReadAllBytes(path));
        }
        return File.Exists(CachedWorkbook(p)) ? new WorkbookCatalog(File.ReadAllBytes(CachedWorkbook(p))) : null;
    }
    public async Task<(ArchiveProfile Profile, WorkbookCatalog Catalog)> RefreshAsync(ArchiveProfile p, CancellationToken ct = default)
    {
        var book = await drive.GetAsync(p.WorkbookId, ct); var folder = await drive.GetAsync(p.DocumentsId, ct);
        if (!book.Parents.Contains(p.RootId) || !folder.Parents.Contains(p.RootId) || !folder.IsFolder) throw new InvalidOperationException("Registr nebo složka už nepatří do zvoleného rootu.");
        var bytes = await drive.DownloadAsync(p.WorkbookId, ct); var catalog = new WorkbookCatalog(bytes);
        p = p with { Name = book.Name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ? book.Name[..^5] : p.Name, DocumentsName = folder.Name, CanWrite = book.CanEdit && folder.CanAddChildren };
        await CacheAsync(p, bytes, ct); return (p, catalog);
    }
    public AddJournal? PendingOperation(ArchiveProfile p) => Read<AddJournal>(p, "operation.json") is { Complete: false } journal ? journal : null;
    public async Task<DocumentRecord> AddAsync(ArchiveProfile p, DocumentDraft draft, IEnumerable<string> files, string? existingCode = null, CancellationToken ct = default)
    {
        if (!p.CanWrite) throw new InvalidOperationException("Archiv je otevřen jen pro čtení.");
        await writer.WaitAsync(ct);
        try
        {
            using var localLock = new FileStream(FileFor(p, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var journal = PendingOperation(p);
            if (journal == null)
            {
                var catalog = new WorkbookCatalog(await drive.DownloadAsync(p.WorkbookId, ct));
                if (catalog.Warnings.Any(w => w.Contains("neúplné evidenční číslo"))) throw new InvalidOperationException("Registr obsahuje neúplná evidenční čísla.");
                var reserved = Read<List<string>>(p, "reservations.json") ?? [];
                var code = existingCode ?? ArchivePaths.NextCode(catalog.Records, draft.Category, reserved);
                if (existingCode != null && !catalog.Records.Any(r => r.Code == code)) throw new InvalidOperationException("Původní záznam už neexistuje.");
                var expanded = ExpandFiles(files);
                var paths = expanded.Keys.ToList();
                if (paths.Any(x => !File.Exists(x))) throw new InvalidOperationException("Některá příloha již není na disku.");
                if (existingCode != null && paths.Count == 0) throw new InvalidOperationException("Vyberte přílohy k doplnění.");
                if (string.IsNullOrWhiteSpace(draft.Title)) throw new InvalidOperationException("Vyplňte název dokumentu.");
                journal = new() { Code = code, Draft = draft, Files = paths, RelativeFiles = expanded, Attach = existingCode != null, FolderName = code + "_" + ArchivePaths.SafeName(draft.Title) };
                foreach (var file in paths) journal.FileHashes[file] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file, ct)));
                if (existingCode == null) { reserved.Add(code); Write(p, "reservations.json", reserved); }
                Write(p, "operation.json", journal);
            }
            return await ContinueAdd(p, journal, ct);
        }
        finally { writer.Release(); }
    }
    async Task<DocumentRecord> ContinueAdd(ArchiveProfile p, AddJournal journal, CancellationToken ct)
    {
        var book = await drive.GetAsync(p.WorkbookId, ct); if (!book.CanEdit) throw new InvalidOperationException("Nemáte právo změnit XLSX.");
        var original = await drive.DownloadAsync(p.WorkbookId, ct);
        var catalog = new WorkbookCatalog(original);
        if (journal.ExpectedHash.Length > 0 && Convert.ToHexString(SHA256.HashData(original)) == journal.ExpectedHash)
        { journal.Complete = true; Write(p, "operation.json", journal); await CacheAsync(p, original, ct); return catalog.Records.Single(x => x.Code == journal.Code); }
        if (!journal.Attach && catalog.Records.Any(x => x.Code == journal.Code)) throw new InvalidOperationException("Rezervované číslo je již v cloudovém registru. Obnova vyžaduje kontrolu správce.");
        if (journal.Files.Count > 0)
        {
            var old = journal.Attach ? catalog.Records.Single(x => x.Code == journal.Code) : null;
            if (journal.FolderId.Length == 0)
            {
                if (old?.DriveId is { Length: > 0 } id)
                {
                    if (!await BelongsAsync(p, id, ct)) throw new InvalidOperationException("Původní příloha není v tomto archivu.");
                    var existing = await drive.GetAsync(id, ct);
                    if (existing.IsFolder) { journal.FolderId = id; journal.FolderName = existing.Name; }
                }
                if (journal.FolderId.Length == 0) journal.FolderId = (await drive.CreateFolderAsync(p.DocumentsId, journal.FolderName, journal.Operation + "-folder", ct)).Id;
                Write(p, "operation.json", journal);
            }
            if (old?.DriveId is { Length: > 0 } oldId && oldId != journal.FolderId)
                await drive.MoveAsync(oldId, journal.FolderId, ct);
            foreach (var path in journal.Files)
            {
                if (journal.Uploaded.ContainsKey(path)) continue;
                var bytes = await File.ReadAllBytesAsync(path, ct);
                if (Convert.ToHexString(SHA256.HashData(bytes)) != journal.FileHashes[path]) throw new InvalidOperationException("Příloha se po rezervaci změnila. Vraťte původní soubor před obnovením.");
                var relative = journal.RelativeFiles.GetValueOrDefault(path) ?? Path.GetFileName(path);
                var target = journal.FolderId; var accumulated = "";
                foreach (var segment in relative.Split('/')[..^1])
                {
                    accumulated += segment + "/";
                    if (!journal.Subfolders.TryGetValue(accumulated, out var folderId))
                    {
                        folderId = (await drive.CreateFolderAsync(target, segment, journal.Operation + "-dir-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accumulated))), ct)).Id;
                        journal.Subfolders[accumulated] = folderId; Write(p, "operation.json", journal);
                    }
                    target = folderId;
                }
                var item = await drive.UploadAsync(target, relative.Split('/')[^1], bytes, "application/octet-stream", journal.Operation + "-" + journal.FileHashes[path] + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..12], ct);
                journal.Uploaded[path] = item.Id; Write(p, "operation.json", journal);
            }
        }
        var url = journal.FolderId.Length > 0 ? "https://drive.google.com/drive/folders/" + journal.FolderId : "";
        if (journal.Attach) catalog.SetAttachments(journal.Code, journal.FolderName, url, journal.FolderId);
        else catalog.Append(journal.Code, journal.Draft, journal.Files.Count > 0 ? journal.FolderName : "", url, journal.FolderId);
        var updated = catalog.Save(); _ = new WorkbookCatalog(updated);
        journal.ExpectedHash = Convert.ToHexString(SHA256.HashData(updated)); Write(p, "operation.json", journal);
        await Publish(p, book.Version, original, updated, ct);
        journal.Complete = true; Write(p, "operation.json", journal); await CacheAsync(p, updated, ct);
        return new WorkbookCatalog(updated).Records.Single(x => x.Code == journal.Code);
    }
    static Dictionary<string, string> ExpandFiles(IEnumerable<string> inputs)
    {
        var result = new Dictionary<string, string>();
        void Add(string file, string relative)
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Zástupce nebo symbolický odkaz nelze přidat. Vyberte vlastní soubor.");
            relative = string.Join('/', relative.Replace('\\', '/').Split('/').Select(ArchivePaths.SafeName));
            if (result.Any(x => x.Key != file && x.Value.Equals(relative, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Přílohy mají shodný název. Přejmenujte je nebo přidejte jejich nadřazené složky.");
            result[file] = relative;
        }
        foreach (var input in inputs.Select(Path.GetFullPath).Distinct())
        {
            if (!Directory.Exists(input)) { Add(input, Path.GetFileName(input)); continue; }
            if ((File.GetAttributes(input) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Vyberte vlastní složku, nikoli symbolický odkaz.");
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
            var children = Directory.EnumerateFiles(input, "*", options).ToList();
            if (children.Count == 0) throw new InvalidOperationException("Složka neobsahuje žádné běžné soubory.");
            foreach (var file in children) Add(file, Path.GetFileName(input) + "/" + Path.GetRelativePath(input, file));
        }
        return result;
    }
    async Task Publish(ArchiveProfile p, string version, byte[] original, byte[] updated, CancellationToken ct)
    {
        var backup = Path.Combine(store.ArchiveDirectory(p), "backups"); Directory.CreateDirectory(backup);
        await File.WriteAllBytesAsync(Path.Combine(backup, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + ".xlsx"), original, ct);
        if ((await drive.GetAsync(p.WorkbookId, ct)).Version != version) throw new InvalidOperationException("Cloudový XLSX se během operace změnil. Vaše rezervace a přílohy jsou uložené; obnovte operaci nad novou verzí.");
        await drive.ReplaceAsync(p.WorkbookId, updated, ct);
    }
    public async Task SetPendingAsync(ArchiveProfile p, string code, bool pending, CancellationToken ct = default) => await Edit(p, c => c.SetPending(code, pending), ct);
    async Task Edit(ArchiveProfile p, Action<WorkbookCatalog> mutate, CancellationToken ct)
    {
        if (!p.CanWrite) throw new InvalidOperationException("Archiv je otevřen jen pro čtení.");
        await writer.WaitAsync(ct);
        try
        {
            using var localLock = new FileStream(FileFor(p, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (PendingOperation(p) != null) throw new InvalidOperationException("Nejprve obnovte rozpracované přidání.");
            var meta = await drive.GetAsync(p.WorkbookId, ct); var original = await drive.DownloadAsync(p.WorkbookId, ct);
            var catalog = new WorkbookCatalog(original); mutate(catalog); var updated = catalog.Save(); _ = new WorkbookCatalog(updated);
            await Publish(p, meta.Version, original, updated, ct); await CacheAsync(p, updated, ct);
        }
        finally { writer.Release(); }
    }
    public async Task<bool> BelongsAsync(ArchiveProfile p, string id, CancellationToken ct = default)
    {
        var queue = new Queue<string>(); queue.Enqueue(id); var seen = new HashSet<string>();
        while (queue.Count > 0 && seen.Count < 100)
        { var current = queue.Dequeue(); if (current == p.DocumentsId) return true; if (!seen.Add(current)) continue; foreach (var parent in (await drive.GetAsync(current, ct)).Parents) queue.Enqueue(parent); }
        return false;
    }
    public async Task<int> RepairIdsAsync(ArchiveProfile p, CancellationToken ct = default)
    {
        var catalog = await RefreshAsync(p, ct); var candidates = new Dictionary<string, string>();
        foreach (var record in catalog.Catalog.Records.Where(r => r.DriveId.Length == 0 && r.Electronic))
        {
            string? id = null;
            var match = System.Text.RegularExpressions.Regex.Match(record.DriveUrl, @"(?:/d/|/folders/|[?&]id=)([A-Za-z0-9_-]+)");
            if (match.Success) id = match.Groups[1].Value;
            if (id != null)
            {
                try { if (await BelongsAsync(p, id, ct)) candidates[record.Code] = id; }
                catch (DriveException e) when (e.Status is 403 or 404) { /* Deleted or inaccessible legacy links remain for manual correction. */ }
            }
        }
        if (candidates.Count > 0) await Edit(p, c => { foreach (var item in candidates) c.SetDriveId(item.Key, item.Value); }, ct);
        return candidates.Count;
    }
    public async Task SynchronizeAsync(ArchiveProfile p, IProgress<string>? progress, CancellationToken ct = default)
    {
        if (p.LocalRoot == null || !p.ManagedCopy) throw new InvalidOperationException("Vyberte samostatnou složku pro kopii spravovanou SimpleDMS.");
        var manifest = Read<SyncManifest>(p, "manifest.json") ?? new(); manifest.CompletedAt = null; manifest.Errors.Clear();
        Write(p, "manifest.json", manifest);
        var refreshed = await RefreshAsync(p, ct); p = refreshed.Profile;
        var workbookPath = ArchivePaths.ResolveLocal(p.LocalRoot!, p.Name + ".xlsx"); Directory.CreateDirectory(p.LocalRoot!);
        await File.WriteAllBytesAsync(workbookPath + ".part", await File.ReadAllBytesAsync(CachedWorkbook(p), ct), ct); File.Move(workbookPath + ".part", workbookPath, true);
        var queue = new Queue<(string Id, string Path)>(); queue.Enqueue((p.DocumentsId, ArchivePaths.SafeName(p.DocumentsName ?? p.Name)));
        var visited = new HashSet<string>(); var seenItems = new HashSet<string>(); int count = 0;
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested(); var (id, path) = queue.Dequeue(); if (!visited.Add(id)) continue;
            Directory.CreateDirectory(ArchivePaths.ResolveLocal(p.LocalRoot!, path)); manifest.Entries[id] = new(id, path, "", "", true);
            seenItems.Add(id);
            var children = await drive.ListAsync(id, ct);
            foreach (var child in children)
            {
                ct.ThrowIfCancellationRequested();
                seenItems.Add(child.Id);
                var name = ArchivePaths.SafeName(child.Name);
                if (children.Count(x => ArchivePaths.SafeName(x.Name).Equals(name, StringComparison.OrdinalIgnoreCase)) > 1) name = Path.GetFileNameWithoutExtension(name) + "_" + child.Id + Path.GetExtension(name);
                var relative = path + "/" + name;
                if (child.IsFolder) { queue.Enqueue((child.Id, relative)); continue; }
                if (child.MimeType == "application/vnd.google-apps.shortcut") { manifest.Errors.Add(child.Name + ": zástupce vyžaduje místní přípravu cíle."); continue; }
                if (child.MimeType.StartsWith("application/vnd.google-apps.")) relative += child.MimeType.EndsWith("spreadsheet") ? ".xlsx" : ".pdf";
                var local = ArchivePaths.ResolveLocal(p.LocalRoot!, relative);
                progress?.Report($"Offline kopie: {++count}. {child.Name}");
                try
                {
                    if (manifest.Entries.TryGetValue(child.Id, out var entry) && entry.Version == child.Version && entry.RelativePath == relative && File.Exists(local))
                    {
                        if (entry.Md5.Length == 0 || Convert.ToHexString(MD5.HashData(await File.ReadAllBytesAsync(local, ct))).Equals(entry.Md5, StringComparison.OrdinalIgnoreCase)) continue;
                    }
                    var data = await drive.DownloadAsync(child.Id, ct);
                    if (child.Md5.Length > 0 && !Convert.ToHexString(MD5.HashData(data)).Equals(child.Md5, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Kontrolní součet nesouhlasí.");
                    if (File.Exists(local))
                    {
                        var existing = await File.ReadAllBytesAsync(local, ct);
                        if (!manifest.Entries.TryGetValue(child.Id, out var prior) || prior.Md5.Length == 0 || !Convert.ToHexString(MD5.HashData(existing)).Equals(prior.Md5, StringComparison.OrdinalIgnoreCase))
                            File.Copy(local, local + ".local-backup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), true);
                    }
                    await File.WriteAllBytesAsync(local + ".part", data, ct); File.Move(local + ".part", local, true);
                    manifest.Entries[child.Id] = new(child.Id, relative, Convert.ToHexString(MD5.HashData(data)), child.Version, true);
                }
                catch (Exception e) when (e is not OperationCanceledException) { manifest.Errors.Add(child.Name + ": " + e.Message); }
                Write(p, "manifest.json", manifest);
            }
        }
        foreach (var stale in manifest.Entries.Keys.Where(id => !seenItems.Contains(id)).ToList()) manifest.Entries.Remove(stale);
        if (manifest.Errors.Count == 0) manifest.CompletedAt = DateTimeOffset.UtcNow;
        Write(p, "manifest.json", manifest);
        progress?.Report(manifest.Errors.Count == 0 ? "Offline kopie byla aktualizována." : $"Offline kopie má {manifest.Errors.Count} chyb. Znovu spusťte aktualizaci.");
    }
    public async Task IndexLocalAsync(ArchiveProfile p, CancellationToken ct = default)
    {
        if (p.LocalRoot == null || p.ManagedCopy) return;
        var manifest = new SyncManifest(); var queue = new Queue<(string Id, string Path)>();
        var wanted=LoadOffline(p)?.Records.Where(r=>r.DriveId.Length>0).Select(r=>r.DriveId).ToHashSet();
        queue.Enqueue((p.DocumentsId, p.DocumentsName ?? p.Name)); var visited = new HashSet<string>();
        while (queue.Count > 0 && (wanted==null||wanted.Count>0||visited.Count==0))
        {
            var (id, path) = queue.Dequeue(); if (!visited.Add(id)) continue;
            var local = ArchivePaths.ResolveLocal(p.LocalRoot, path);
            manifest.Entries[id] = new(id, path, "", "", Directory.Exists(local));
            foreach (var child in await drive.ListAsync(id, ct))
            {
                // External clients retain Drive filenames; ambiguous duplicates need manual selection.
                var relative = path + "/" + child.Name;
                try
                {
                    var target = ArchivePaths.ResolveLocal(p.LocalRoot, relative);
                    manifest.Entries[child.Id] = new(child.Id, relative, child.Md5, child.Version, File.Exists(target) || Directory.Exists(target));
                    wanted?.Remove(child.Id);
                    if (child.IsFolder) queue.Enqueue((child.Id, relative));
                }
                catch (InvalidOperationException) { manifest.Errors.Add("Nelze mapovat místní název: " + child.Name); }
            }
        }
        manifest.CompletedAt = DateTimeOffset.UtcNow; Write(p, "manifest.json", manifest);
    }
    public async Task<string?> LocalPathAsync(ArchiveProfile p, DocumentRecord record, bool online, CancellationToken ct = default)
    {
        if (p.LocalRoot == null) return null;
        var manifest = Read<SyncManifest>(p, "manifest.json") ?? new();
        if (manifest.Entries.TryGetValue(record.DriveId, out var entry))
        { var path = ArchivePaths.ResolveLocal(p.LocalRoot, entry.RelativePath); if (File.Exists(path) || Directory.Exists(path)) return path; }
        if (!p.ManagedCopy && online && record.DriveId.Length > 0)
        {
            var segments = new List<string>(); var current = record.DriveId; var visited = new HashSet<string>();
            while (current != p.DocumentsId && visited.Add(current) && visited.Count < 100)
            {
                var item = await drive.GetAsync(current, ct); segments.Insert(0, item.Name);
                if (item.Parents.Length != 1) return null; current = item.Parents[0];
            }
            if (current == p.DocumentsId)
            {
                segments.Insert(0, p.DocumentsName ?? p.Name); var relative = string.Join('/', segments);
                var path = ArchivePaths.ResolveLocal(p.LocalRoot!, relative);
                manifest.Entries[record.DriveId] = new(record.DriveId, relative, "", "", File.Exists(path) || Directory.Exists(path)); Write(p, "manifest.json", manifest);
                return File.Exists(path) || Directory.Exists(path) ? path : null;
            }
        }
        if (!p.ManagedCopy && record.RelativePath.Length > 0)
        {
            var relative = (p.DocumentsName ?? p.Name) + "/" + record.RelativePath.TrimStart('/');
            try { var path = ArchivePaths.ResolveLocal(p.LocalRoot!, relative); if (File.Exists(path) || Directory.Exists(path)) return path; } catch (InvalidOperationException) { }
        }
        return null;
    }
}
