using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SystemGuard.Desktop.Services;

// Общие live-компоненты: HTTP API + Cloudflare-туннель + токен.
// Владелец — MainWindowViewModel (живёт всё время работы приложения),
// бот и вкладка Telegram только читают/управляют.
public static class LiveServices
{
    private static readonly object _lock = new();
    private static RemoteHttpServer? _server;
    private static TunnelService? _tunnel;

    public static string Token { get; private set; } = LoadOrCreateToken();
    public static RemoteHttpServer? Server { get { lock (_lock) return _server; } }
    public static TunnelService Tunnel
    {
        get { lock (_lock) return _tunnel ??= new TunnelService(); }
    }

    /// <summary>
    /// Автопубликация live при старте приложения ("чтобы в следующем запуске
    /// всё работало"). Включается первым ручным Publish, выключается кнопкой
    /// Stop. Хранится в %LocalAppData%\SystemGuard\live.json.
    /// </summary>
    public static bool AutopublishLive
    {
        get { try { return File.Exists(LiveFlagPath()); } catch { return false; } }
    }

    private static string LiveFlagPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SystemGuard", "live_autostart");

    public static void SetAutopublish(bool on)
    {
        try
        {
            var p = LiveFlagPath();
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            if (on) File.WriteAllText(p, "1");
            else try { File.Delete(p); } catch { }
        }
        catch { }
    }

    public static void StartServer()
    {
        lock (_lock)
        {
            if (_server != null) return;
            _server = new RemoteHttpServer(Token);
            _server.Start();
        }
    }

    // Новый токен одной кнопкой (вкладка Telegram → "Новый токен").
    // Перезапускает HTTP API с новым токеном; туннель трогать не надо
    // (он смотрит на порт, а не на токен). WebApp придётся связать заново.
    public static string RegenerateToken()
    {
        lock (_lock)
        {
            var bytes = new byte[24];
            RandomNumberGenerator.Fill(bytes);
            Token = Convert.ToBase64String(bytes)
                .Replace('+', '-').Replace('/', '_').TrimEnd('=')[..24];
            try
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SystemGuard", "remote.json");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                SecretStore.WriteText(path, Token);
            }
            catch { }
            try { _server?.Dispose(); } catch { }
            _server = null;
            _server = new RemoteHttpServer(Token);
            _server.Start();
            return Token;
        }
    }

    public static void StopAll()
    {
        lock (_lock)
        {
            try { _tunnel?.Stop(); } catch { }
            try { _tunnel?.Dispose(); } catch { }
            _tunnel = null;
            try { _server?.Dispose(); } catch { }
            _server = null;
        }
    }

    private static string LoadOrCreateToken()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SystemGuard", "remote.json");
            var dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            var saved = SecretStore.ReadText(path);
            if (!string.IsNullOrWhiteSpace(saved))
            {
                var t = saved.Trim();
                if (t.Length >= 16) return t;
            }
            var bytes = new byte[24];
            RandomNumberGenerator.Fill(bytes);
            var token = Convert.ToBase64String(bytes)
                .Replace('+', '-').Replace('/', '_').TrimEnd('=')[..24];
            SecretStore.WriteText(path, token);
            return token;
        }
        catch
        {
            var bytes = new byte[18];
            RandomNumberGenerator.Fill(bytes);
            return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }
    }
}
