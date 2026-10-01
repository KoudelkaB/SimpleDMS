using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace SimpleDms.Core;

public sealed record DriveItem(string Id, string Name, string MimeType, string Version, string Md5, string[] Parents,
    bool CanEdit, bool CanAddChildren, long Size = 0)
{
    public bool IsFolder => MimeType == "application/vnd.google-apps.folder";
}
public interface IDriveClient
{
    Task<DriveItem> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<DriveItem>> ListAsync(string parent, CancellationToken ct = default);
    Task<byte[]> DownloadAsync(string id, CancellationToken ct = default);
    Task<DriveItem> CreateFolderAsync(string parent, string name, string operation, CancellationToken ct = default);
    Task<DriveItem> UploadAsync(string parent, string name, byte[] bytes, string mime, string operation, CancellationToken ct = default);
    Task<DriveItem> ReplaceAsync(string id, byte[] bytes, CancellationToken ct = default);
    Task<DriveItem> MoveAsync(string id, string parent, CancellationToken ct = default);
}
public sealed class DriveException(int status, string message) : InvalidOperationException(message)
{ public int Status { get; } = status; }
public sealed class DriveClient(GoogleAuth auth, HttpClient? client = null) : IDriveClient
{
    readonly HttpClient http = client ?? new() { Timeout = TimeSpan.FromMinutes(5) };
    const string Base = "https://www.googleapis.com/drive/v3/";
    const string Fields = "id,name,mimeType,version,md5Checksum,parents,size,capabilities(canEdit,canAddChildren)";
    static DriveItem Item(JsonElement x) => new(x.GetProperty("id").GetString()!, x.GetProperty("name").GetString()!,
        x.GetProperty("mimeType").GetString()!, x.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "",
        x.TryGetProperty("md5Checksum", out var m) ? m.GetString() ?? "" : "", x.TryGetProperty("parents", out var ps) ? ps.EnumerateArray().Select(p => p.GetString()!).ToArray() : [],
        x.TryGetProperty("capabilities", out var c) && c.TryGetProperty("canEdit", out var e) && e.GetBoolean(),
        x.TryGetProperty("capabilities", out c) && c.TryGetProperty("canAddChildren", out var a) && a.GetBoolean(),
        x.TryGetProperty("size", out var size) && long.TryParse(size.GetString(), out var n) ? n : 0);
    async Task<HttpResponseMessage> Send(HttpMethod method, string url, HttpContent? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url) { Content = body };
        request.Headers.Authorization = new("Bearer", await auth.GetAccessTokenAsync(ct));
        var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode; response.Dispose();
            throw new DriveException(status, status switch { 403 => "Google Drive: chybí oprávnění pro tuto operaci.", 404 => "Google Drive: položka není dostupná nebo byla přesunuta.", 429 => "Google Drive dočasně omezuje požadavky. Zkuste operaci později.", _ => $"Google Drive: operace selhala (HTTP {status})." });
        }
        return response;
    }
    public async Task<DriveItem> GetAsync(string id, CancellationToken ct = default)
    { using var r = await Send(HttpMethod.Get, Base + "files/" + Uri.EscapeDataString(id) + "?supportsAllDrives=true&fields=" + Uri.EscapeDataString(Fields), null, ct); return Item(await r.Content.ReadFromJsonAsync<JsonElement>(ct)); }
    async Task<IReadOnlyList<DriveItem>> Query(string q, CancellationToken ct)
    {
        var result = new List<DriveItem>(); string? token = null;
        do
        {
            var url = Base + "files?supportsAllDrives=true&includeItemsFromAllDrives=true&pageSize=1000&q=" + Uri.EscapeDataString(q) + "&fields=" + Uri.EscapeDataString("nextPageToken,files(" + Fields + ")");
            if (token != null) url += "&pageToken=" + Uri.EscapeDataString(token);
            using var r = await Send(HttpMethod.Get, url, null, ct); var json = await r.Content.ReadFromJsonAsync<JsonElement>(ct);
            result.AddRange(json.GetProperty("files").EnumerateArray().Select(Item)); token = json.TryGetProperty("nextPageToken", out var p) ? p.GetString() : null;
        } while (token != null);
        return result;
    }
    static string Q(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");
    public Task<IReadOnlyList<DriveItem>> ListAsync(string parent, CancellationToken ct = default) => Query("'" + Q(parent) + "' in parents and trashed = false", ct);
    public async Task<byte[]> DownloadAsync(string id, CancellationToken ct = default)
    {
        var item = await GetAsync(id, ct);
        var url = Base + "files/" + Uri.EscapeDataString(id);
        if (item.MimeType.StartsWith("application/vnd.google-apps."))
            url += "/export?mimeType=" + Uri.EscapeDataString(item.MimeType == "application/vnd.google-apps.spreadsheet" ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" : "application/pdf");
        else url += "?alt=media&supportsAllDrives=true";
        using var r = await Send(HttpMethod.Get, url, null, ct); return await r.Content.ReadAsByteArrayAsync(ct);
    }
    async Task<DriveItem?> Existing(string parent, string operation, CancellationToken ct)
    {
        var items = await Query("'" + Q(parent) + "' in parents and trashed = false and appProperties has { key='simpledmsOperation' and value='" + Q(operation) + "' }", ct);
        if (items.Count > 1) throw new InvalidOperationException("Na Drive jsou duplicitní položky téže operace.");
        return items.SingleOrDefault();
    }
    public async Task<DriveItem> CreateFolderAsync(string parent, string name, string operation, CancellationToken ct = default)
    {
        var old = await Existing(parent, operation, ct); if (old != null) return old;
        using var r = await Send(HttpMethod.Post, Base + "files?supportsAllDrives=true&fields=" + Uri.EscapeDataString(Fields),
            JsonContent.Create(new { name, mimeType = "application/vnd.google-apps.folder", parents = new[] { parent }, appProperties = new { simpledmsOperation = operation } }), ct);
        return Item(await r.Content.ReadFromJsonAsync<JsonElement>(ct));
    }
    public async Task<DriveItem> UploadAsync(string parent, string name, byte[] bytes, string mime, string operation, CancellationToken ct = default)
    {
        var old = await Existing(parent, operation, ct); if (old != null) return old;
        return await Transfer(HttpMethod.Post, "", bytes, mime, JsonContent.Create(new { name, parents = new[] { parent }, appProperties = new { simpledmsOperation = operation } }), ct);
    }
    public Task<DriveItem> ReplaceAsync(string id, byte[] bytes, CancellationToken ct = default) => Transfer(HttpMethod.Patch, id, bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", JsonContent.Create(new { }), ct);
    public async Task<DriveItem> MoveAsync(string id, string parent, CancellationToken ct = default)
    {
        var item = await GetAsync(id, ct); if (item.Parents.Contains(parent)) return item;
        var url = Base + "files/" + Uri.EscapeDataString(id) + "?supportsAllDrives=true&addParents=" + Uri.EscapeDataString(parent) + "&removeParents=" + Uri.EscapeDataString(string.Join(',', item.Parents)) + "&fields=" + Uri.EscapeDataString(Fields);
        using var response = await Send(HttpMethod.Patch, url, JsonContent.Create(new { }), ct); return Item(await response.Content.ReadFromJsonAsync<JsonElement>(ct));
    }
    async Task<DriveItem> Transfer(HttpMethod method, string id, byte[] bytes, string mime, HttpContent metadata, CancellationToken ct)
    {
        metadata.Headers.Add("X-Upload-Content-Type", mime); metadata.Headers.Add("X-Upload-Content-Length", bytes.Length.ToString());
        using var begin = await Send(method, "https://www.googleapis.com/upload/drive/v3/files" + (id.Length > 0 ? "/" + Uri.EscapeDataString(id) : "") + "?uploadType=resumable&supportsAllDrives=true&fields=" + Uri.EscapeDataString(Fields), metadata, ct);
        var location = begin.Headers.Location ?? throw new InvalidOperationException("Google nevrátil adresu uploadu.");
        if (location.Scheme != "https" || !(location.Host == "www.googleapis.com" || location.Host == "content.googleapis.com")) throw new InvalidOperationException("Google vrátil neplatnou adresu uploadu.");
        var data = new ByteArrayContent(bytes); data.Headers.ContentType = new MediaTypeHeaderValue(mime);
        using var result = await Send(HttpMethod.Put, location.AbsoluteUri, data, ct); return Item(await result.Content.ReadFromJsonAsync<JsonElement>(ct));
    }
}
