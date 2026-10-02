using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SimpleDms.Core;

// Edits only affected XML parts; all other ZIP entries, including links and print settings, are retained.
public sealed class WorkbookCatalog
{
    static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    static readonly XNamespace P = "http://schemas.openxmlformats.org/package/2006/relationships";
    readonly Dictionary<string, byte[]> parts;
    readonly List<string> strings;
    readonly XDocument sheet;
    readonly XDocument styles;
    readonly string sheetPath;
    readonly Dictionary<string, int[]> originalStyles;
    public List<string> Warnings { get; } = [];
    public IReadOnlyList<DocumentRecord> Records { get; private set; } = [];
    public IReadOnlyDictionary<string, string> Categories { get; private set; } = new Dictionary<string, string>();
    public WorkbookCatalog(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        if (zip.Entries.Sum(x => x.Length) > 128 * 1024 * 1024) throw new InvalidOperationException("Registr je příliš velký.");
        parts = zip.Entries.ToDictionary(e => e.FullName, e => { using var m = new MemoryStream(); using var st = e.Open(); st.CopyTo(m); return m.ToArray(); });
        strings = parts.TryGetValue("xl/sharedStrings.xml", out var ss) ? Load(ss).Root!.Elements(S + "si")
            .Select(x => string.Concat(x.Descendants(S + "t").Select(t => t.Value))).ToList() : [];
        var wb = Load(parts["xl/workbook.xml"]);
        var rels = Load(parts["xl/_rels/workbook.xml.rels"]);
        string FindPath(XElement node)
        {
            var rel = rels.Root!.Elements().Single(x => (string?)x.Attribute("Id") == (string?)node.Attribute(R + "id"));
            var target = (string)rel.Attribute("Target")!;
            return target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
        }
        var db = wb.Descendants(S + "sheet").FirstOrDefault(x => (string?)x.Attribute("name") == "Databáze")
            ?? throw new InvalidOperationException("XLSX neobsahuje list Databáze.");
        sheetPath = FindPath(db);
        sheet = Load(parts[sheetPath]);
        styles = Load(parts["xl/styles.xml"]);
        originalStyles = parts.TryGetValue("customXml/simpledms-state.xml", out var custom)
            ? Load(custom).Root!.Elements("row").ToDictionary(x => (string)x.Attribute("code")!, x => x.Value.Split(',').Select(int.Parse).ToArray()) : [];
        var first = sheet.Descendants(S + "row").FirstOrDefault(x => (string?)x.Attribute("r") == "1");
        for (int i = 0; i < 6; i++)
            if (Value(Cell(first, ((char)('A' + i)).ToString())) != "N" + (i + 1))
                throw new InvalidOperationException("List Databáze nemá očekávané hlavičky N1–N6 v A–F.");
        if (Value(Cell(first, "G")) != "Název") throw new InvalidOperationException("V G chybí hlavička Název.");
        var cats = wb.Descendants(S + "sheet").FirstOrDefault(x => (string?)x.Attribute("name") == "Kódování dokumentů");
        var categories = new Dictionary<string, string>();
        if (cats != null)
        {
            foreach (var row in Load(parts[FindPath(cats)]).Descendants(S + "row"))
            {
                var a = Value(Cell(row, "A")); var b = Value(Cell(row, "B"));
                var code = Regex.IsMatch(a, "^[0-9]{2}$") ? a : Regex.IsMatch(a + b, "^[0-9]{2}$") ? a + b : "";
                if (code.Length == 2) categories.TryAdd(code, Value(Cell(row, a.Length == 2 ? "B" : "C")));
            }
        }
        Reload();
        foreach (var code in Records.Select(r => r.Category).Distinct()) categories.TryAdd(code, "");
        Categories = categories;
    }
    static XDocument Load(byte[] bytes) => XDocument.Load(new MemoryStream(bytes), LoadOptions.PreserveWhitespace);
    static XElement? Cell(XElement? row, string col) => row?.Elements(S + "c").FirstOrDefault(c => Regex.Replace((string?)c.Attribute("r") ?? "", "[0-9]", "") == col);
    string Value(XElement? cell)
    {
        if (cell == null) return "";
        if ((string?)cell.Attribute("t") == "inlineStr") return string.Concat(cell.Descendants(S + "t").Select(x => x.Value));
        var value = cell.Element(S + "v")?.Value ?? "";
        return (string?)cell.Attribute("t") == "s" && int.TryParse(value, out var i) ? strings[i] : value;
    }
    string LinkValue(XElement? cell)
    {
        var f = cell?.Element(S + "f")?.Value ?? "";
        var match = Regex.Match(f, "HYPERLINK\\(\"([^\"]+)\"", RegexOptions.IgnoreCase);
        if (match.Success) return match.Groups[1].Value;
        var reference = (string?)cell?.Attribute("r");
        var link = sheet.Descendants(S + "hyperlink").FirstOrDefault(x => (string?)x.Attribute("ref") == reference);
        var id = (string?)link?.Attribute(R + "id");
        var relPath = Path.GetDirectoryName(sheetPath)!.Replace('\\', '/') + "/_rels/" + Path.GetFileName(sheetPath) + ".rels";
        if (id != null && parts.TryGetValue(relPath, out var bytes))
            return (string?)Load(bytes).Root!.Elements().FirstOrDefault(x => (string?)x.Attribute("Id") == id)?.Attribute("Target") ?? Value(cell);
        return Value(cell);
    }
    int Style(XElement? cell) => (int?)cell?.Attribute("s") ?? 0;
    string? Fill(XElement? cell)
    {
        var xf = styles.Root!.Element(S + "cellXfs")!.Elements().ElementAt(Style(cell));
        var fill = styles.Root!.Element(S + "fills")!.Elements().ElementAt((int?)xf.Attribute("fillId") ?? 0).Element(S + "patternFill");
        return (string?)fill?.Attribute("patternType") == "solid" ? (string?)fill?.Element(S + "fgColor")?.Attribute("rgb") : null;
    }
    void Reload()
    {
        Warnings.Clear();
        var list = new List<DocumentRecord>();
        foreach (var row in sheet.Descendants(S + "row").Where(r => (int?)r.Attribute("r") > 1))
        {
            var digits = Enumerable.Range(0, 6).Select(i => Value(Cell(row, ((char)('A' + i)).ToString()))).ToArray();
            if (!digits.All(d => Regex.IsMatch(d, "^[0-9]$")))
            {
                if (digits.Any(d => d.Length > 0)) Warnings.Add($"Řádek {row.Attribute("r")}: neúplné evidenční číslo.");
                continue;
            }
            var code = string.Concat(digits);
            var state = Value(Cell(row, "R"));
            var orange = "ABCDEFGHIJKLMNOPQ".All(c => Fill(Cell(row, c.ToString())) == "FFFFC000");
            var pending = state == "Rozpracovaný" || (state.Length == 0 && orange);
            if (state.Length > 0 && state is not ("Rozpracovaný" or "Dokončený")) Warnings.Add($"{code}: neznámý stav zpracování.");
            if (state.Length > 0 && pending != orange) Warnings.Add($"{code}: stav a oranžová výplň se liší.");
            var p = Value(Cell(row, "P"));
            if (p.Length > 0 && p != code) Warnings.Add($"{code}: souhrnné číslo v P se liší od A–F.");
            list.Add(new((int)row.Attribute("r")!, code, Value(Cell(row, "G")), Value(Cell(row, "H")), Value(Cell(row, "I")),
                Value(Cell(row, "J")), Value(Cell(row, "K")).Equals("ano", StringComparison.OrdinalIgnoreCase),
                Value(Cell(row, "L")), LinkValue(Cell(row, "M")), LinkValue(Cell(row, "N")), Value(Cell(row, "O")), Value(Cell(row, "Q")), pending));
        }
        if (list.GroupBy(x => x.Code).Any(g => g.Count() > 1)) throw new InvalidOperationException("Registr obsahuje duplicitní evidenční čísla. Zápis nelze bezpečně provést.");
        Records = list;
    }
    XElement Row(int number)
    {
        var data = sheet.Root!.Element(S + "sheetData")!;
        var row = data.Elements(S + "row").FirstOrDefault(r => (int?)r.Attribute("r") == number);
        if (row != null) return row;
        row = new XElement(S + "row", new XAttribute("r", number));
        var after = data.Elements(S + "row").FirstOrDefault(r => (int?)r.Attribute("r") > number);
        if (after == null) data.Add(row); else after.AddBeforeSelf(row);
        return row;
    }
    XElement EnsureCell(XElement row, string col)
    {
        var cell = Cell(row, col);
        if (cell != null) return cell;
        cell = new XElement(S + "c", new XAttribute("r", col + (string)row.Attribute("r")!));
        var after = row.Elements(S + "c").FirstOrDefault(c => ColumnIndex(Regex.Replace((string)c.Attribute("r")!, "[0-9]", "")) > ColumnIndex(col));
        if (after == null) row.Add(cell); else after.AddBeforeSelf(cell);
        return cell;
    }
    static int ColumnIndex(string col) => col.Aggregate(0, (v, c) => v * 26 + c - 'A' + 1);
    void Set(XElement row, string col, string value)
    {
        var cell = EnsureCell(row, col);
        cell.Elements().Where(e => e.Name == S + "v" || e.Name == S + "f" || e.Name == S + "is").Remove();
        cell.SetAttributeValue("t", "inlineStr");
        cell.AddFirst(new XElement(S + "is", new XElement(S + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), value)));
        if (col is "M" or "N")
        {
            sheet.Descendants(S + "hyperlink").Where(x => (string?)x.Attribute("ref") == col + (string)row.Attribute("r")!).Remove();
            // An empty <hyperlinks/> violates the schema and Excel reports the file as damaged.
            sheet.Root!.Elements(S + "hyperlinks").Where(x => !x.HasElements).Remove();
        }
    }
    public DocumentRecord Append(string code, DocumentDraft draft, string path, string url, string id)
    {
        if (Warnings.Any(w => w.Contains("neúplné evidenční číslo"))) throw new InvalidOperationException("Nejprve opravte neúplná evidenční čísla v registru.");
        if (!Regex.IsMatch(code, "^[0-9]{6}$") || Records.Any(r => r.Code == code)) throw new InvalidOperationException("Neplatné nebo použité číslo.");
        if (string.IsNullOrWhiteSpace(draft.Title)) throw new InvalidOperationException("Doplňte název dokumentu.");
        var rowNumber = sheet.Descendants(S + "row").Where(r => r.Elements(S + "c").Any(c => Value(c).Length > 0)).Select(r => (int)r.Attribute("r")!).Max() + 1;
        var row = Row(rowNumber);
        for (var i = 0; i < 6; i++) Set(row, ((char)('A' + i)).ToString(), code[i].ToString());
        var values = new[] { draft.Title, draft.Reference, draft.Author, draft.Validity, id.Length > 0 ? "ano" : "ne", path, url, "", draft.Notes, code, id };
        for (var i = 0; i < values.Length; i++) Set(row, ((char)('G' + i)).ToString(), values[i]);
        SetPending(code, draft.Pending);
        var dimension = sheet.Root!.Element(S + "dimension");
        var last = Regex.Match((string?)dimension?.Attribute("ref") ?? "R1", @"([A-Z]+)([0-9]+)$");
        var column = last.Success && ColumnIndex(last.Groups[1].Value) > 18 ? last.Groups[1].Value : "R";
        var lastRow = last.Success ? int.Parse(last.Groups[2].Value) : 1;
        dimension?.SetAttributeValue("ref", "A1:" + column + Math.Max(rowNumber, lastRow));
        Reload(); return Records.Single(r => r.Code == code);
    }
    public void SetDriveId(string code, string id) => Set(Row(Records.Single(r => r.Code == code).Row), "Q", id);
    public void SetAttachments(string code, string path, string url, string id)
    { var row = Row(Records.Single(r => r.Code == code).Row); Set(row, "K", "ano"); Set(row, "L", path); Set(row, "M", url); Set(row, "Q", id); Reload(); }
    public void SetPending(string code, bool pending)
    {
        var row = sheet.Descendants(S + "row").Single(r => string.Concat(Enumerable.Range(0, 6).Select(i => Value(Cell(r, ((char)('A' + i)).ToString())))) == code);
        var header = Row(1);
        if (Value(Cell(header, "R")) is not ("" or "Stav zpracování")) throw new InvalidOperationException("Sloupec R je již používán pro jiná data.");
        Set(header, "R", "Stav zpracování"); Set(row, "R", pending ? "Rozpracovaný" : "Dokončený");
        var fills = styles.Root!.Element(S + "fills")!;
        var xfs = styles.Root!.Element(S + "cellXfs")!;
        int orange = fills.Elements().ToList().FindIndex(f => (string?)f.Element(S + "patternFill")?.Element(S + "fgColor")?.Attribute("rgb") == "FFFFC000");
        if (orange < 0) { orange = fills.Elements().Count(); fills.Add(new XElement(S + "fill", new XElement(S + "patternFill", new XAttribute("patternType", "solid"), new XElement(S + "fgColor", new XAttribute("rgb", "FFFFC000")), new XElement(S + "bgColor", new XAttribute("indexed", "64"))))); fills.SetAttributeValue("count", orange + 1); }
        if (pending && !originalStyles.ContainsKey(code)) originalStyles[code] = "ABCDEFGHIJKLMNOPQ".Select(c =>
        {
            var cell = Cell(row, c.ToString()); var index = Style(cell); if (Fill(cell) != "FFFFC000") return index;
            var baseStyle = new XElement(xfs.Elements().ElementAt(index)); baseStyle.SetAttributeValue("fillId", 0); baseStyle.SetAttributeValue("applyFill", 1);
            var match = xfs.Elements().ToList().FindIndex(x => XNode.DeepEquals(x, baseStyle));
            if (match < 0) { match = xfs.Elements().Count(); xfs.Add(baseStyle); xfs.SetAttributeValue("count", match + 1); }
            return match;
        }).ToArray();
        for (var i = 0; i < 17; i++)
        {
            var cell = EnsureCell(row, ((char)('A' + i)).ToString());
            if (!pending && originalStyles.TryGetValue(code, out var prior)) { cell.SetAttributeValue("s", prior[i]); continue; }
            if (!pending && Fill(cell) != "FFFFC000") continue;
            var xf = new XElement(xfs.Elements().ElementAt(Style(cell))); xf.SetAttributeValue("fillId", pending ? orange : 0); xf.SetAttributeValue("applyFill", 1);
            var match = xfs.Elements().ToList().FindIndex(x => XNode.DeepEquals(x, xf));
            if (match < 0) { match = xfs.Elements().Count(); xfs.Add(xf); xfs.SetAttributeValue("count", match + 1); }
            cell.SetAttributeValue("s", match);
        }
        if (!pending) originalStyles.Remove(code);
        Reload();
    }
    static byte[] Bytes(XDocument doc) { using var m = new MemoryStream(); doc.Save(m, SaveOptions.DisableFormatting); return m.ToArray(); }
    public byte[] Save()
    {
        parts[sheetPath] = Bytes(sheet); parts["xl/styles.xml"] = Bytes(styles);
        parts["customXml/simpledms-state.xml"] = Bytes(new XDocument(new XElement("states", originalStyles.Select(x => new XElement("row", new XAttribute("code", x.Key), string.Join(',', x.Value))))));
        var rels = Load(parts["_rels/.rels"]);
        if (!rels.Root!.Elements().Any(x => (string?)x.Attribute("Target") == "customXml/simpledms-state.xml"))
            rels.Root.Add(new XElement(P + "Relationship", new XAttribute("Id", "simpledmsState"), new XAttribute("Type", R.NamespaceName + "/customXml"), new XAttribute("Target", "customXml/simpledms-state.xml")));
        parts["_rels/.rels"] = Bytes(rels);
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true)) foreach (var p in parts) { using var st = zip.CreateEntry(p.Key).Open(); st.Write(p.Value); }
        return output.ToArray();
    }
    public static byte[] Create()
    {
        var headers = new[] { "N1", "N2", "N3", "N4", "N5", "N6", "Název", "Reference", "Autor/Autoři/účastníci", "Platnost", "Elektronická forma (ano/ne)", "File/folder name", "Link na Google disk", "Link na sychronizovaný lokální disk G:", "Klíčová slova (neobsažená v názvu) a poznámky", "N1N2N3N4N5N6", "Google ID složky/souboru", "Stav zpracování" };
        var data = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/worksheets/sheet2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>",
            ["_rels/.rels"] = $"<Relationships xmlns=\"{P}\"><Relationship Id=\"rId1\" Type=\"{R}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>",
            ["xl/workbook.xml"] = $"<workbook xmlns=\"{S}\" xmlns:r=\"{R}\"><sheets><sheet name=\"Databáze\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"Kódování dokumentů\" sheetId=\"2\" r:id=\"rId2\"/></sheets></workbook>",
            ["xl/_rels/workbook.xml.rels"] = $"<Relationships xmlns=\"{P}\"><Relationship Id=\"rId1\" Type=\"{R}/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"{R}/worksheet\" Target=\"worksheets/sheet2.xml\"/><Relationship Id=\"rId3\" Type=\"{R}/styles\" Target=\"styles.xml\"/></Relationships>",
            ["xl/styles.xml"] = $"<styleSheet xmlns=\"{S}\"><fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts><fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills><borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/></cellXfs></styleSheet>",
            ["xl/worksheets/sheet2.xml"] = $"<worksheet xmlns=\"{S}\"><sheetData/></worksheet>"
        };
        data["xl/worksheets/sheet1.xml"] = new XDocument(new XElement(S + "worksheet", new XElement(S + "dimension", new XAttribute("ref", "A1:R1")), new XElement(S + "sheetData", new XElement(S + "row", new XAttribute("r", 1), headers.Select((h, i) => new XElement(S + "c", new XAttribute("r", ((char)('A' + i)) + "1"), new XAttribute("t", "inlineStr"), new XElement(S + "is", new XElement(S + "t", h)))))))).ToString();
        using var output = new MemoryStream(); using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true)) foreach (var p in data) { using var w = new StreamWriter(zip.CreateEntry(p.Key).Open()); w.Write(p.Value); }
        return output.ToArray();
    }
}
