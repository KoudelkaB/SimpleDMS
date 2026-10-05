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
    // True when the "Kódování dokumentů" sheet defines categories; otherwise Categories only lists codes in use.
    public bool CategoriesDefined { get; private set; }
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
            // Legacy layout: A = N1 digit with its group name in B (filled only on the first row of
            // the group), C = N2 digit with the category name in D. Flat "10 | name" is also accepted.
            var n1 = "";
            foreach (var row in Load(parts[FindPath(cats)]).Descendants(S + "row"))
            {
                var v = new[] { "A", "B", "C", "D" }.Select(c => Value(Cell(row, c)).Trim()).ToArray();
                if (Regex.IsMatch(v[0], "^[0-9]{2}$")) { categories.TryAdd(v[0], v[1]); continue; }
                if (Regex.IsMatch(v[0] + v[1], "^[0-9]{2}$")) { categories.TryAdd(v[0] + v[1], v[2]); continue; }
                if (Regex.IsMatch(v[0], "^[0-9]$")) n1 = v[0];
                if (n1.Length == 1 && Regex.IsMatch(v[2], "^[0-9]$")) categories.TryAdd(n1 + v[2], v[3]);
            }
        }
        Reload();
        CategoriesDefined = categories.Count > 0;
        foreach (var code in Records.Select(r => r.Category).Distinct()) categories.TryAdd(code, "");
        Categories = categories;
    }
    static XDocument Load(byte[] bytes) => XDocument.Load(new MemoryStream(bytes), LoadOptions.PreserveWhitespace);
    // Column letters of a cell reference such as "M12"; called for every cell, so no regular expression.
    static string Column(XElement cell) { var r = (string?)cell.Attribute("r") ?? ""; var n = 0; while (n < r.Length && char.IsAsciiLetter(r[n])) n++; return r[..n]; }
    static XElement? Cell(XElement? row, string col) => row?.Elements(S + "c").FirstOrDefault(c => Column(c) == col);
    string Value(XElement? cell)
    {
        if (cell == null) return "";
        if ((string?)cell.Attribute("t") == "inlineStr") return string.Concat(cell.Descendants(S + "t").Select(x => x.Value));
        var value = cell.Element(S + "v")?.Value ?? "";
        return (string?)cell.Attribute("t") == "s" && int.TryParse(value, out var i) ? strings[i] : value;
    }
    // Cell reference → hyperlink target, built once per reload instead of searching the sheet for every cell.
    Dictionary<string, string> Hyperlinks()
    {
        var relPath = Path.GetDirectoryName(sheetPath)!.Replace('\\', '/') + "/_rels/" + Path.GetFileName(sheetPath) + ".rels";
        var targets = parts.TryGetValue(relPath, out var bytes)
            ? Load(bytes).Root!.Elements().Where(x => x.Attribute("Id") != null && x.Attribute("Target") != null).GroupBy(x => (string)x.Attribute("Id")!).ToDictionary(g => g.Key, g => (string)g.First().Attribute("Target")!) : [];
        var links = new Dictionary<string, string>();
        foreach (var link in sheet.Root!.Elements(S + "hyperlinks").Elements(S + "hyperlink"))
            if ((string?)link.Attribute("ref") is { } reference && (string?)link.Attribute(R + "id") is { } id && targets.TryGetValue(id, out var target)) links.TryAdd(reference, target);
        return links;
    }
    string LinkValue(XElement? cell, Dictionary<string, string> links)
    {
        var f = cell?.Element(S + "f")?.Value ?? "";
        if (f.Length > 0 && Regex.Match(f, "HYPERLINK\\(\"([^\"]+)\"", RegexOptions.IgnoreCase) is { Success: true } match) return match.Groups[1].Value;
        return (string?)cell?.Attribute("r") is { } reference && links.TryGetValue(reference, out var target) ? target : Value(cell);
    }
    public static string DriveLink(string id) => "https://drive.google.com/open?id=" + Uri.EscapeDataString(id);
    // Platnost is stored as an Excel date serial in the legacy register.
    string DateValue(XElement? cell)
    {
        var value = Value(cell);
        return cell?.Attribute("t") == null && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial >= 1 && serial < 2958466
            ? DateTime.FromOADate(serial).ToString("d.M.yyyy", CultureInfo.InvariantCulture) : value;
    }
    public static DateTime? ParseDate(string text) => DateTime.TryParseExact(text.Trim(), ["d.M.yyyy", "d. M. yyyy", "d.M.yy", "yyyy-MM-dd"],
        CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
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
        var links = Hyperlinks();
        // Rows share a few dozen styles; resolve each style's fill only once.
        var fills = new Dictionary<int, string?>();
        string? FillOf(XElement? cell) { var style = Style(cell); if (!fills.TryGetValue(style, out var fill)) fills[style] = fill = Fill(cell); return fill; }
        foreach (var row in sheet.Descendants(S + "row").Where(r => (int?)r.Attribute("r") > 1))
        {
            var digits = Enumerable.Range(0, 6).Select(i => Value(Cell(row, ((char)('A' + i)).ToString()))).ToArray();
            if (!digits.All(d => d.Length == 1 && char.IsAsciiDigit(d[0])))
            {
                if (digits.Any(d => d.Length > 0)) Warnings.Add($"Řádek {row.Attribute("r")}: neúplné evidenční číslo.");
                continue;
            }
            var code = string.Concat(digits);
            // Work in progress is marked only by the whole row A–Q filled orange.
            var pending = "ABCDEFGHIJKLMNOPQ".All(c => FillOf(Cell(row, c.ToString())) == "FFFFC000");
            // L names the document file or folder ("/100027A10.doc", "/190123"); P is MID(L, 2, 6) in the legacy
            // register. A different number there means the row points at another document's attachment.
            var l = Value(Cell(row, "L")).Trim(); var p = Value(Cell(row, "P")).Trim(); var problem = "";
            if (Regex.Match(l, "^/?([0-9]{6})(?![0-9])") is { Success: true } target && target.Groups[1].Value != code)
                problem = $"Sloupec L ({l}) ukazuje na soubor nebo složku dokumentu {target.Groups[1].Value}. Opravte L v registru.";
            else if (p.Length > 0 && p != code) problem = $"Souhrnné číslo v P ({p}) neodpovídá číslu dokumentu. Opravte P nebo L v registru.";
            if (problem.Length > 0) Warnings.Add($"{code}: {problem}");
            // M is usually a (shared) HYPERLINK formula built from Q, so Q is the reliable source of the link.
            var id = Value(Cell(row, "Q")).Trim(); var link = LinkValue(Cell(row, "M"), links);
            var url = id.Length > 0 ? DriveLink(id) : Regex.IsMatch(link, "^https://drive\\.google\\.com/.*(?:/d/|/folders/|[?&]id=)[A-Za-z0-9_-]{10,}") ? link : "";
            list.Add(new((int)row.Attribute("r")!, code, Value(Cell(row, "G")), Value(Cell(row, "H")), Value(Cell(row, "I")),
                DateValue(Cell(row, "J")), Value(Cell(row, "K")).Equals("ano", StringComparison.OrdinalIgnoreCase),
                Value(Cell(row, "L")), url, LinkValue(Cell(row, "N"), links), Value(Cell(row, "O")), id, pending, problem));
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
        var after = row.Elements(S + "c").FirstOrDefault(c => ColumnIndex(Column(c)) > ColumnIndex(col));
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
        if (col is "M" or "N") RemoveHyperlink(row, col);
    }
    void RemoveHyperlink(XElement row, string col)
    {
        sheet.Descendants(S + "hyperlink").Where(x => (string?)x.Attribute("ref") == col + (string)row.Attribute("r")!).Remove();
        // An empty <hyperlinks/> violates the schema and Excel reports the file as damaged.
        sheet.Root!.Elements(S + "hyperlinks").Where(x => !x.HasElements).Remove();
    }
    void SetFormula(XElement row, string col, string formula, string cached)
    {
        var cell = EnsureCell(row, col);
        cell.Elements().Where(e => e.Name == S + "v" || e.Name == S + "f" || e.Name == S + "is").Remove();
        cell.SetAttributeValue("t", "str");
        cell.Add(new XElement(S + "f", formula), new XElement(S + "v", cached));
        RemoveHyperlink(row, col);
    }
    // Same formulas as the legacy register: M opens the Drive item from Q, N the synchronized local folder.
    void SetLinks(XElement row, string localDocuments, bool electronic)
    {
        var r = (string)row.Attribute("r")!; var cached = electronic ? "otevřít" : "";
        foreach (var (col, target) in new[] { ("M", "\"https://drive.google.com/open?id=\"&Q" + r), ("N", "\"" + localDocuments.Replace("\"", "\"\"") + "\"&L" + r) })
        {
            if (col == "N" && localDocuments.Length == 0) continue;
            var cell = Cell(row, col);
            if (cell?.Element(S + "f") != null) { cell.SetAttributeValue("t", "str"); cell.Elements(S + "v").Remove(); cell.Add(new XElement(S + "v", cached)); RemoveHyperlink(row, col); continue; }
            SetFormula(row, col, $"IF(K{r}=\"ano\",HYPERLINK({target},\"otevřít\"),\"\")", cached);
        }
    }
    int AddStyle(XElement xf)
    {
        var xfs = styles.Root!.Element(S + "cellXfs")!;
        var match = xfs.Elements().ToList().FindIndex(x => XNode.DeepEquals(x, xf));
        if (match < 0) { match = xfs.Elements().Count(); xfs.Add(xf); xfs.SetAttributeValue("count", match + 1); }
        return match;
    }
    void SetDate(XElement row, string col, DateTime date)
    {
        var cell = EnsureCell(row, col);
        cell.Elements().Where(e => e.Name == S + "v" || e.Name == S + "f" || e.Name == S + "is").Remove();
        cell.SetAttributeValue("t", null);
        cell.Add(new XElement(S + "v", date.ToOADate().ToString(CultureInfo.InvariantCulture)));
        var xf = new XElement(styles.Root!.Element(S + "cellXfs")!.Elements().ElementAt(Style(cell)));
        var format = (int?)xf.Attribute("numFmtId") ?? 0;
        var custom = (string?)styles.Root!.Element(S + "numFmts")?.Elements().FirstOrDefault(x => (int?)x.Attribute("numFmtId") == format)?.Attribute("formatCode") ?? "";
        if (format is >= 14 and <= 22 || Regex.IsMatch(custom, "[dy]", RegexOptions.IgnoreCase)) return;
        xf.SetAttributeValue("numFmtId", 14); xf.SetAttributeValue("applyNumberFormat", 1);
        cell.SetAttributeValue("s", AddStyle(xf));
    }
    // relativePath is "/folder" relative to the documents folder, as in the legacy register.
    public DocumentRecord Append(string code, DocumentDraft draft, string relativePath, string localDocuments = "")
    {
        if (Warnings.Any(w => w.Contains("neúplné evidenční číslo"))) throw new InvalidOperationException("Nejprve opravte neúplná evidenční čísla v registru.");
        if (!Regex.IsMatch(code, "^[0-9]{6}$") || Records.Any(r => r.Code == code)) throw new InvalidOperationException("Neplatné nebo použité číslo.");
        if (string.IsNullOrWhiteSpace(draft.Title)) throw new InvalidOperationException("Doplňte název dokumentu.");
        var rowNumber = sheet.Descendants(S + "row").Where(r => r.Elements(S + "c").Any(c => Value(c).Length > 0)).Select(r => (int)r.Attribute("r")!).Max() + 1;
        var row = Row(rowNumber);
        // New rows look like their predecessor (fonts, wrapping, date format of J).
        if (Records.Count > 0)
        {
            var previous = Row(Records.MaxBy(r => r.Row)!.Row);
            foreach (var cell in previous.Elements(S + "c").Where(c => (string?)c.Attribute("s") != null && ColumnIndex(Column(c)) <= 17))
                EnsureCell(row, Column(cell)).SetAttributeValue("s", (string)cell.Attribute("s")!);
        }
        // The legacy register keeps N1–N6 as numbers; text digits would show Excel's "number stored as text".
        for (var i = 0; i < 6; i++)
        {
            var cell = EnsureCell(row, ((char)('A' + i)).ToString());
            cell.Elements().Remove(); cell.SetAttributeValue("t", null); cell.Add(new XElement(S + "v", code[i].ToString()));
        }
        var electronic = relativePath.Length > 0;
        var values = new[] { ("G", draft.Title), ("H", draft.Reference), ("I", draft.Author), ("J", draft.Validity), ("K", electronic ? "ano" : "ne"), ("L", relativePath), ("O", draft.Notes), ("P", code), ("Q", "") };
        foreach (var (col, value) in values)
            if (col == "J" && ParseDate(value) is { } date) SetDate(row, col, date); else Set(row, col, value);
        SetLinks(row, localDocuments, electronic);
        SetPending(code, draft.Pending);
        var dimension = sheet.Root!.Element(S + "dimension");
        var last = Regex.Match((string?)dimension?.Attribute("ref") ?? "Q1", @"([A-Z]+)([0-9]+)$");
        var column = last.Success && ColumnIndex(last.Groups[1].Value) > 17 ? last.Groups[1].Value : "Q";
        var lastRow = last.Success ? int.Parse(last.Groups[2].Value) : 1;
        dimension?.SetAttributeValue("ref", "A1:" + column + Math.Max(rowNumber, lastRow));
        Reload(); return Records.Single(r => r.Code == code);
    }
    public void SetDriveId(string code, string id)
    {
        var record = Records.Single(r => r.Code == code); var row = Row(record.Row);
        Set(row, "Q", id); if (Cell(row, "M")?.Element(S + "f") == null) SetLinks(row, "", record.Electronic); Reload();
    }
    public void SetAttachments(string code, string relativePath, string localDocuments = "")
    {
        var record = Records.Single(r => r.Code == code); var row = Row(record.Row);
        Set(row, "K", "ano");
        // A new target folder makes the old Q stale; the linker fills the folder ID later.
        if (record.RelativePath != relativePath) { Set(row, "L", relativePath); Set(row, "Q", ""); }
        SetLinks(row, localDocuments, true); Reload();
    }
    public void SetPending(string code, bool pending)
    {
        var row = sheet.Descendants(S + "row").Single(r => string.Concat(Enumerable.Range(0, 6).Select(i => Value(Cell(r, ((char)('A' + i)).ToString())))) == code);
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
        // calcChain lists formula cells; a stale chain makes Excel "repair" the file. Excel rebuilds it.
        if (parts.Remove("xl/calcChain.xml"))
        {
            var bookRels = Load(parts["xl/_rels/workbook.xml.rels"]);
            bookRels.Root!.Elements().Where(x => ((string?)x.Attribute("Type") ?? "").EndsWith("/calcChain", StringComparison.Ordinal)).Remove();
            parts["xl/_rels/workbook.xml.rels"] = Bytes(bookRels);
            var types = Load(parts["[Content_Types].xml"]);
            types.Root!.Elements().Where(x => (string?)x.Attribute("PartName") == "/xl/calcChain.xml").Remove();
            parts["[Content_Types].xml"] = Bytes(types);
        }
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true)) foreach (var p in parts) { using var st = zip.CreateEntry(p.Key).Open(); st.Write(p.Value); }
        return output.ToArray();
    }
    public static byte[] Create()
    {
        var headers = new[] { "N1", "N2", "N3", "N4", "N5", "N6", "Název", "Reference", "Autor/Autoři/účastníci", "Platnost", "Elektronická forma (ano/ne)", "File/folder name", "Link na Google disk", "Link na sychronizovaný lokální disk G:", "Klíčová slova (neobsažená v názvu) a poznámky", "N1N2N3N4N5N6", "Google ID složky/souboru" };
        var data = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/worksheets/sheet2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>",
            ["_rels/.rels"] = $"<Relationships xmlns=\"{P}\"><Relationship Id=\"rId1\" Type=\"{R}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>",
            ["xl/workbook.xml"] = $"<workbook xmlns=\"{S}\" xmlns:r=\"{R}\"><sheets><sheet name=\"Databáze\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"Kódování dokumentů\" sheetId=\"2\" r:id=\"rId2\"/></sheets></workbook>",
            ["xl/_rels/workbook.xml.rels"] = $"<Relationships xmlns=\"{P}\"><Relationship Id=\"rId1\" Type=\"{R}/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"{R}/worksheet\" Target=\"worksheets/sheet2.xml\"/><Relationship Id=\"rId3\" Type=\"{R}/styles\" Target=\"styles.xml\"/></Relationships>",
            ["xl/styles.xml"] = $"<styleSheet xmlns=\"{S}\"><fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts><fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills><borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/></cellXfs></styleSheet>",
            ["xl/worksheets/sheet2.xml"] = $"<worksheet xmlns=\"{S}\"><sheetData/></worksheet>"
        };
        data["xl/worksheets/sheet1.xml"] = new XDocument(new XElement(S + "worksheet", new XElement(S + "dimension", new XAttribute("ref", "A1:Q1")), new XElement(S + "sheetData", new XElement(S + "row", new XAttribute("r", 1), headers.Select((h, i) => new XElement(S + "c", new XAttribute("r", ((char)('A' + i)) + "1"), new XAttribute("t", "inlineStr"), new XElement(S + "is", new XElement(S + "t", h)))))))).ToString();
        using var output = new MemoryStream(); using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true)) foreach (var p in data) { using var w = new StreamWriter(zip.CreateEntry(p.Key).Open()); w.Write(p.Value); }
        return output.ToArray();
    }
}
