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
    public async Task SingleFileIsStoredAsCodeNamedFile()
    {
        var (service, p, dir) = Archive(); var scan = File(dir, "Sken smlouvy.PDF", "scan");
        var record = await service.AddAsync(p, new("10", "Smlouva: nájem", Validity: "1.2.2030"), [scan]);
        Assert.Equal("100001", record.Code); Assert.Equal("/100001.PDF", record.RelativePath); Assert.True(record.Electronic); Assert.Equal("1.2.2030", record.Validity);
        Assert.Equal("scan", System.IO.File.ReadAllText(Path.Combine(p.DocumentsPath, "100001.PDF")));
        Assert.Equal(Path.Combine(p.DocumentsPath, "100001.PDF"), service.LocalPath(p, record));
        Assert.True(System.IO.File.Exists(scan));
    }
    [Fact]
    public async Task SeveralItemsGoToCodeNamedFolder()
    {
        var (service, p, dir) = Archive(); var a = File(dir, "a.pdf", "a"); var b = File(dir, "b.pdf", "b");
        var record = await service.AddAsync(p, new("10", "Dvě přílohy"), [a, b]);
        Assert.Equal("/100001", record.RelativePath);
        Assert.Equal(["a.pdf", "b.pdf"], Directory.EnumerateFiles(Path.Combine(p.DocumentsPath, "100001")).Select(Path.GetFileName).Order());
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(p.DocumentsPath, "100001")));
    }
    [Fact]
    public async Task SingleFolderBecomesCodeFolderWithItsSubfolders()
    {
        var (service, p, dir) = Archive(); var scans = Path.Combine(dir, "Skeny"); File(scans, "strana1.pdf", "1"); File(scans, "příloha/foto.jpg", "foto");
        var record = await service.AddAsync(p, new("10", "Složka"), [scans]);
        Assert.Equal("/100001", record.RelativePath);
        Assert.Equal("1", System.IO.File.ReadAllText(Path.Combine(p.DocumentsPath, "100001", "strana1.pdf")));
        Assert.Equal("foto", System.IO.File.ReadAllText(Path.Combine(p.DocumentsPath, "100001", "příloha", "foto.jpg")));
        Directory.CreateDirectory(Path.Combine(dir, "prázdná"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddAsync(p, new("10", "Prázdná"), [Path.Combine(dir, "prázdná")]));
    }
    [Fact]
    public async Task FilesAndFoldersCanBeMixedAndAttachedLater()
    {
        var (service, p, dir) = Archive();
        var scans = Path.Combine(dir, "Skeny"); File(scans, "a/1.pdf", "1"); var cover = File(dir, "obal.pdf", "obal");
        var record = await service.AddAsync(p, new("10", "Smíšené"), [cover, scans]);
        Assert.Equal("/100001", record.RelativePath);
        var folder = Path.Combine(p.DocumentsPath, "100001");
        Assert.True(System.IO.File.Exists(Path.Combine(folder, "obal.pdf"))); Assert.True(System.IO.File.Exists(Path.Combine(folder, "Skeny", "a", "1.pdf")));
        var single = await service.AddAsync(p, new("10", "Jeden soubor"), [File(dir, "x.pdf", "x")]); Assert.Equal("/100002.pdf", single.RelativePath);
        var extra = Path.Combine(dir, "Doplněk"); File(extra, "d.pdf", "d");
        var attached = await service.AddAsync(p, new("10", "Jeden soubor"), [extra], single.Code);
        Assert.Equal("/100002", attached.RelativePath);
        Assert.True(System.IO.File.Exists(Path.Combine(p.DocumentsPath, "100002", "100002.pdf"))); Assert.True(System.IO.File.Exists(Path.Combine(p.DocumentsPath, "100002", "Doplněk", "d.pdf")));
        await service.AddAsync(p, new("10", "Jeden soubor"), [extra], single.Code);
        Assert.True(Directory.Exists(Path.Combine(p.DocumentsPath, "100002", "Doplněk (2)")));
    }
    [Fact]
    public async Task NumberingSkipsFoldersNotYetInRegister()
    {
        var (service, p, _) = Archive(); Directory.CreateDirectory(Path.Combine(p.DocumentsPath, "100007_ručně"));
        Assert.Equal("100008", (await service.AddAsync(p, new("10", "Papír"), [])).Code);
    }
    [Fact]
    public async Task PaperRecordGetsFileThenFolderWithoutNewNumber()
    {
        var (service, p, dir) = Archive();
        var paper = await service.AddAsync(p, new("10", "Papír"), []); Assert.False(paper.Electronic); Assert.Equal("", paper.RelativePath);
        var withFile = await service.AddAsync(p, new("10", "Papír"), [File(dir, "scan.pdf", "scan")], paper.Code);
        Assert.Equal(paper.Code, withFile.Code); Assert.True(withFile.Electronic); Assert.Equal("/100001.pdf", withFile.RelativePath);
        var again = await service.AddAsync(p, new("10", "Papír"), [File(dir, "other/scan.pdf", "second")], paper.Code);
        Assert.Equal("/100001", again.RelativePath);
        Assert.Equal(["100001.pdf", "scan.pdf"], Directory.EnumerateFiles(service.LocalPath(p, again)!).Select(Path.GetFileName).Order());
        var third = await service.AddAsync(p, new("10", "Papír"), [File(dir, "third/scan.pdf", "third")], paper.Code);
        Assert.Equal("/100001", third.RelativePath);
        Assert.Equal(["100001.pdf", "scan (2).pdf", "scan.pdf"], Directory.EnumerateFiles(service.LocalPath(p, third)!).Select(Path.GetFileName).Order());
        Assert.Single(service.Load(p).Catalog.Records);
    }
    [Fact]
    public async Task LegacyFileWithSuffixMovesIntoCodeFolder()
    {
        var (service, p, dir) = Archive();
        var bytes = System.IO.File.ReadAllBytes(p.WorkbookPath); var c = new WorkbookCatalog(bytes); c.Append("100003", new("10", "Starý"), "/100003A05.doc"); System.IO.File.WriteAllBytes(p.WorkbookPath, c.Save());
        File(p.DocumentsPath, "100003A05.doc", "legacy");
        var record = await service.AddAsync(p, new("10", "Starý"), [File(dir, "scan.pdf", "scan")], "100003");
        Assert.Equal("/100003", record.RelativePath);
        Assert.Equal(["100003A05.doc", "scan.pdf"], Directory.EnumerateFiles(service.LocalPath(p, record)!).Select(Path.GetFileName).Order());
    }
    [Fact]
    public async Task FailedWriteReturnsMovedLegacyFile()
    {
        var (service, p, dir) = Archive();
        var bytes = System.IO.File.ReadAllBytes(p.WorkbookPath); var c = new WorkbookCatalog(bytes); c.Append("100001", new("10", "Starý"), "/100001.pdf"); System.IO.File.WriteAllBytes(p.WorkbookPath, c.Save());
        File(p.DocumentsPath, "100001.pdf", "legacy"); var locked = File(dir, "locked.pdf", "x");
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAnyAsync<IOException>(() => service.AddAsync(p, new("10", "Starý"), [File(dir, "scan.pdf", "scan"), locked], "100001"));
        Assert.Equal(["100001.pdf"], Directory.EnumerateFileSystemEntries(p.DocumentsPath).Select(Path.GetFileName));
        Assert.Equal("/100001.pdf", Assert.Single(service.Load(p).Catalog.Records).RelativePath);
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
