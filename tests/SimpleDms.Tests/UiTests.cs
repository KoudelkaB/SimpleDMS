using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using SimpleDms.App;
using SimpleDms.Core;
using Xunit;

namespace SimpleDms.Tests;

public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<SimpleDms.App.App>().UseHeadless(new() { UseHeadlessDrawing = false }).UseSkia();
}
public sealed class UiTests
{
    [Fact]
    public async Task OfflineArchiveLoadsAndFiltersInRealControls()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestApp));
        await session.Dispatch(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "simpledms-ui-" + Guid.NewGuid().ToString("N")); var store = new LocalStore(dir);
            var profile = new ArchiveProfile("root", "book", "docs", "Zkušební archiv", "u", "user@example.test", false);
            store.Write("settings.json", new AppSettings { Archive = profile });
            var book = new WorkbookCatalog(WorkbookCatalog.Create()); book.Append("100001", new("10", "Smlouva"), "", "", ""); book.Append("100002", new("10", "Žádost", Pending: true), "", "", "");
            File.WriteAllBytes(Path.Combine(store.ArchiveDirectory(profile), "catalog.xlsx"), book.Save());
            var window = new MainWindow(store); window.Show();
            Assert.Equal("Zkušební archiv — SimpleDMS", window.Title);
            var text = window.GetVisualDescendants().OfType<TextBox>().Single(x => x.PlaceholderText == "Číslo, název, autor, poznámky…");
            text.Text = "zadost";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "1 z 2 dokumentů");
            var qa = Environment.GetEnvironmentVariable("SIMPLEDMS_QA_DIR"); if (qa != null) { Directory.CreateDirectory(qa); using var bitmap = window.CaptureRenderedFrame(); bitmap?.Save(Path.Combine(qa, "application.png")); }
            window.Close();
        }, CancellationToken.None);
    }
}
