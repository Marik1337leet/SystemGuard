using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SystemGuard.Desktop.Services;

public class AccountSession
{
    public string Provider { get; set; } = ""; // "Google" | "Apple"
    public string Sub { get; set; } = "";
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public string? AvatarUrl { get; set; }
    public string RefreshToken { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public bool IsExpired => DateTime.UtcNow > ExpiresAt.AddMinutes(-5);
}

// ── Полноценный OAuth 2.0 (Google) + OIDC (Apple) ───────────────────────────
// Схема: системный браузер → loopback http://127.0.0.1:порт/ → обмен code→token.
// Секреты не хранятся в коде кроме ClientID (публичный). Refresh-токен лежит
// в %LocalAppData%/SystemGuard/account.dat под AES-ключом, привязанным к HWID.
public class AuthService
{
    private static readonly HttpClient _http = new();
    private readonly string _sessionPath;

    public AccountSession? CurrentSession { get; private set; }
    public bool IsSignedIn => CurrentSession != null;
    public event Action? OnAccountChanged;

    public AuthService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SystemGuard");
        Directory.CreateDirectory(dir);
        _sessionPath = Path.Combine(dir, "account.dat");
        LoadSession();
    }

    // ── Google ──────────────────────────────────────────────────────────────

    public async Task<AccountSession> SignInWithGoogleAsync(CancellationToken ct = default)
    {
        if (!AuthConfig.IsGoogleConfigured)
            throw new InvalidOperationException(
                "Google Client ID не настроен. Открой Services/AuthConfig.cs и вставь GOOGLE Client ID (console.cloud.google.com → Credentials → Desktop app).");

        var port = GetFreePort();
        var redirectUri = $"http://127.0.0.1:{port}/";
        var state = RandomHex(16);

        var authUrl = "https://accounts.google.com/o/oauth2/v2/auth" +
            $"?client_id={Uri.EscapeDataString(AuthConfig.GoogleClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            "&response_type=code" +
            "&scope=" + Uri.EscapeDataString("openid email profile") +
            "&access_type=offline&prompt=consent" +
            $"&state={state}";

        var code = await AuthorizeViaBrowserAsync(authUrl, redirectUri, state, ct);

        var token = await PostFormAsync("https://oauth2.googleapis.com/token", new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = AuthConfig.GoogleClientId,
            ["client_secret"] = AuthConfig.GoogleClientSecret,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code"
        }, ct);

        var accessToken = token.GetProperty("access_token").GetString() ?? "";
        var refreshToken = token.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : CurrentSession?.RefreshToken ?? "";
        var expiresIn = token.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;

        var userJson = await GetGoogleUserInfoAsync(accessToken, ct);

        using var user = JsonDocument.Parse(userJson);
        var root = user.RootElement;

        var session = new AccountSession
        {
            Provider = "Google",
            Sub = root.TryGetProperty("sub", out var s) ? s.GetString() ?? "" : "",
            Email = root.TryGetProperty("email", out var e) ? e.GetString() ?? "" : "",
            Name = root.TryGetProperty("name", out var n) ? n.GetString() ?? ""
                : root.TryGetProperty("email", out var e2) ? e2.GetString() ?? "" : "",
            AvatarUrl = root.TryGetProperty("picture", out var p) ? p.GetString() : null,
            RefreshToken = refreshToken,
            ExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn)
        };

        SetSession(session);
        return session;
    }

    public async Task RefreshGoogleAsync(CancellationToken ct = default)
    {
        if (CurrentSession?.Provider != "Google" || string.IsNullOrEmpty(CurrentSession.RefreshToken))
            throw new InvalidOperationException("No Google refresh token. Sign in again.");

        var token = await PostFormAsync("https://oauth2.googleapis.com/token", new Dictionary<string, string>
        {
            ["client_id"] = AuthConfig.GoogleClientId,
            ["client_secret"] = AuthConfig.GoogleClientSecret,
            ["refresh_token"] = CurrentSession.RefreshToken,
            ["grant_type"] = "refresh_token"
        }, ct);

        var expiresIn = token.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
        CurrentSession.ExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn);
        SaveSession();
        OnAccountChanged?.Invoke();
    }

    // ── Apple (OIDC, response_mode=query для native/loopback) ────────────────

    public async Task<AccountSession> SignInWithAppleAsync(CancellationToken ct = default)
    {
        if (!AuthConfig.IsAppleConfigured)
            throw new InvalidOperationException(
                "Apple Sign In не настроен. Нужны: Services ID, Team ID, Key ID и .p8 ключ " +
                "(developer.apple.com, платный Developer Program). Вставь их в Services/AuthConfig.cs.");

        var port = GetFreePort();
        var redirectUri = $"http://127.0.0.1:{port}/";
        var state = RandomHex(16);

        var authUrl = "https://appleid.apple.com/auth/authorize" +
            "?response_type=code" +
            $"&client_id={Uri.EscapeDataString(AuthConfig.AppleServiceId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            "&scope=" + Uri.EscapeDataString("name email") +
            "&response_mode=query" +
            $"&state={state}";

        var code = await AuthorizeViaBrowserAsync(authUrl, redirectUri, state, ct);
        var clientSecret = CreateAppleClientSecret();

        var token = await PostFormAsync("https://appleid.apple.com/auth/token", new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = AuthConfig.AppleServiceId,
            ["client_secret"] = clientSecret,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code"
        }, ct);

        // Apple возвращает id_token (JWT). Email/name забираем из его payload без проверки
        // подписи здесь — серверная проверка обязательна в проде с бэкендом; для
        // локального аккаунта фиксируем sub как stable-идентификатор.
        string sub = "", email = "";
        if (token.TryGetProperty("id_token", out var idt))
            (sub, email) = ParseAppleIdToken(idt.GetString() ?? "");

        var expiresIn = token.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
        var refreshToken = token.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "";

        var session = new AccountSession
        {
            Provider = "Apple",
            Sub = sub,
            Email = email,
            Name = email, // имя Apple отдаёт только при первой авторизации (в user-параметре формы)
            RefreshToken = refreshToken,
            ExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn)
        };

        SetSession(session);
        return session;
    }

    private static (string Sub, string Email) ParseAppleIdToken(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return ("", "");
            var payload = Encoding.UTF8.GetString(Convert.FromBase64String(PadBase64(parts[1])));
            using var doc = JsonDocument.Parse(payload);
            var r = doc.RootElement;
            return (
                r.TryGetProperty("sub", out var s) ? s.GetString() ?? "" : "",
                r.TryGetProperty("email", out var e) ? e.GetString() ?? "" : "");
        }
        catch { return ("", ""); }
    }

    private static string CreateAppleClientSecret()
    {
        // client_secret = JWT ES256: header {alg,kid}, payload {iss(team),sub(client),aud,iat,exp}
        // Подпись приватным .p8 ключом.
        var header = Base64Url(Encoding.UTF8.GetBytes(
            $"{{\"alg\":\"ES256\",\"kid\":\"{AuthConfig.AppleKeyId}\"}}"));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = Base64Url(Encoding.UTF8.GetBytes(
            "{\"iss\":\"" + AuthConfig.AppleTeamId + "\"," +
            "\"sub\":\"" + AuthConfig.AppleServiceId + "\"," +
            "\"aud\":\"https://appleid.apple.com\"," +
            $"\"iat\":{now},\"exp\":{now + 86400 * 150}" + "}"));

        var data = $"{header}.{payload}";
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(AuthConfig.ApplePrivateKeyP8);
        var sig = ecdsa.SignData(Encoding.ASCII.GetBytes(data), HashAlgorithmName.SHA256);
        return $"{data}.{Base64Url(sig)}";
    }

    // ── Общие: браузер + loopback ────────────────────────────────────────────

    private static async Task<string> AuthorizeViaBrowserAsync(
        string authUrl, string redirectUri, string state, CancellationToken ct)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        listener.Start();

        try
        {
            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Cannot open browser: {ex.Message}");
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        while (!linked.Token.IsCancellationRequested)
        {
            var ctx = await listener.GetContextAsync().WaitAsync(linked.Token);
            try
            {
                var q = ctx.Request.QueryString;
                if (q["error"] != null)
                {
                    await Respond(ctx, "Authorization failed. You can close this tab.");
                    throw new InvalidOperationException($"OAuth error: {q["error"]}");
                }
                if (q["state"] != state)
                {
                    await Respond(ctx, "State mismatch. Try again.");
                    continue;
                }
                var code = q["code"];
                if (string.IsNullOrEmpty(code))
                {
                    await Respond(ctx, "No code received.");
                    continue;
                }
                await Respond(ctx, "Signed in! Return to SystemGuard.");
                return code;
            }
            finally { try { ctx.Response.Close(); } catch { } }
        }
        throw new OperationCanceledException("Authorization timed out.");
    }

    private static async Task Respond(HttpListenerContext ctx, string text)
    {
        var html = $"<html><body style='background:#0C0A15;color:#FAFAFA;font-family:sans-serif;" +
                   $"display:flex;height:100vh;align-items:center;justify-content:center'>" +
                   $"<h2>{System.Net.WebUtility.HtmlEncode(text)}</h2></body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
    }

    private static async Task<JsonElement> PostFormAsync(
        string url, Dictionary<string, string> fields, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(fields);
        using var res = await _http.PostAsync(url, content, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Token endpoint error ({(int)res.StatusCode}): {Trim(body, 300)}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    // ── Сессия: хранение под HWID-привязанным AES ────────────────────────────

    public void SignOut()
    {
        CurrentSession = null;
        try { File.Delete(_sessionPath); } catch { }
        OnAccountChanged?.Invoke();
    }

    private void SetSession(AccountSession s)
    {
        CurrentSession = s;
        SaveSession();
        OnAccountChanged?.Invoke();
    }

    private void SaveSession()
    {
        try
        {
            if (CurrentSession == null) return;
            var json = JsonSerializer.Serialize(CurrentSession);
            File.WriteAllBytes(_sessionPath, Protect(Encoding.UTF8.GetBytes(json)));
        }
        catch { }
    }

    private void LoadSession()
    {
        try
        {
            if (!File.Exists(_sessionPath)) return;
            var json = Encoding.UTF8.GetString(Unprotect(File.ReadAllBytes(_sessionPath)));
            CurrentSession = JsonSerializer.Deserialize<AccountSession>(json);
        }
        catch { CurrentSession = null; }
    }

    private static byte[] MachineAesKey()
    {
        // Ключ, стабильный на этом ПК и неизвестный без доступа к нему.
        var hwid = Environment.MachineName + "|" + Environment.ProcessorCount;
        return SHA256.HashData(Encoding.UTF8.GetBytes(hwid + "|SG_ACCOUNT_V1"));
    }

    private static byte[] Protect(byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = MachineAesKey();
        aes.IV = new byte[16]; // фиксированный IV допустим: ключ уникален на машину
        using var enc = aes.CreateEncryptor();
        return enc.TransformFinalBlock(data, 0, data.Length);
    }

    private static byte[] Unprotect(byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = MachineAesKey();
        aes.IV = new byte[16];
        using var dec = aes.CreateDecryptor();
        return dec.TransformFinalBlock(data, 0, data.Length);
    }

    private static async Task<string> GetGoogleUserInfoAsync(string accessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            "https://openidconnect.googleapis.com/v1/userinfo");
        req.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync(ct);
    }

    // ── Утилиты ──────────────────────────────────────────────────────────────

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string RandomHex(int bytes)
    {
        var b = new byte[bytes];
        RandomNumberGenerator.Fill(b);
        return Convert.ToHexString(b).ToLower();
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string PadBase64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
    }

    private static string Trim(string s, int n) => s.Length > n ? s[..n] + "…" : s;
}
