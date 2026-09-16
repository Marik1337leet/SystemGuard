using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SystemGuard.Desktop.Services;

public class WebSocketServer : IDisposable
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<string, WebSocket> _clients = new();

    // Токен авторизации — генерируется при старте сервера
    private readonly string _authToken;

    public event Action<string>? OnMessageReceived;
    public bool IsRunning { get; private set; }

    // Отдаём токен наружу чтобы показать в UI / Mini App
    public string AuthToken => _authToken;

    public WebSocketServer()
    {
        // Генерируем случайный токен при каждом запуске
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        _authToken = Convert.ToBase64String(bytes)[..24]; // 24 chars URL-safe
    }

    public void Start(int port = 8888)
    {
        if (IsRunning) return;

        _cts = new CancellationTokenSource();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();
        IsRunning = true;

        _ = ListenAsync(_cts.Token);
        _ = CleanupLoop(_cts.Token);

        System.Diagnostics.Debug.WriteLine($"[WS] Server started on port {port}, token: {_authToken}");
    }

    public void Stop()
    {
        if (!IsRunning) return;

        IsRunning = false;
        _cts?.Cancel();

        foreach (var (_, ws) in _clients)
        {
            try { ws.Abort(); } catch { }
        }
        _clients.Clear();

        try { _listener?.Stop(); } catch { }
    }

    // ── Подключение клиентов ──────────────────────────────────────────────────

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var context = await _listener!.GetContextAsync().WaitAsync(ct);

                if (!context.Request.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    continue;
                }

                // Проверка токена — передаётся как query-параметр ?token=XXX
                var token = context.Request.QueryString["token"];
                if (token != _authToken)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    System.Diagnostics.Debug.WriteLine("[WS] Rejected unauthorized connection");
                    continue;
                }

                var wsContext = await context.AcceptWebSocketAsync(null);
                var clientId = Guid.NewGuid().ToString()[..8];
                _clients[clientId] = wsContext.WebSocket;

                _ = HandleClientAsync(clientId, wsContext.WebSocket, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    System.Diagnostics.Debug.WriteLine($"[WS] Listen error: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(string clientId, WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[4096];

        try
        {
            // Отправляем приветствие с текущим статусом
            await SendToClientAsync(ws, await BuildStatusJson(), ct);

            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
                    break;
                }

                var msg = Encoding.UTF8.GetString(buffer, 0, result.Count).Trim();
                OnMessageReceived?.Invoke(msg);

                var response = await ProcessCommandAsync(msg);
                await SendToClientAsync(ws, response, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WS] Client {clientId} error: {ex.Message}");
        }
        finally
        {
            _clients.TryRemove(clientId, out _);
            try { if (ws.State != WebSocketState.Closed) ws.Abort(); } catch { }
        }
    }

    // ── Broadcast ─────────────────────────────────────────────────────────────

    public async Task BroadcastAsync(string message)
    {
        foreach (var (id, ws) in _clients)
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                    await SendToClientAsync(ws, message, CancellationToken.None);
            }
            catch
            {
                _clients.TryRemove(id, out _);
            }
        }
    }

    private static async Task SendToClientAsync(WebSocket ws, string message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    // ── Команды ───────────────────────────────────────────────────────────────

    private async Task<string> ProcessCommandAsync(string command)
    {
        return command.ToLower() switch
        {
            "/status" => await BuildStatusJson(),
            "/processes" => await BuildProcessesJson(),
            "/info" => BuildInfoJson(),
            _ => await ExecuteShellCommandAsync(command)
        };
    }

    private Task<string> BuildStatusJson()
    {
        try
        {
            // Total/avail — физическая RAM (GlobalMemoryStatusEx), НЕ GC-метрика с pagefile
            var (totalGb, availGb) = DetailedSystemInfoService.GetPhysicalMemory();
            var usedGb = Math.Max(0, totalGb - availGb);

            var status = new
            {
                type = "status",
                time = DateTime.Now.ToString("HH:mm:ss"),
                cpu_cores = Environment.ProcessorCount,
                ram_total_gb = Math.Round(totalGb, 1),
                ram_used_gb = Math.Round(usedGb, 1),
                ram_percent = totalGb > 0
                    ? Math.Round(usedGb / totalGb * 100, 1)
                    : 0,
                os = Environment.OSVersion.VersionString
            };
            return Task.FromResult(JsonSerializer.Serialize(status));
        }
        catch
        {
            return Task.FromResult("{\"type\":\"status\",\"error\":\"failed\"}");
        }
    }

    private Task<string> BuildProcessesJson()
    {
        var procs = new List<object>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                procs.Add(new
                {
                    name = p.ProcessName,
                    pid = p.Id,
                    ram_mb = Math.Round(p.WorkingSet64 / 1_048_576.0, 1)
                });
            }
            catch { }

            if (procs.Count >= 20) break;
        }
        return Task.FromResult(JsonSerializer.Serialize(new { type = "processes", list = procs }));
    }

    private static string BuildInfoJson() => JsonSerializer.Serialize(new
    {
        type = "info",
        machine = Environment.MachineName,
        user = Environment.UserName,
        dotnet = Environment.Version.ToString(),
        os = Environment.OSVersion.VersionString
    });

    // Выполнение shell-команды — только безопасные команды
    private static readonly HashSet<string> _allowedPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ipconfig", "ping", "netstat", "tasklist", "systeminfo", "dir", "echo"
    };

    private static async Task<string> ExecuteShellCommandAsync(string command)
    {
        // Проверяем что команда начинается с разрешённого префикса
        var firstWord = command.Split(' ')[0].TrimStart('/');
        if (!_allowedPrefixes.Contains(firstWord))
        {
            return JsonSerializer.Serialize(new
            {
                type = "error",
                message = $"Command '{firstWord}' is not allowed"
            });
        }

        // Анти-инъекция: allowlist проверял только первое слово, а выполнялась
        // вся строка через cmd.exe — "ping 8.8.8.8 & whoami" проходил.
        // Блокируем chaining/redirect/substitution до строгого парсера аргументов.
        const string forbidden = "&|;$`()<>!\n\r";
        if (command.IndexOfAny(forbidden.ToCharArray()) >= 0)
        {
            return JsonSerializer.Serialize(new
            {
                type = "error",
                message = "Chained commands are not allowed — one simple command only"
            });
        }

        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c {command}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync();
            var error = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();

            var result = string.IsNullOrEmpty(output) ? error : output;
            if (result.Length > 2000) result = result[..2000] + "\n...(truncated)";

            return JsonSerializer.Serialize(new { type = "output", command, output = result });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { type = "error", message = ex.Message });
        }
    }

    // ── Очистка закрытых клиентов ─────────────────────────────────────────────

    private async Task CleanupLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(30_000, ct);

            var toRemove = new List<string>();
            foreach (var (id, ws) in _clients)
            {
                if (ws.State != WebSocketState.Open)
                    toRemove.Add(id);
            }
            foreach (var id in toRemove)
                _clients.TryRemove(id, out _);
        }
    }

    public void Dispose() => Stop();
}