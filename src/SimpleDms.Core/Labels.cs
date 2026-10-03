using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using QRCoder;
using SkiaSharp;

namespace SimpleDms.Core;

public sealed class LabelProfile
{
    public string Name { get; set; } = "A4 3 × 8";
    public int Rows { get; set; } = 8;
    public int Columns { get; set; } = 3;
    public float PaperWidth { get; set; } = 210;
    public float PaperHeight { get; set; } = 297;
    public float Width { get; set; } = 64;
    public float Height { get; set; } = 33.9f;
    public float Left { get; set; } = 7;
    public float Top { get; set; } = 12.9f;
    public float GapX { get; set; } = 2;
    public float GapY { get; set; } = 0;
    public float OffsetX { get; set; }
    public float OffsetY { get; set; }
    public bool Qr { get; set; } = true;
    public int Capacity => checked(Rows * Columns);
    public string Key => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new { Rows, Columns, PaperWidth, PaperHeight, Width, Height, Left, Top, GapX, GapY }))))[..24];
    public void Validate()
    {
        if (Rows < 1 || Columns < 1 || Rows > 50 || Columns > 50 || Rows * Columns > 500) throw new InvalidOperationException("Arch musí mít 1 až 500 nálepek.");
        if (new[] { PaperWidth, PaperHeight, Width, Height, Left, Top, GapX, GapY, OffsetX, OffsetY }.Any(x => !float.IsFinite(x))) throw new InvalidOperationException("Rozměry musí být konečná čísla.");
        if (Width < 15 || Height < 10 || PaperWidth < Width || PaperHeight < Height || Left < 0 || Top < 0 || GapX < 0 || GapY < 0) throw new InvalidOperationException("Neplatné rozměry archu nebo nálepky.");
        if (Left + Columns * Width + (Columns - 1) * GapX > PaperWidth + .01 || Top + Rows * Height + (Rows - 1) * GapY > PaperHeight + .01) throw new InvalidOperationException("Nálepky se nevejdou na zadaný papír.");
        if (Left + OffsetX < 0 || Top + OffsetY < 0 || Left + OffsetX + Columns * Width + (Columns - 1) * GapX > PaperWidth || Top + OffsetY + Rows * Height + (Rows - 1) * GapY > PaperHeight) throw new InvalidOperationException("Kalibrace posouvá nálepky mimo papír.");
    }
}
public sealed class LabelSheet
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ProfileKey { get; set; } = "";
    public HashSet<int> Used { get; set; } = [];
    public int Start { get; set; }
    public int Next(LabelProfile p) => Enumerable.Range(Math.Clamp(Start, 0, p.Capacity), p.Capacity - Math.Clamp(Start, 0, p.Capacity)).FirstOrDefault(i => !Used.Contains(i), p.Capacity);
}
public sealed record LabelPlacement(int Page, int Position, LabelItem Label);
public sealed record PrintPage(string SheetId, List<LabelPlacement> Placements);
public sealed record PrintPlan(string Id, string ProfileKey, List<PrintPage> Pages)
{
    public IReadOnlyList<LabelPlacement> Placements => Pages.SelectMany(p => p.Placements).ToList();
}
public static class LabelPlanner
{
    public static PrintPlan Plan(LabelProfile profile, LabelSheet sheet, IReadOnlyList<LabelItem> labels)
    {
        profile.Validate();
        if (sheet.ProfileKey.Length > 0 && sheet.ProfileKey != profile.Key) throw new InvalidOperationException("Arch patří jinému profilu. Vyberte odpovídající arch nebo založte nový.");
        if (labels.Count == 0) throw new InvalidOperationException("Ve frontě nejsou štítky.");
        var available = Enumerable.Range(Math.Clamp(sheet.Start, 0, profile.Capacity), profile.Capacity - Math.Clamp(sheet.Start, 0, profile.Capacity)).Where(i => !sheet.Used.Contains(i)).ToList();
        if (available.Count == 0) throw new InvalidOperationException("Arch je plný. Vložte nový papír a zvolte Nový arch.");
        var pages = new List<PrintPage>(); var page = new PrintPage(sheet.Id, []); pages.Add(page); int at = 0;
        foreach (var label in labels)
        {
            if (at == available.Count) { page = new(Guid.NewGuid().ToString("N"), []); pages.Add(page); available = Enumerable.Range(0, profile.Capacity).ToList(); at = 0; }
            page.Placements.Add(new(pages.Count - 1, available[at++], label));
        }
        return new(Guid.NewGuid().ToString("N"), profile.Key, pages);
    }
    public static (LabelSheet Sheet, List<LabelItem> Queue) Confirm(LabelProfile profile, LabelSheet original, PrintPlan plan, IEnumerable<LabelPlacement> printed, IEnumerable<LabelItem> queue)
    {
        if (plan.ProfileKey != profile.Key || plan.Pages[0].SheetId != original.Id) throw new InvalidOperationException("Tisková úloha patří jinému archu.");
        var results = printed.Distinct().ToList();
        if (results.Any(p => !plan.Placements.Contains(p))) throw new InvalidOperationException("Pozice nepatří tiskové úloze.");
        var completedKeys = results.Select(x => x.Label.Key).ToHashSet();
        var lastPage = results.Select(x => x.Page).DefaultIfEmpty(0).Max();
        var sheet = new LabelSheet { Id = plan.Pages[lastPage].SheetId, ProfileKey = profile.Key, Start = lastPage == 0 ? original.Start : 0, Used = lastPage == 0 ? [.. original.Used] : [] };
        foreach (var placement in results.Where(x => x.Page == lastPage)) sheet.Used.Add(placement.Position);
        sheet.Start = sheet.Next(profile);
        return (sheet, queue.Where(x => !completedKeys.Contains(x.Key)).ToList());
    }
}
// Draws label pages in millimetres; shared by the PDF preview and direct printing.
sealed class LabelRenderer : IDisposable
{
    readonly SKPaint paint = new() { Color = SKColors.Black, IsAntialias = true };
    readonly SKPaint qrPaint = new() { Color = SKColors.Black, IsAntialias = false };
    readonly SKTypeface typeface = SKTypeface.FromFamilyName("sans-serif");
    readonly SKFont numberFont, titleFont;
    public LabelRenderer() { numberFont = new(typeface, 5.2f); titleFont = new(typeface, 3.1f); }
    public static void Check(LabelProfile profile, PrintPlan plan)
    {
        profile.Validate();
        if (profile.Key != plan.ProfileKey) throw new InvalidOperationException("Profil se po vytvoření tiskové úlohy změnil.");
    }
    public void Draw(SKCanvas canvas, LabelProfile profile, PrintPage page)
    {
        foreach (var placement in page.Placements)
        {
            var x = profile.Left + profile.OffsetX + (placement.Position % profile.Columns) * (profile.Width + profile.GapX);
            var y = profile.Top + profile.OffsetY + (placement.Position / profile.Columns) * (profile.Height + profile.GapY);
            canvas.Save(); canvas.ClipRect(new SKRect(x, y, x + profile.Width, y + profile.Height));
            var qrSize = profile.Qr ? Math.Min(18, profile.Height - 4) : 0;
            var textWidth = profile.Width - 4 - (profile.Qr ? qrSize + 2 : 0);
            canvas.DrawText(placement.Label.Code, x + 2, y + 7, numberFont, paint);
            var title = placement.Label.Title.Normalize();
            while (title.Length > 0 && titleFont.MeasureText(title, paint) > textWidth) title = title[..^1];
            canvas.DrawText(title, x + 2, y + 12, titleFont, paint);
            if (profile.Qr)
            {
                using var qr = QRCodeGenerator.GenerateQrCode(placement.Label.Url.Length > 0 ? placement.Label.Url : placement.Label.Code, QRCodeGenerator.ECCLevel.M);
                var matrix = qr.ModuleMatrix; var size = qrSize / matrix.Count;
                for (int row = 0; row < matrix.Count; row++) for (int col = 0; col < matrix.Count; col++) if (matrix[row][col])
                            canvas.DrawRect(x + profile.Width - qrSize - 2 + col * size, y + 2 + row * size, size, size, qrPaint);
            }
            canvas.Restore();
        }
    }
    public void Dispose() { numberFont.Dispose(); titleFont.Dispose(); typeface.Dispose(); paint.Dispose(); qrPaint.Dispose(); }
}
public static class LabelPdf
{
    public static void Export(string path, LabelProfile profile, PrintPlan plan)
    {
        LabelRenderer.Check(profile, plan);
        using var document = SKDocument.CreatePdf(path) ?? throw new InvalidOperationException("Nelze vytvořit PDF.");
        using var renderer = new LabelRenderer();
        const float mm = 72f / 25.4f;
        foreach (var page in plan.Pages)
        {
            var canvas = document.BeginPage(profile.PaperWidth * mm, profile.PaperHeight * mm); canvas.Scale(mm);
            renderer.Draw(canvas, profile, page);
            document.EndPage();
        }
        document.Close();
    }
}
public static class LabelPrinter
{
    static bool Flatpak => Environment.GetEnvironmentVariable("FLATPAK_ID") != null;
    public static IReadOnlyList<string> List()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return WindowsPrint.Printers();
            return Run("lpstat", "-e").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or System.Security.SecurityException or UnauthorizedAccessException) { return []; }
    }
    public static string? Default()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return WindowsPrint.DefaultPrinter();
            var line = Run("lpstat", "-d"); var index = line.IndexOf(':');
            return index > 0 ? line[(index + 1)..].Trim() : null;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or System.Security.SecurityException or UnauthorizedAccessException) { return null; }
    }
    static string Run(string exe, params string[] args)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Nelze spustit " + exe + ".");
        var output = process.StandardOutput.ReadToEndAsync(); _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000)) { process.Kill(); throw new InvalidOperationException(exe + " neodpovídá."); }
        return process.ExitCode == 0 ? output.Result : "";
    }
    // Returns a status message. Windows prints directly through GDI at exactly 100 % (no PDF viewer scaling);
    // Linux sends the PDF to CUPS; Flatpak opens the PDF because it cannot reach the host printers.
    public static async Task<string> PrintAsync(LabelProfile profile, PrintPlan plan, string? printer, string pdfFolder, CancellationToken ct = default)
    {
        LabelRenderer.Check(profile, plan);
        if (OperatingSystem.IsWindows())
        {
            printer = string.IsNullOrWhiteSpace(printer) ? Default() : printer;
            if (string.IsNullOrWhiteSpace(printer)) throw new InvalidOperationException("Vyberte tiskárnu.");
            await Task.Run(() => { if (OperatingSystem.IsWindows()) WindowsPrint.Print(profile, plan, printer); }, ct);
            return "Úloha předána tiskárně " + printer + ".";
        }
        Directory.CreateDirectory(pdfFolder); var path = Path.Combine(pdfFolder, plan.Id + ".pdf"); LabelPdf.Export(path, profile, plan);
        if (Flatpak) { GoogleAuth.OpenBrowser(path); return "PDF otevřeno pro tisk. Zvolte měřítko 100 %."; }
        var info = new ProcessStartInfo("lp") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        if (!string.IsNullOrWhiteSpace(printer)) { info.ArgumentList.Add("-d"); info.ArgumentList.Add(printer); }
        info.ArgumentList.Add("-o"); info.ArgumentList.Add("scaling=100"); info.ArgumentList.Add("-o"); info.ArgumentList.Add("fit-to-page=false"); info.ArgumentList.Add(Path.GetFullPath(path));
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Nelze spustit tisk.");
        var error = process.StandardError.ReadToEndAsync(ct); var output = process.StandardOutput.ReadToEndAsync(ct); await process.WaitForExitAsync(ct); await output;
        if (process.ExitCode != 0) throw new InvalidOperationException("Tiskárna úlohu nepřijala: " + await error);
        return "Úloha předána tiskárně" + (string.IsNullOrWhiteSpace(printer) ? "." : " " + printer + ".");
    }
}
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class WindowsPrint
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DocInfo { public int cbSize; public string lpszDocName; public string? lpszOutput; public string? lpszDatatype; public int fwType; }
    [StructLayout(LayoutKind.Sequential)]
    struct BitmapInfoHeader { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateDCW")] static extern IntPtr CreateDC(string driver, string device, string? output, IntPtr mode);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "StartDocW")] static extern int StartDoc(IntPtr dc, ref DocInfo info);
    [DllImport("gdi32.dll")] static extern int EndDoc(IntPtr dc);
    [DllImport("gdi32.dll")] static extern int AbortDoc(IntPtr dc);
    [DllImport("gdi32.dll")] static extern int StartPage(IntPtr dc);
    [DllImport("gdi32.dll")] static extern int EndPage(IntPtr dc);
    [DllImport("gdi32.dll")] static extern int GetDeviceCaps(IntPtr dc, int index);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] static extern int StretchDIBits(IntPtr dc, int x, int y, int width, int height, int sx, int sy, int sw, int sh, IntPtr bits, ref BitmapInfoHeader info, uint usage, uint rop);
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, EntryPoint = "EnumPrintersW", SetLastError = true)] static extern bool EnumPrinters(int flags, string? name, int level, IntPtr buffer, int size, out int needed, out int returned);
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, EntryPoint = "GetDefaultPrinterW", SetLastError = true)] static extern bool GetDefaultPrinter(char[]? buffer, ref int size);
    public static IReadOnlyList<string> Printers()
    {
        const int Local = 2, Connections = 4, InfoSize = 3; // PRINTER_INFO_4: name, server, attributes
        EnumPrinters(Local | Connections, null, 4, IntPtr.Zero, 0, out var needed, out _);
        if (needed == 0) return [];
        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!EnumPrinters(Local | Connections, null, 4, buffer, needed, out _, out var count)) return [];
            return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, i * InfoSize * IntPtr.Size))).OfType<string>().Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public static string? DefaultPrinter()
    {
        var size = 0; GetDefaultPrinter(null, ref size);
        if (size == 0) return null;
        var buffer = new char[size];
        return GetDefaultPrinter(buffer, ref size) ? new string(buffer, 0, Math.Max(0, size - 1)) : null;
    }
    const int HorzRes = 8, VertRes = 10, LogPixelsX = 88, LogPixelsY = 90, PhysicalWidth = 110, PhysicalHeight = 111, PhysicalOffsetX = 112, PhysicalOffsetY = 113;
    // output: optional file for drivers such as "Microsoft Print to PDF" (used for verification).
    public static void Print(LabelProfile profile, PrintPlan plan, string printer, string? output = null)
    {
        var dc = CreateDC("WINSPOOL", printer, null, IntPtr.Zero);
        if (dc == IntPtr.Zero) throw new InvalidOperationException("Tiskárnu „" + printer + "“ nelze otevřít.");
        try
        {
            float dpiX = GetDeviceCaps(dc, LogPixelsX), dpiY = GetDeviceCaps(dc, LogPixelsY);
            float paperWidth = GetDeviceCaps(dc, PhysicalWidth) * 25.4f / dpiX, paperHeight = GetDeviceCaps(dc, PhysicalHeight) * 25.4f / dpiY;
            if (Math.Abs(paperWidth - profile.PaperWidth) > 3 || Math.Abs(paperHeight - profile.PaperHeight) > 3)
                throw new InvalidOperationException($"Tiskárna má nastavený papír {paperWidth:0}×{paperHeight:0} mm, profil archu {profile.PaperWidth:0}×{profile.PaperHeight:0} mm. Změňte formát papíru ve vlastnostech tiskárny.");
            int width = GetDeviceCaps(dc, HorzRes), height = GetDeviceCaps(dc, VertRes);
            float offsetX = GetDeviceCaps(dc, PhysicalOffsetX), offsetY = GetDeviceCaps(dc, PhysicalOffsetY);
            // Render at most 300 DPI to keep the page bitmap small; GDI scales it to the device.
            var scale = Math.Min(1f, 300f / Math.Max(dpiX, dpiY));
            int bitmapWidth = (int)Math.Ceiling(width * scale), bitmapHeight = (int)Math.Ceiling(height * scale);
            var info = new DocInfo { cbSize = Marshal.SizeOf<DocInfo>(), lpszDocName = "SimpleDMS – štítky", lpszOutput = output };
            if (StartDoc(dc, ref info) <= 0) throw new InvalidOperationException("Tiskárna odmítla tiskovou úlohu.");
            try
            {
                using var renderer = new LabelRenderer();
                foreach (var page in plan.Pages)
                {
                    if (StartPage(dc) <= 0) throw new InvalidOperationException("Tiskárna odmítla stránku.");
                    using var bitmap = new SKBitmap(new SKImageInfo(bitmapWidth, bitmapHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
                    using (var canvas = new SKCanvas(bitmap))
                    {
                        // Millimetres from the paper edge → device pixels of the printable area.
                        canvas.Clear(SKColors.White); canvas.Scale(scale); canvas.Translate(-offsetX, -offsetY); canvas.Scale(dpiX / 25.4f, dpiY / 25.4f);
                        renderer.Draw(canvas, profile, page);
                    }
                    var header = new BitmapInfoHeader { biSize = Marshal.SizeOf<BitmapInfoHeader>(), biWidth = bitmapWidth, biHeight = -bitmapHeight, biPlanes = 1, biBitCount = 32 };
                    SetStretchBltMode(dc, 3);
                    if (StretchDIBits(dc, 0, 0, width, height, 0, 0, bitmapWidth, bitmapHeight, bitmap.GetPixels(), ref header, 0, 0x00CC0020) == 0)
                        throw new InvalidOperationException("Stránku štítků nelze předat tiskárně.");
                    if (EndPage(dc) <= 0) throw new InvalidOperationException("Tiskárna odmítla stránku.");
                }
                if (EndDoc(dc) <= 0) throw new InvalidOperationException("Tisk nebyl dokončen.");
            }
            catch { AbortDoc(dc); throw; }
        }
        finally { DeleteDC(dc); }
    }
}
