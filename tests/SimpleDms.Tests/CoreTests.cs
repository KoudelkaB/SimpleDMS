using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;
using SimpleDms.Core;
using Xunit;

namespace SimpleDms.Tests;

public sealed class CoreTests
{
    internal static DocumentRecord Record(string code = "100001", string title = "Příručka žárovky") => new(2, code, title, "", "Novák", "", true, "", "", "", "Zelený šanon", "", false);
    [Fact] public void SearchCombinesWordsAndIgnoresAccents() { var r = Record(); Assert.True(r.Matches("prirucka novak")); Assert.False(r.Matches("prirucka chybi")); Assert.True(r.Matches("100001")); }
    [Fact] public void NumberingNeverFillsGaps() { Assert.Equal("100008", ArchivePaths.NextCode([Record("100001"), Record("100004")], "10", ["100007"])); Assert.Throws<InvalidOperationException>(() => ArchivePaths.NextCode([Record("109999")], "10")); }
    [Theory]
    [InlineData("https://drive.google.com/drive/folders/abc_-123?usp=sharing", "abc_-123")]
    public void RootUrlExtractsIdentity(string url, string id) => Assert.Equal(id, ArchivePaths.ParseRoot(url));
    [Theory]
    [InlineData("https://evil.example/drive/folders/abc")]
    [InlineData("file:///drive/folders/abc")]
    public void RootUrlRejectsWrongHost(string url) => Assert.Throws<InvalidOperationException>(() => ArchivePaths.ParseRoot(url));
    [Theory]
    [InlineData("../outside")]
    [InlineData("dir/../../secret")]
    [InlineData("/tmp/absolute")]
    public void LocalPathsCannotLeaveArchive(string value) => Assert.Throws<InvalidOperationException>(() => ArchivePaths.ResolveLocal(Path.GetTempPath(), value));
    [Fact]
    public void TemplateAndRoundtripPreserveDocumentsAndExplicitState()
    {
        var c = new WorkbookCatalog(WorkbookCatalog.Create()); c.Append("100001", new("10", "Žádost", "Ref", "Autor", Notes: "Šanon", Pending: true), "100001_Zadost", "https://drive.google.com/open?id=test", "test");
        var loaded = new WorkbookCatalog(c.Save()); var r = Assert.Single(loaded.Records); Assert.True(r.Pending); Assert.Equal("Žádost", r.Title); Assert.Equal("test", r.DriveId); Assert.Empty(loaded.Warnings);
        loaded.SetPending(r.Code, false); Assert.False(Assert.Single(new WorkbookCatalog(loaded.Save()).Records).Pending);
    }
    [Fact]
    public void OnlyWholeOrangeRowIsPending()
    {
        var c = new WorkbookCatalog(WorkbookCatalog.Create()); c.Append("100001", new("10", "Rozpracované", Pending: true), "", "", ""); c.Append("100002", new("10", "Jen název"), "", "", "");
        var bytes = Modify(c.Save(), "xl/worksheets/sheet1.xml", xml =>
        {
            XNamespace s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main"; var rows = xml.Descendants(s + "row").ToList();
            foreach (var row in rows.Skip(1)) { row.Elements(s + "c").Where(x => ((string)x.Attribute("r")!).StartsWith("R")).Remove(); }
            var orange = rows[1].Elements(s + "c").Single(x => (string?)x.Attribute("r") == "G2").Attribute("s")!.Value;
            rows[2].Elements(s + "c").Single(x => (string?)x.Attribute("r") == "G3").SetAttributeValue("s", orange);
        });
        var records = new WorkbookCatalog(bytes).Records; Assert.True(records[0].Pending); Assert.False(records[1].Pending);
    }
    [Fact]
    public void WorkbookKeepsUntouchedZipPartsAndExistingFormulas()
    {
        var bytes = WorkbookCatalog.Create(); var c = new WorkbookCatalog(bytes); c.Append("100001", new("10", "První"), "", "", "");
        bytes = Modify(c.Save(), "xl/worksheets/sheet1.xml", xml => { XNamespace s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main"; var cell = xml.Descendants(s + "c").Single(x => (string?)x.Attribute("r") == "P2"); cell.Elements().Remove(); cell.SetAttributeValue("t", null); cell.Add(new XElement(s + "f", "CONCAT(A2,B2,C2,D2,E2,F2)"), new XElement(s + "v", "100001")); });
        c = new(bytes); c.Append("100002", new("10", "Další", Pending: true), "", "", ""); var result = c.Save();
        Assert.Equal(Entry(bytes, "xl/worksheets/sheet2.xml"), Entry(result, "xl/worksheets/sheet2.xml"));
        Assert.Contains("CONCAT(A2,B2,C2,D2,E2,F2)", System.Text.Encoding.UTF8.GetString(Entry(result, "xl/worksheets/sheet1.xml")));
        Assert.Equal(2, new WorkbookCatalog(result).Records.Count);
    }
    [Fact]
    public void ReplacingLastHyperlinkDropsEmptyHyperlinksElement()
    {
        XNamespace s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var c = new WorkbookCatalog(WorkbookCatalog.Create()); c.Append("100001", new("10", "První"), "", "", "");
        var bytes = Modify(c.Save(), "xl/worksheets/sheet1.xml", xml => xml.Root!.Add(new XElement(s + "hyperlinks", new XElement(s + "hyperlink", new XAttribute("ref", "M2")))));
        c = new(bytes); c.SetAttachments("100001", "100001_Prvni", "https://drive.google.com/drive/folders/f", "f");
        var sheet = XDocument.Load(new MemoryStream(Entry(c.Save(), "xl/worksheets/sheet1.xml")));
        Assert.Empty(sheet.Root!.Elements(s + "hyperlinks"));
    }
    [Fact]
    public void LabelBatchSkipsUsedCellsAndContinuesAcrossSheets()
    {
        var profile = new LabelProfile(); var sheet = new LabelSheet { ProfileKey = profile.Key, Start = 5, Used = [0, 1, 2, 3, 4, 7] };
        var queue = Enumerable.Range(0, 22).Select(i => new LabelItem("100001", "Štítek " + i)).ToList(); var plan = LabelPlanner.Plan(profile, sheet, queue);
        Assert.Equal(5, plan.Placements[0].Position); Assert.DoesNotContain(plan.Pages[0].Placements, p => p.Position == 7); Assert.Equal(2, plan.Pages.Count);
        var result = LabelPlanner.Confirm(profile, sheet, plan, [plan.Placements[0]], queue); Assert.Equal(21, result.Queue.Count); Assert.Contains(5, result.Sheet.Used); Assert.Equal(6, result.Sheet.Next(profile)); Assert.Equal(6, sheet.Used.Count);
    }
    [Fact]
    public void PdfPreviewDoesNotConsumeSheetAndHasPhysicalPageDimensions()
    {
        var profile = new LabelProfile(); var sheet = new LabelSheet { Start = 5 }; var plan = LabelPlanner.Plan(profile, sheet, [new("100001", "Žádost o připojení")]);
        var dir = Path.Combine(Path.GetTempPath(), "simpledms-pdf-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "labels.pdf"); LabelPdf.Export(path, profile, plan); Assert.Empty(sheet.Used); Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(path)));
        var qa = Environment.GetEnvironmentVariable("SIMPLEDMS_QA_DIR"); if (qa != null) { Directory.CreateDirectory(qa); File.Copy(path, Path.Combine(qa, "labels.pdf"), true); }
    }
    [Fact]
    public void InvalidGridAndMismatchedPrintPlanAreRejected()
    { var p = new LabelProfile { Columns = 10 }; Assert.Throws<InvalidOperationException>(p.Validate); p = new(); var sheet = new LabelSheet(); var plan = LabelPlanner.Plan(p, sheet, [new("100001", "")]); Assert.Throws<InvalidOperationException>(() => LabelPlanner.Confirm(p, new(), plan, plan.Placements, [])); }
    [Fact]
    public void SettingsPersistPaperPositionAndPendingPrint()
    { var path = Path.Combine(Path.GetTempPath(), "simpledms-store-" + Guid.NewGuid().ToString("N")); var s = new LocalStore(path); var settings = new AppSettings(); settings.Sheet.Start = 5; settings.Sheet.Used = [0, 1]; settings.PendingPrint = LabelPlanner.Plan(settings.Labels, settings.Sheet, [new("100001", "Test")]); s.Write("settings.json", settings); var loaded = s.Read<AppSettings>("settings.json")!; Assert.Equal(5, loaded.Sheet.Start); Assert.Equal(2, loaded.Sheet.Used.Count); Assert.NotNull(loaded.PendingPrint); }
    [Fact]
    public void ReferenceWorkbookImportAndWriteKeepSourceUnchanged()
    {
        var path = Environment.GetEnvironmentVariable("SIMPLEDMS_REFERENCE_WORKBOOK"); if (path == null) return;
        var source = File.ReadAllBytes(path); var hash = SHA256.HashData(source); var c = new WorkbookCatalog(source); Assert.Equal(668, c.Records.Count); Assert.Equal(5, c.Records.Count(x => x.Pending));
        c.SetPending(c.Records.First(x => !x.Pending).Code, true); var saved = c.Save(); Assert.Equal(668, new WorkbookCatalog(saved).Records.Count); Assert.Equal(6, new WorkbookCatalog(saved).Records.Count(x => x.Pending));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(path))); Assert.Equal(Entry(source, "xl/worksheets/sheet2.xml"), Entry(saved, "xl/worksheets/sheet2.xml"));
    }
    internal static byte[] Entry(byte[] bytes, string name) { using var z = new ZipArchive(new MemoryStream(bytes)); using var m = new MemoryStream(); using var s = z.GetEntry(name)!.Open(); s.CopyTo(m); return m.ToArray(); }
    static byte[] Modify(byte[] bytes, string name, Action<XDocument> action)
    {
        using var m = new MemoryStream(); m.Write(bytes); using (var z = new ZipArchive(m, ZipArchiveMode.Update, true)) { var entry = z.GetEntry(name)!; XDocument doc; using (var stream = entry.Open()) doc = XDocument.Load(stream); action(doc); entry.Delete(); using var output = z.CreateEntry(name).Open(); doc.Save(output); }
        return m.ToArray();
    }
}
