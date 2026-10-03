using SimpleDms.Core;
using Xunit;

namespace SimpleDms.Tests;

// Optional Google linking must never read metadata outside the selected archive root.
public sealed class ArchiveScopeTests
{
    static FakeDrive Drive()
    {
        var drive = new FakeDrive();
        drive.Items["personal"] = new("personal", "Personal", "application/vnd.google-apps.folder", []);
        drive.Items["private"] = new("private", "Private.xlsx", "application/octet-stream", ["personal"]);
        return drive;
    }
    [Fact]
    public async Task ForeignIdsAreRejectedWithoutReadingThem()
    {
        var drive = Drive(); var scoped = new ArchiveDriveClient(drive, "root");
        await Assert.ThrowsAsync<InvalidOperationException>(() => scoped.GetAsync("private"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scoped.ListAsync("personal"));
        Assert.DoesNotContain("private", drive.MetadataReads); Assert.DoesNotContain("personal", drive.MetadataReads); Assert.DoesNotContain("personal", drive.Listings);
    }
    [Fact]
    public async Task MovedFolderIsRejected()
    {
        var drive = Drive(); var folder = drive.Add("nested", "Nested", "docs");
        var scoped = new ArchiveDriveClient(drive, "root");
        Assert.Equal("Nested", (await scoped.GetAsync("nested")).Name);
        drive.Items["nested"] = folder with { Parents = ["personal"] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => scoped.GetAsync("nested"));
        Assert.DoesNotContain("personal", drive.MetadataReads);
    }
    [Fact]
    public async Task LinkingRootWithoutDocumentsFolderFails()
    {
        var drive = Drive(); var dir = Path.Combine(Path.GetTempPath(), "simpledms-scope-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var service = new ArchiveService(new LocalStore(Path.Combine(dir, "app")));
        var p = service.Open(dir, new("Jiný", null, null), true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ArchiveService.VerifyDriveRootAsync(p, drive, "root"));
        var scan = Path.Combine(dir, "scan.pdf"); await File.WriteAllTextAsync(scan, "scan"); await service.AddAsync(p, new("10", "Test"), [scan]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.LinkDriveIdsAsync(p with { DriveRootId = "root", AccountId = "u" }, drive));
        Assert.DoesNotContain("personal", drive.MetadataReads);
    }
}
