using System.Net.Http.Json;
using System.Text.Json;

namespace SimpleDms.Core;

public sealed record DriveItem(string Id, string Name, string MimeType, string[] Parents)
{
    public bool IsFolder => MimeType == "application/vnd.google-apps.folder";
}
// Files are transferred by the sync client; the API is used only to read IDs for Q.
public interface IDriveClient
{
    Task<DriveItem> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<DriveItem>> ListAsync(string parent, CancellationToken ct = default);
}
public sealed class DriveException(int status, string message) : InvalidOperationException(message)
{ public int Status { get; } = status; }
public sealed class DriveClient(GoogleAuth auth, HttpClient? client = null) : IDriveClient
{
    readonly HttpClient http = client ?? new() { Timeout = TimeSpan.FromMinutes(2) };
    const string Base = "https://www.googleapis.com/drive/v3/";
    const string Fields = "id,name,mimeType,parents";
    static DriveItem Item(JsonElement x) => new(x.GetProperty("id").GetString()!, x.GetProperty("name").GetString()!,
        x.GetProperty("mimeType").GetString()!, x.TryGetProperty("parents", out var ps) ? ps.EnumerateArray().Select(p => p.GetString()!).ToArray() : []);
    async Task<JsonElement> Send(string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new("Bearer", await auth.GetAccessTokenAsync(ct));
            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var status = (int)response.StatusCode; var reason = "";
            try { reason = (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("error").GetProperty("errors")[0].GetProperty("reason").GetString() ?? ""; }
            catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException) { }
            // Drive reports rate limits as 403 as well as 429; both are transient.
            var transient = status is 429 or >= 500 || reason is "rateLimitExceeded" or "userRateLimitExceeded";
            if (transient && attempt < 5) { await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)) + TimeSpan.FromMilliseconds(Random.Shared.Next(500)), ct); continue; }
            throw new DriveException(status, (status, transient) switch
            {
                (_, true) => "Google Drive dočasně omezuje požadavky. Zkuste to později.",
                (403, _) => $"Google Drive: chybí oprávnění ({reason}).",
                (404, _) => "Google Drive: položka není dostupná nebo byla přesunuta.",
                _ => $"Google Drive: operace selhala (HTTP {status} {reason})."
            });
        }
    }
    public async Task<DriveItem> GetAsync(string id, CancellationToken ct = default)
        => Item(await Send(Base + "files/" + Uri.EscapeDataString(id) + "?supportsAllDrives=true&fields=" + Uri.EscapeDataString(Fields), ct));
    public async Task<IReadOnlyList<DriveItem>> ListAsync(string parent, CancellationToken ct = default)
    {
        var result = new List<DriveItem>(); string? token = null;
        var q = "'" + parent.Replace("\\", "\\\\").Replace("'", "\\'") + "' in parents and trashed = false";
        do
        {
            var url = Base + "files?supportsAllDrives=true&includeItemsFromAllDrives=true&pageSize=1000&q=" + Uri.EscapeDataString(q) + "&fields=" + Uri.EscapeDataString("nextPageToken,files(" + Fields + ")");
            if (token != null) url += "&pageToken=" + Uri.EscapeDataString(token);
            var json = await Send(url, ct);
            result.AddRange(json.GetProperty("files").EnumerateArray().Select(Item)); token = json.TryGetProperty("nextPageToken", out var p) ? p.GetString() : null;
        } while (token != null);
        return result;
    }
}
