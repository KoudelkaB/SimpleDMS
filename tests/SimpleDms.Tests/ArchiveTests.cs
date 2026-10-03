using SimpleDms.Core;
using Xunit;

namespace SimpleDms.Tests;

public sealed class FakeDrive : IDriveClient
{
    public Dictionary<string, DriveItem> Items = [];
    public List<string> MetadataReads = [], Listings = [];
    public FakeDrive()
    {
        Items["root"] = new("root", "Databáze", "application/vnd.google-apps.folder", []);
        Items["docs"] = new("docs", "Archiv", "application/vnd.google-apps.folder", ["root"]);
        Items["book"] = new("book", "Archiv.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ["root"]);
    }
    public DriveItem Add(string id, string name, string parent, bool folder = true) => Items[id] = new(id, name, folder ? "application/vnd.google-apps.folder" : "application/pdf", [parent]);
    public Task<DriveItem> GetAsync(string id, CancellationToken ct = default) { MetadataReads.Add(id); return Task.FromResult(Items[id]); }
    public Task<IReadOnlyList<DriveItem>> ListAsync(string parent, CancellationToken ct = default) { Listings.Add(parent); return Task.FromResult<IReadOnlyList<DriveItem>>(Items.Values.Where(x => x.Parents.Contains(parent)).ToList()); }
}
public sealed class ArchiveTests
{
    static string Temp() { var dir = Path.Combine(Path.GetTempPath(), "simpledms-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); return dir; }
    static (ArchiveService Service, ArchiveProfile Profile, string Dir) Archive()
    {
        var dir = Temp(); var service = new ArchiveService(new LocalStore(Path.Combine(dir, "app")));
        Directory.CreateDirectory(Path.Combine(dir, "drive"));
        return (service, service.Open(Path.Combine(dir, "drive"), new("Archiv", null, null), true), dir);
    }
    static string File(string dir, string name, string content) { var path = Path.Combine(dir, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); System.IO.File.WriteAllText(path, content); return path; }
    [Fact]
    public void DiscoverPairsWorkbookWithUnderscoreFolderAndIgnoresExcelLock()
    {
        var root = Temp(); File(root, "Dokumenty KS.xlsx", ""); File(root, "~$Dokumenty KS.xlsx", ""); Directory.CreateDirectory(Path.Combine(root, "Dokumenty_KS"));
        var choice = Assert.Single(ArchiveService.Discover(root)); Assert.Equal("Dokumenty KS", choice.Name); Assert.Equal("Dokumenty_KS", choice.Folder);
    }
    [Fact]
    public void ReaderCannotCreateMissingArchiveAndOpeningTwiceKeepsRegister()
    {
        var root = Temp(); var service = new ArchiveService(new LocalStore(Path.Combine(root, "app")));
        Assert.Throws<InvalidOperationException>(() => service.Open(root, new("Nový", null, null), false));
        var p = service.Open(root, new("Nový", null, null), true); Assert.True(Directory.Exists(p.DocumentsPath));
        var bytes = System.IO.File.ReadAllBytes(p.WorkbookPath); service.Open(root, new("Nový", "Nový", "Nový"), false);
        Assert.Equal(bytes, System.IO.File.ReadAllBytes(p.WorkbookPath));
    }
    [Fact]
    public async Task AddCopiesFilesAndFoldersIntoNumberedDocumentFolder()
    {
        var (service, p, dir) = Archive();
        var source = Path.Combine(dir, "scans"); File(source, "annex/page.pdf", "nested"); var single = File(dir, "cover.pdf", "cover");
        var record = await service.AddAsync(p, new("10", "Smlouva: nájem", Validity: "1.2.2030"), [source, single]);
        Assert.Equal("100001", record.Code); Assert.Equal("/100001_Smlouva_ nájem", record.RelativePath); Assert.True(record.Electronic); Assert.Equal("1.2.2030", record.Validity);
        var folder = service.LocalPath(p, record)!;
        Assert.Equal("nested", System.IO.File.ReadAllText(Path.Combine(folder, "scans", "annex", "page.pdf")));
        Assert.Equal("cover", System.IO.File.ReadAllText(Path.Combine(folder, "cover.pdf")));
        Assert.True(System.IO.File.Exists(source + "/annex/page.pdf"));
        Assert.Single(service.Load(p).Catalog.Records);
    }
    [Fact]
    public async Task NumberingSkipsFoldersNotYetInRegister()
    {
        var (service, p, _) = Archive(); Directory.CreateDirectory(Path.Combine(p.DocumentsPath, "100007_ručně"));
        Assert.Equal("100008", (await service.AddAsync(p, new("10", "Papír"), [])).Code);
    }
    [Fact]
    public async Task PaperRecordReceivesAttachmentsWithoutNewNumber()
    {
        var (service, p, dir) = Archive();
        var paper = await service.AddAsync(p, new("10", "Papír"), []); Assert.False(paper.Electronic); Assert.Equal("", paper.RelativePath);
        var withFile = await service.AddAsync(p, new("10", "Papír"), [File(dir, "scan.pdf", "scan")], paper.Code);
        Assert.Equal(paper.Code, withFile.Code); Assert.True(withFile.Electronic); Assert.Equal("/100001_Papír", withFile.RelativePath);
        var again = await service.AddAsync(p, new("10", "Papír"), [File(dir, "other/scan.pdf", "second")], paper.Code);
        Assert.Equal(withFile.RelativePath, again.RelativePath);
        Assert.Equal(["scan (2).pdf", "scan.pdf"], Directory.EnumerateFiles(service.LocalPath(p, again)!).Select(Path.GetFileName).Order());
        Assert.Single(service.Load(p).Catalog.Records);
    }
    [Fact]
    public async Task LegacyFileAttachmentMovesIntoNewDocumentFolder()
    {
        var (service, p, dir) = Archive();
        var bytes = System.IO.File.ReadAllBytes(p.WorkbookPath); var c = new WorkbookCatalog(bytes); c.Append("100001", new("10", "Starý"), "/100001.pdf"); System.IO.File.WriteAllBytes(p.WorkbookPath, c.Save());
        File(p.DocumentsPath, "100001.pdf", "legacy");
        var record = await service.AddAsync(p, new("10", "Starý"), [File(dir, "scan.pdf", "scan")], "100001");
        Assert.Equal("/100001_Starý", record.RelativePath);
        Assert.Equal(["100001.pdf", "scan.pdf"], Directory.EnumerateFiles(service.LocalPath(p, record)!).Select(Path.GetFileName).Order());
    }
    [Fact]
    public async Task OpenRegisterBlocksWriteWithoutLeavingFolder()
    {
        var (service, p, dir) = Archive(); var file = File(dir, "scan.pdf", "scan");
        using (new FileStream(p.WorkbookPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddAsync(p, new("10", "Test"), [file]));
            Assert.Contains("Excel", error.Message);
            // Reading still works while another application holds the register.
            Assert.Empty(service.Load(p).Catalog.Records);
        }
        Assert.Empty(Directory.EnumerateFileSystemEntries(p.DocumentsPath));
        Assert.Equal("100001", (await service.AddAsync(p, new("10", "Test"), [file])).Code);
    }
    [Fact]
    public async Task EveryWriteKeepsBackupAndPendingToggles()
    {
        var (service, p, dir) = Archive();
        var record = await service.AddAsync(p, new("10", "Rozpracovaný", Pending: true), []); Assert.True(record.Pending);
        await service.SetPendingAsync(p, record.Code, false); Assert.False(Assert.Single(service.Load(p).Catalog.Records).Pending);
        Assert.Equal(2, Directory.EnumerateFiles(Path.Combine(dir, "app", p.Key, "backups")).Count());
    }
    [Fact]
    public async Task UnavailableFolderFallsBackToCachedRegister()
    {
        var (service, p, _) = Archive(); await service.AddAsync(p, new("10", "Test"), []);
        var offline = p with { Root = Path.Combine(p.Root, "disconnected") };
        System.IO.File.Copy(service.CachedWorkbook(p), service.CachedWorkbook(offline));
        var (catalog, cached) = service.Load(offline); Assert.True(cached); Assert.Single(catalog.Records);
    }
    [Fact]
    public async Task LinkingFillsDriveIdsOfUploadedFolders()
    {
        var (service, p, dir) = Archive(); var drive = new FakeDrive();
        var first = await service.AddAsync(p, new("10", "První"), [File(dir, "a.pdf", "a")]);
        var second = await service.AddAsync(p, new("10", "Druhý"), [File(dir, "b.pdf", "b")]);
        await service.AddAsync(p, new("10", "Papír"), []);
        drive.Add("f1", first.RelativePath.TrimStart('/'), "docs");
        p = p with { DriveRootId = "root", AccountId = "u", AccountEmail = "u@example.test" };
        Assert.Equal(1, await service.LinkDriveIdsAsync(p, drive));
        var records = service.Load(p).Catalog.Records;
        Assert.Equal("https://drive.google.com/open?id=f1", records.Single(r => r.Code == first.Code).DriveUrl);
        Assert.Equal("", records.Single(r => r.Code == second.Code).DriveId);
        drive.Add("f2", second.RelativePath.TrimStart('/'), "docs");
        Assert.Equal(1, await service.LinkDriveIdsAsync(p, drive)); Assert.Equal(0, await service.LinkDriveIdsAsync(p, drive));
    }
}
