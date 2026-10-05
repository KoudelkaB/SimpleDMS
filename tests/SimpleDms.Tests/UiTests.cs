using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
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
    static void WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) { Avalonia.Threading.Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Assert.True(condition());
    }
    [Fact]
    public async Task OfflineArchiveLoadsAndFiltersInRealControls()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestApp));
        await session.Dispatch(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "simpledms-ui-" + Guid.NewGuid().ToString("N")); var store = new LocalStore(dir);
            // The synchronized folder is missing, so the window falls back to the cached copy of the register.
            var profile = new ArchiveProfile(Path.Combine(dir, "missing-drive"), "Zkušební archiv", "Zkušební archiv");
            store.Write("settings.json", new AppSettings { Archive = profile });
            var book = new WorkbookCatalog(WorkbookCatalog.Create()); book.Append("100001", new("10", "Smlouva"), ""); book.Append("100002", new("10", "Žádost", Pending: true), "");
            File.WriteAllBytes(Path.Combine(store.ArchiveDirectory(profile), "catalog.xlsx"), book.Save());
            var window = new MainWindow(store); window.Show();
            Assert.Equal("Zkušební archiv — SimpleDMS", window.Title);
            var text = window.GetLogicalDescendants().OfType<TextBox>().Single(x => x.PlaceholderText == "Číslo, název, autor, poznámky…");
            text.Text = "zadost";
            WaitFor(() => window.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == "1 z 2 dokumentů"));
            Assert.Contains(window.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "1 z 2 dokumentů");
            window.Close();
        }, CancellationToken.None);
    }
    [Fact]
    public async Task WritableArchiveOffersNamedCategoriesInAddForm()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestApp));
        await session.Dispatch(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "simpledms-ui-" + Guid.NewGuid().ToString("N")); var store = new LocalStore(Path.Combine(dir, "app"));
            var profile = new ArchiveProfile(Path.Combine(dir, "drive"), "Archiv", "Archiv"); Directory.CreateDirectory(profile.DocumentsPath);
            System.Xml.Linq.XNamespace s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            System.Xml.Linq.XElement Cell(string r, string v) => new(s + "c", new System.Xml.Linq.XAttribute("r", r), new System.Xml.Linq.XAttribute("t", "inlineStr"), new System.Xml.Linq.XElement(s + "is", new System.Xml.Linq.XElement(s + "t", v)));
            var bytes = CoreTests.Modify(WorkbookCatalog.Create(), "xl/worksheets/sheet2.xml", x => x.Root!.Element(s + "sheetData")!.Add(
                new System.Xml.Linq.XElement(s + "row", new System.Xml.Linq.XAttribute("r", 2), Cell("A2", "1"), Cell("B2", "KS"), Cell("C2", "0"), Cell("D2", "smlouvy")),
                new System.Xml.Linq.XElement(s + "row", new System.Xml.Linq.XAttribute("r", 3), Cell("C3", "1"), Cell("D3", "časopisy"))));
            var book = new WorkbookCatalog(bytes); book.Append("100001", new("10", "Nájemní smlouva", Author: "Novák", Validity: "1.1.2030"), ""); book.Append("110001", new("11", "Zpravodaj"), "");
            File.WriteAllBytes(profile.WorkbookPath, book.Save());
            store.Write("settings.json", new AppSettings { Archive = profile });
            var window = new MainWindow(store); window.Show();
            // The register and the next-number preview load in the background.
            WaitFor(() => window.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == "Přidělí se číslo 100002"));
            var combos = window.GetLogicalDescendants().OfType<ComboBox>().ToList();
            Assert.Contains(combos, c => c.SelectedItem?.ToString() == "10 – smlouvy" && c.ItemsSource!.Cast<object>().Select(x => x.ToString()).SequenceEqual(["10 – smlouvy", "11 – časopisy"]));
            Assert.Contains(window.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "Přidělí se číslo 100002");
            Assert.True(window.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == "Uložit dokument a připravit štítek").IsEnabled);
            // Unticking QR applies immediately, without saving the label profile.
            window.GetLogicalDescendants().OfType<CheckBox>().Single(x => x.Content as string == "QR kód").IsChecked = false;
            var saved = store.Read<AppSettings>("settings.json")!; Assert.False(saved.Labels.Qr); Assert.False(saved.LabelProfiles[saved.Labels.Name].Qr);
            var qa = Environment.GetEnvironmentVariable("SIMPLEDMS_QA_DIR");
            if (qa != null)
            {
                Directory.CreateDirectory(qa); var tabs = window.GetLogicalDescendants().OfType<TabControl>().Single();
                for (var i = 0; i < tabs.ItemCount; i++) { tabs.SelectedIndex = i; Avalonia.Threading.Dispatcher.UIThread.RunJobs(); using var bitmap = window.CaptureRenderedFrame(); bitmap?.Save(Path.Combine(qa, $"tab{i}.png")); }
            }
            window.Close();
        }, CancellationToken.None);
    }
    [Fact]
    public async Task QueueCanBeEditedWhilePrintAwaitsConfirmation()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestApp));
        await session.Dispatch(() =>
        {
            var store = new LocalStore(Path.Combine(Path.GetTempPath(), "simpledms-ui-" + Guid.NewGuid().ToString("N")));
            var settings = new AppSettings { LabelQueue = [new("100236", "První"), new("100246", "Druhý"), new("100247", "Třetí")] };
            settings.PendingPrint = LabelPlanner.Plan(settings.Labels, settings.Sheet, settings.LabelQueue.Take(1).ToList());
            // A label refreshed with its Google link is a different record instance with the same Key.
            settings.LabelQueue[1] = settings.LabelQueue[1] with { Url = "https://drive.google.com/open?id=x" };
            store.Write("settings.json", settings);
            var window = new MainWindow(store); window.Show();
            var queue = window.GetLogicalDescendants().OfType<ListBox>().Single(x => x.SelectionMode == SelectionMode.Multiple);
            void Click(string text) { window.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == text).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); WaitFor(() => true); }
            queue.SelectedItem = queue.ItemsSource!.Cast<LabelItem>().Single(x => x.Code == "100246");
            Click("Odebrat vybrané");
            Assert.Equal(["100236", "100247"], store.Read<AppSettings>("settings.json")!.LabelQueue.Select(x => x.Code));
            Click("Vyprázdnit frontu");
            var saved = store.Read<AppSettings>("settings.json")!; Assert.Empty(saved.LabelQueue); Assert.NotNull(saved.PendingPrint);
            window.Close();
        }, CancellationToken.None);
    }
}
