using System.Diagnostics;
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
public static class LabelPdf
{
    public static void Export(string path, LabelProfile profile, PrintPlan plan)
    {
        profile.Validate();
        if (profile.Key != plan.ProfileKey) throw new InvalidOperationException("Profil se po vytvoření tiskové úlohy změnil.");
        using var document = SKDocument.CreatePdf(path) ?? throw new InvalidOperationException("Nelze vytvořit PDF.");
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        using var typeface = SKTypeface.FromFamilyName("sans-serif");
        using var numberFont = new SKFont(typeface, 5.2f); using var titleFont = new SKFont(typeface, 3.1f);
        const float mm = 72f / 25.4f;
        foreach (var page in plan.Pages)
        {
            var canvas = document.BeginPage(profile.PaperWidth * mm, profile.PaperHeight * mm); canvas.Scale(mm);
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
                    using var qrPaint = new SKPaint { Color = SKColors.Black, IsAntialias = false };
                    for (int row = 0; row < matrix.Count; row++) for (int col = 0; col < matrix.Count; col++) if (matrix[row][col])
                                canvas.DrawRect(x + profile.Width - qrSize - 2 + col * size, y + 2 + row * size, size, size, qrPaint);
                }
                canvas.Restore();
            }
            document.EndPage();
        }
        document.Close();
    }
    public static async Task SubmitAsync(string path, string? printer = null, CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows())
        {
            _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "print" }) ?? throw new InvalidOperationException("Výchozí PDF prohlížeč nepodporuje tisk. Otevřete PDF a použijte jeho tiskové okno.");
            return;
        }
        if (Environment.GetEnvironmentVariable("FLATPAK_ID") != null) { GoogleAuth.OpenBrowser(path); return; }
        var info = new ProcessStartInfo("lp") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        if (!string.IsNullOrWhiteSpace(printer)) { info.ArgumentList.Add("-d"); info.ArgumentList.Add(printer); }
        info.ArgumentList.Add("-o"); info.ArgumentList.Add("scaling=100"); info.ArgumentList.Add("-o"); info.ArgumentList.Add("fit-to-page=false"); info.ArgumentList.Add(Path.GetFullPath(path));
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Nelze spustit tisk.");
        var error = process.StandardError.ReadToEndAsync(ct); var output = process.StandardOutput.ReadToEndAsync(ct); await process.WaitForExitAsync(ct); await output;
        if (process.ExitCode != 0) throw new InvalidOperationException("Tiskárna úlohu nepřijala: " + await error);
    }
}
