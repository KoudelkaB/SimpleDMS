using System.Security.Cryptography;
using SimpleDms.Core;
using Xunit;

namespace SimpleDms.Tests;

public sealed class FakeDrive : IDriveClient
{
    public Dictionary<string, DriveItem> Items = [];
    public Dictionary<string, byte[]> Data = [];
    readonly Dictionary<string, string> operations = [];
    public bool FailNextReplace, ChangeDuringUpload;
    public int FolderCreations, Uploads;
    public FakeDrive() { Items["root"] = new("root", "Archiv", "application/vnd.google-apps.folder", "1", "", [], true, true); Items["docs"] = new("docs", "Archiv", "application/vnd.google-apps.folder", "1", "", ["root"], true, true); Items["book"] = new("book", "Archiv.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "1", "", ["root"], true, false); Data["book"] = WorkbookCatalog.Create(); }
    public Task<DriveItem> GetAsync(string id, CancellationToken ct = default) => Task.FromResult(Items[id]);
    public Task<IReadOnlyList<DriveItem>> ListAsync(string parent, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DriveItem>>(Items.Values.Where(x => x.Parents.Contains(parent)).ToList());
    public Task<byte[]> DownloadAsync(string id, CancellationToken ct = default) => Task.FromResult(Data[id]);
    public Task<DriveItem> CreateFolderAsync(string parent, string name, string operation, CancellationToken ct = default)
    { if (operations.TryGetValue(operation, out var id)) return GetAsync(id, ct); id = Guid.NewGuid().ToString("N"); var item = new DriveItem(id, name, "application/vnd.google-apps.folder", "1", "", [parent], true, true); Items[id] = item; operations[operation] = id; FolderCreations++; return Task.FromResult(item); }
    public Task<DriveItem> UploadAsync(string parent, string name, byte[] bytes, string mime, string operation, CancellationToken ct = default)
    { if (operations.TryGetValue(operation, out var id)) return GetAsync(id, ct); id = Guid.NewGuid().ToString("N"); var item = new DriveItem(id, name, mime, "1", Convert.ToHexString(MD5.HashData(bytes)), [parent], true, false); Items[id] = item; Data[id] = bytes; operations[operation] = id; Uploads++; if (ChangeDuringUpload) { Items["book"] = Items["book"] with { Version = "2" }; ChangeDuringUpload = false; } return Task.FromResult(item); }
    public Task<DriveItem> ReplaceAsync(string id, byte[] bytes, CancellationToken ct = default)
    { if (FailNextReplace) { FailNextReplace = false; throw new IOException("Výpadek sítě"); } Data[id] = bytes; Items[id] = Items[id] with { Version = (int.Parse(Items[id].Version) + 1).ToString() }; return GetAsync(id, ct); }
    public Task<DriveItem> MoveAsync(string id, string parent, CancellationToken ct = default) { Items[id] = Items[id] with { Parents = [parent] }; return GetAsync(id, ct); }
}
public sealed class ArchiveTests
{
    [Fact]
    public async Task FolderAttachmentsRetainNestedStructure()
    {
        var drive = new FakeDrive(); var store = Store(); var service = new ArchiveService(drive, store);
        var folder = Path.Combine(store.Root, "scans"); Directory.CreateDirectory(Path.Combine(folder, "annex"));
        await File.WriteAllTextAsync(Path.Combine(folder, "annex", "page.pdf"), "nested");
        var record = await service.AddAsync(Profile(), new("10", "Folder"), [folder]);
        var scans = Assert.Single(await drive.ListAsync(record.DriveId)); Assert.Equal("scans", scans.Name);
        var annex = Assert.Single(await drive.ListAsync(scans.Id)); Assert.Equal("annex", annex.Name);
        var page = Assert.Single(await drive.ListAsync(annex.Id)); Assert.Equal("page.pdf", page.Name);
        Assert.Equal("nested", System.Text.Encoding.UTF8.GetString(await drive.DownloadAsync(page.Id)));
    }
    [Fact]
    public async Task IndexingExternalCopyMapsAllRecordsBeforeOfflineUse()
    {
        var drive = new FakeDrive(); var store = Store(); var service = new ArchiveService(drive, store);
        var folder = await drive.CreateFolderAsync("docs", "Legacy scanned folder", "legacy");
        var p = Profile() with { LocalRoot = Path.Combine(store.Root, "external") };
        var path = Path.Combine(p.LocalRoot!, p.DocumentsName ?? p.Name, folder.Name); Directory.CreateDirectory(path);
        await service.IndexLocalAsync(p);
        var r = new DocumentRecord(2, "100001", "Legacy", "", "", "", true, "wrong legacy path", "", "", "", folder.Id, false);
        Assert.Equal(path, await service.LocalPathAsync(p, r, false));
    }
    static LocalStore Store() => new(Path.Combine(Path.GetTempPath(), "simpledms-test-" + Guid.NewGuid().ToString("N")));
    static ArchiveProfile Profile() => new("root", "book", "docs", "Archiv", "user", "reader@example.test", true);
    [Fact]
    public async Task FirstOpenUsesPairedFilesAndNoWrites()
    { var drive = new FakeDrive(); var service = new ArchiveService(drive, Store()); var choice = Assert.Single(await service.DiscoverAsync("root")); var p = await service.OpenAsync("root", choice, "u", "u@example.test", true); Assert.Equal("Archiv", p.Name); Assert.False(p.CanWrite); Assert.Equal(0, drive.Uploads); Assert.Equal(0, drive.FolderCreations); }
    [Fact]
    public async Task MissingArchiveCanBeCreatedOnceAndReaderCannotCreate()
    { var drive = new FakeDrive(); drive.Items.Remove("book"); drive.Items.Remove("docs"); var service = new ArchiveService(drive, Store()); await Assert.ThrowsAsync<InvalidOperationException>(() => service.OpenAsync("root", new("Nový", null, null), "u", "e", true)); var p = await service.OpenAsync("root", new("Nový", null, null), "u", "e", false); Assert.Empty(new WorkbookCatalog(drive.Data[p.WorkbookId]).Records); await service.OpenAsync("root", new("Nový", null, null), "u", "e", false); Assert.Equal(1, drive.Uploads); Assert.Equal(1, drive.FolderCreations); }
    [Fact]
    public async Task InterruptedAddResumesWithSameNumberAndIds()
    {
        var drive = new FakeDrive { FailNextReplace = true }; var store = Store(); var service = new ArchiveService(drive, store); var file = Path.Combine(store.Root, "scan.pdf"); await File.WriteAllTextAsync(file, "scan"); var p = Profile();
        await Assert.ThrowsAsync<IOException>(() => service.AddAsync(p, new("10", "Dokument"), [file])); var journal = service.PendingOperation(p)!; Assert.Equal("100001", journal.Code);
        var record = await service.AddAsync(p, journal.Draft, journal.Files); Assert.Equal("100001", record.Code); Assert.Equal(journal.FolderId, record.DriveId); Assert.Equal(1, drive.FolderCreations); Assert.Equal(1, drive.Uploads); Assert.Null(service.PendingOperation(p)); Assert.Single(new WorkbookCatalog(drive.Data["book"]).Records);
        Assert.True(Directory.EnumerateFiles(Path.Combine(store.ArchiveDirectory(p), "backups")).Any());
    }
    [Fact]
    public async Task ConcurrentWorkbookChangePreventsOverwrite()
    { var drive = new FakeDrive { ChangeDuringUpload = true }; var store = Store(); var service = new ArchiveService(drive, store); var file = Path.Combine(store.Root, "scan.pdf"); await File.WriteAllTextAsync(file, "scan"); await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddAsync(Profile(), new("10", "Test"), [file])); Assert.Empty(new WorkbookCatalog(drive.Data["book"]).Records); Assert.NotNull(service.PendingOperation(Profile())); var op = service.PendingOperation(Profile())!; await service.AddAsync(Profile(), op.Draft, op.Files); Assert.Single(new WorkbookCatalog(drive.Data["book"]).Records); Assert.Equal(1, drive.Uploads); }
    [Fact]
    public async Task PaperRecordCanReceiveAttachmentWithoutNewNumber()
    { var drive = new FakeDrive(); var store = Store(); var service = new ArchiveService(drive, store); var p = Profile(); var r = await service.AddAsync(p, new("10", "Papír"), []); Assert.Equal("", r.DriveId); var file = Path.Combine(store.Root, "scan.pdf"); await File.WriteAllTextAsync(file, "scan"); var withFile = await service.AddAsync(p, new("10", "Papír"), [file], r.Code); Assert.Equal(r.Code, withFile.Code); Assert.True(withFile.Electronic); Assert.Single(new WorkbookCatalog(drive.Data["book"]).Records); }
    [Fact]
    public async Task ManagedCopyDownloadsAndReadsOffline()
    { var drive = new FakeDrive(); var store = Store(); var service = new ArchiveService(drive, store); var p = Profile(); var input = Path.Combine(store.Root, "source.pdf"); await File.WriteAllTextAsync(input, "offline"); var r = await service.AddAsync(p, new("10", "Test"), [input]); p = p with { LocalRoot = Path.Combine(store.Root, "offline"), ManagedCopy = true }; await service.SynchronizeAsync(p, null); var path = await service.LocalPathAsync(p, r, false); Assert.NotNull(path); Assert.True(Directory.Exists(path)); Assert.Equal("offline", await File.ReadAllTextAsync(Directory.EnumerateFiles(path!).Single())); Assert.Single(service.LoadOffline(p)!.Records); }
}
