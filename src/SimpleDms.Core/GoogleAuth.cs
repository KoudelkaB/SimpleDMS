using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SimpleDms.Core;

public interface ISecretStore { Task<string?> ReadAsync(string key); Task WriteAsync(string key, string value); }
public sealed class OsSecretStore(LocalStore store) : ISecretStore
{
    string TokenPath(string key) => store.PathFor("token-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".bin");
    public async Task<string?> ReadAsync(string key)
    {
        if (OperatingSystem.IsWindows())
            return File.Exists(TokenPath(key)) ? Encoding.UTF8.GetString(ProtectedData.Unprotect(await File.ReadAllBytesAsync(TokenPath(key)), null, DataProtectionScope.CurrentUser)) : null;
        if (OperatingSystem.IsLinux()) return await Command("secret-tool", ["lookup", "application", "SimpleDMS", "account", key], null, true);
        throw new PlatformNotSupportedException("Bezpečné uložení přihlášení je připraveno pro Windows a Linux.");
    }
    public async Task WriteAsync(string key, string value)
    {
        if (OperatingSystem.IsWindows()) { await File.WriteAllBytesAsync(TokenPath(key), ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser)); return; }
        if (OperatingSystem.IsLinux()) { await Command("secret-tool", ["store", "--label=SimpleDMS Google", "application", "SimpleDMS", "account", key], value, false); return; }
        throw new PlatformNotSupportedException("Chybí zabezpečené úložiště tokenů.");
    }
    static async Task<string?> Command(string exe, string[] args, string? input, bool missingIsOk)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Nelze otevřít úložiště přihlášení.");
            if (input != null) await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); await process.WaitForExitAsync(timeout.Token);
            await error;
            if (process.ExitCode != 0 && !missingIsOk) throw new InvalidOperationException("Uložení přihlášení selhalo. Odemkněte klíčenku operačního systému.");
            return process.ExitCode == 0 ? (await output).TrimEnd('\n') : null;
        }
        catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException("Pro bezpečné Google přihlášení na Linuxu nainstalujte libsecret (secret-tool) a odemkněte klíčenku."); }
    }
}
public sealed class GoogleAuth(AppSettings settings, ISecretStore secrets, HttpClient? client = null)
{
    readonly HttpClient http = client ?? new() { Timeout = TimeSpan.FromSeconds(60) };
    readonly SemaphoreSlim gate = new(1, 1);
    string access = "", refresh = "", account = "";
    DateTimeOffset expires;
    public string AccountId => account;
    public string Email { get; private set; } = "";
    string Key => settings.ClientId + "|" + account;
    public static void OpenBrowser(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    static string Base64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public async Task SignInAsync(CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(settings.ClientId)) throw new InvalidOperationException("Správce musí nejprve nastavit Google OAuth klienta. Otevřete Nastavení nebo importujte jeho JSON.");
        access = ""; refresh = ""; account = ""; expires = default;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var verifier = Base64(RandomNumberGenerator.GetBytes(48)); var state = Base64(RandomNumberGenerator.GetBytes(32));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            var redirect = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
            var parameters = new Dictionary<string, string>{{"client_id",settings.ClientId},{"redirect_uri",redirect},{"response_type","code"},
                {"scope","openid email "+(settings.ReadOnly?"https://www.googleapis.com/auth/drive.readonly":"https://www.googleapis.com/auth/drive")},
                {"code_challenge",Base64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))},{"code_challenge_method","S256"},{"state",state},{"access_type","offline"},{"prompt","consent select_account"}};
            OpenBrowser("https://accounts.google.com/o/oauth2/v2/auth?" + string.Join('&', parameters.Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value))));
            string? code = null;
            while (code == null)
            {
                using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
                using var stream = socket.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                var line = await reader.ReadLineAsync(timeout.Token) ?? "";
                if (line.Length > 8192) continue;
                var parts = line.Split(' ');
                Uri? request = null;
                var ok = parts.Length >= 2 && parts[0] == "GET" && Uri.TryCreate(redirect.TrimEnd('/') + parts[1], UriKind.Absolute, out request) && request.AbsolutePath == "/";
                var args = ok ? ParseQuery(request!.Query) : new Dictionary<string, string>();
                ok = ok && args.TryGetValue("state", out var actual) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(state));
                var content = Encoding.UTF8.GetBytes(ok ? "Přihlášení předáno SimpleDMS. Toto okno můžete zavřít." : "Neplatný požadavek.");
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {(ok ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n"), timeout.Token);
                await stream.WriteAsync(content, timeout.Token);
                if (!ok) continue;
                if (args.ContainsKey("error")) throw new InvalidOperationException("Google přihlášení bylo odmítnuto nebo zrušeno.");
                if (!args.TryGetValue("code", out code)) throw new InvalidOperationException("Google nevrátil autorizační kód.");
            }
            await Tokens(new() { { "code", code }, { "code_verifier", verifier }, { "redirect_uri", redirect }, { "grant_type", "authorization_code" } }, timeout.Token);
            using var message = new HttpRequestMessage(HttpMethod.Get, "https://openidconnect.googleapis.com/v1/userinfo");
            message.Headers.Authorization = new("Bearer", access);
            using var response = await http.SendAsync(message, timeout.Token); response.EnsureSuccessStatusCode();
            var user = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
            account = user.GetProperty("sub").GetString()!; Email = user.GetProperty("email").GetString()!;
            if (refresh.Length == 0) throw new InvalidOperationException("Google nevrátil obnovovací token. Přihlaste se znovu.");
            await secrets.WriteAsync(Key, refresh);
        }
        finally { listener.Stop(); }
    }
    public static Dictionary<string, string> ParseQuery(string query) => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.Split('=', 2)).GroupBy(x => Uri.UnescapeDataString(x[0])).ToDictionary(x => x.Key, x => Uri.UnescapeDataString(x.First().ElementAtOrDefault(1) ?? ""));
    public async Task RestoreAsync(string id, string email, CancellationToken cancellation = default)
    {
        account = id; Email = email; refresh = await secrets.ReadAsync(Key) ?? "";
        if (refresh.Length == 0) throw new InvalidOperationException("Přihlášení není uloženo. Použijte Přihlásit Google.");
        await GetAccessTokenAsync(cancellation);
    }
    public async Task<string> GetAccessTokenAsync(CancellationToken cancellation = default)
    {
        await gate.WaitAsync(cancellation);
        try
        {
            if (expires > DateTimeOffset.UtcNow.AddMinutes(1)) return access;
            if (refresh.Length == 0) throw new InvalidOperationException("Nejprve se přihlaste přes Google.");
            await Tokens(new() { { "refresh_token", refresh }, { "grant_type", "refresh_token" } }, cancellation); return access;
        }
        finally { gate.Release(); }
    }
    async Task Tokens(Dictionary<string, string> parameters, CancellationToken cancellation)
    {
        parameters["client_id"] = settings.ClientId;
        if (settings.ClientSecret.Length > 0) parameters["client_secret"] = settings.ClientSecret;
        using var response = await http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(parameters), cancellation);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Google přihlášení vypršelo nebo OAuth klient není správně nastaven. Přihlaste se znovu.");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellation);
        access = json.GetProperty("access_token").GetString()!; expires = DateTimeOffset.UtcNow.AddSeconds(json.GetProperty("expires_in").GetInt32());
        if (json.TryGetProperty("refresh_token", out var rt)) refresh = rt.GetString()!;
    }
}
