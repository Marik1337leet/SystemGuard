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

    public static string Token { get; } = LoadOrCreateToken();
    public static RemoteHttpServer? Server { get { lock (_lock) return _server; } }
    public static TunnelService Tunnel
    {
        get { lock (_lock) return _tunnel ??= new TunnelService(); }
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
            if (File.Exists(path))
            {
                var t = File.ReadAllText(path).Trim();
                if (t.Length >= 16) return t;
            }
            var bytes = new byte[24];
            RandomNumberGenerator.Fill(bytes);
            var token = Convert.ToBase64String(bytes)
                .Replace('+', '-').Replace('/', '_').TrimEnd('=')[..24];
            File.WriteAllText(path, token);
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
