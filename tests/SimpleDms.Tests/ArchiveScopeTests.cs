using SimpleDms.Core;
using Xunit;

namespace SimpleDms.Tests;

public sealed class ArchiveScopeTests
{
    static FakeDrive Drive()
    {
        var drive = new FakeDrive();
        drive.Items["personal"] = new("personal", "Personal", "application/vnd.google-apps.folder", "1", "", [], true, true);
        drive.Items["private"] = new("private", "Private.xlsx", "application/octet-stream", "1", "", ["personal"], true, false);
        drive.Data["private"] = WorkbookCatalog.Create();
        return drive;
    }
    static ArchiveProfile Profile() => new("root", "book", "docs", "Archiv", "user", "u@example.test", true);
    static LocalStore Store() => new(Path.Combine(Path.GetTempPath(), "simpledms-scope-" + Guid.NewGuid().ToString("N")));
    static DocumentRecord Record(string id, string url = "") => new(2, "100001", "Document", "", "", "", true, "", url, "", "", id, false);

    [Fact]
    public async Task ForeignIdsNeverCausePersonalMetadataContentOrWrites()
    {
        var drive = Drive(); var scoped = new ArchiveDriveClient(drive, "root", true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scoped.GetAsync("private"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scoped.DownloadAsync("private"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scoped.ListAsync("personal"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scoped.ReplaceAsync("private", []));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scoped.UploadAsync("personal", "file", [], "application/octet-stream", "bad"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scoped.CreateFolderAsync("personal", "folder", "bad"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scoped.MoveAsync("book", "personal"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scoped.MoveAsync("private", "docs"));
        Assert.DoesNotContain("private", drive.MetadataReads); Assert.DoesNotContain("personal", drive.MetadataReads);
        Assert.DoesNotContain("personal", drive.Listings); Assert.Empty(drive.Downloads);
        Assert.Empty(drive.Replacements); Assert.Empty(drive.Moves); Assert.Equal(0, drive.Uploads); Assert.Equal(0, drive.FolderCreations);
    }
    [Fact]
    public async Task NestedArchiveFilesRemainUsableAndMovedFilesAreRejected()
    {
        var drive = Drive(); var folder = await drive.CreateFolderAsync("docs", "Nested", "nested");
        var file = await drive.UploadAsync(folder.Id, "scan.pdf", [1, 2], "application/pdf", "scan");
        var scoped = new ArchiveDriveClient(drive, "root", true);
        Assert.Equal(new byte[] { 1, 2 }, await scoped.DownloadAsync(file.Id));
        drive.Downloads.Clear(); drive.Items[folder.Id] = folder with { Parents = ["personal"] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => scoped.DownloadAsync(file.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scoped.UploadAsync(folder.Id, "new.pdf", [], "application/pdf", "new"));
        Assert.Empty(drive.Downloads); Assert.DoesNotContain("personal", drive.MetadataReads);
        Assert.Equal(1, drive.Uploads);
    }
    [Fact]
    public async Task ReadOnlyScopeRejectsEveryWriteBeforeCallingDrive()
    {
        var drive = Drive(); var scoped = new ArchiveDriveClient(drive, "root", false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scoped.ReplaceAsync("book", []));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scoped.UploadAsync("docs", "file", [], "application/pdf", "upload"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scoped.CreateFolderAsync("docs", "folder", "folder"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scoped.MoveAsync("book", "docs"));
        Assert.Empty(drive.MetadataReads); Assert.Empty(drive.Replacements); Assert.Empty(drive.Moves);
        Assert.Equal(0, drive.Uploads); Assert.Equal(0, drive.FolderCreations);
    }
    [Fact]
    public async Task RootCannotBeMovedOrReplacedAndShortcutsAreNotDownloaded()
    {
        var drive = Drive();
        drive.Items["shortcut"] = new("shortcut", "Link", "application/vnd.google-apps.shortcut", "1", "", ["docs"], true, false);
        var scoped = new ArchiveDriveClient(drive, "root", true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scoped.MoveAsync("root", "docs"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scoped.ReplaceAsync("root", []));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scoped.DownloadAsync("shortcut"));
        var service = new ArchiveService(drive, Store());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DocumentUrlAsync(Profile(), Record("shortcut")));
        Assert.Empty(drive.Downloads); Assert.Empty(drive.Replacements); Assert.Empty(drive.Moves);
    }
    [Fact]
    public async Task ForgedWorkbookProfileAndLegacyLinksDoNotReadPersonalData()
    {
        var drive = Drive(); var service = new ArchiveService(drive, Store());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RefreshAsync(Profile() with { WorkbookId = "private" }));
        Assert.False(await service.BelongsAsync(Profile(), "private"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DocumentUrlAsync(Profile(), Record("private")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DocumentUrlAsync(Profile(), Record("", "https://drive.google.com/open?id=private")));
        Assert.DoesNotContain("private", drive.MetadataReads); Assert.DoesNotContain("personal", drive.MetadataReads); Assert.Empty(drive.Downloads);
        Assert.Equal("https://drive.google.com/open?id=docs", await service.DocumentUrlAsync(Profile(), Record("docs")));
    }
    [Fact]
    public async Task RefreshDoesNotPromoteReaderToWriter()
    {
        var service = new ArchiveService(Drive(), Store());
        var result = await service.RefreshAsync(Profile() with { CanWrite = false });
        Assert.False(result.Profile.CanWrite);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetPendingAsync(result.Profile, "100001", true));
    }
    [Fact]
    public async Task RecoveryJournalCannotRedirectUploadOutsideArchive()
    {
        var drive = Drive(); var store = Store(); var p = Profile(); var service = new ArchiveService(drive, store);
        var file = Path.Combine(store.Root, "scan.pdf"); await File.WriteAllTextAsync(file, "scan");
        drive.FailNextReplace = true;
        await Assert.ThrowsAsync<IOException>(() => service.AddAsync(p, new("10", "Document"), [file]));
        var journal = service.PendingOperation(p)!; journal.FolderId = "personal";
        var journalPath = Path.Combine(store.ArchiveDirectory(p), "operation.json");
        await File.WriteAllTextAsync(journalPath, System.Text.Json.JsonSerializer.Serialize(journal, LocalStore.Json));
        var uploads = drive.Uploads; var replacements = drive.Replacements.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddAsync(p, journal.Draft, journal.Files));
        Assert.Equal(uploads, drive.Uploads); Assert.Equal(replacements, drive.Replacements.Count);
        Assert.DoesNotContain("personal", drive.MetadataReads);
    }
}
