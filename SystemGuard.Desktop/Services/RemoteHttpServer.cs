using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SystemGuard.Desktop.Services;

// Локальный HTTP API для WebApp live-режима:
//   GET  /api/status          — RAM/диски/батарея/громкость JSON
//   GET  /api/shot.jpg?w=&q=  — скриншот
//   GET  /api/cam.jpg         — кадр вебкамеры (кэш 4с)
//   GET  /api/mjpeg?fps=&q=   — живой MJPEG-поток экрана
//   GET  /api/file?path=      — скачать файл с ПК (до 50 МБ)
//   POST /api/action          — {action, arg} -> команда (как в боте)
// Авторизация: ?token= (токен генерируется один раз, хранится локально).
// Слушаем localhost всегда; LAN (http://*:8899) — если хватило прав/резервации.
public sealed class RemoteHttpServer : IDisposable
{
    public const int Port = 8899;

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    // Мгновенный стоп MJPEG-потоков + несколько зрителей сразу
    // (основной кадр на вкладке Экран + зеркало в карточке мыши).
    // /api/stop закрывает ВСЕ соединения вида — клиент ничего не доедает.
    private readonly object _streamLock = new();
    private readonly HashSet<CancellationTokenSource> _mjpegStreams = new();
    private readonly HashSet<CancellationTokenSource> _camStreams = new();

    public string Token { get; }
    public int BoundPort { get; }
    public bool IsRunning { get; private set; }
    public bool LanAvailable { get; private set; }
    public string LanHint { get; private set; } = "";

    public RemoteHttpServer(string token, int port = Port)
    {
        Token = token;
        BoundPort = port;
    }

    public void Start()
    {
        if (IsRunning || _disposed) return;
        _cts = new CancellationTokenSource();
        _listener = new HttpListener();

        // Сначала пробуем wildcard (LAN), при отказе — только localhost
        try
        {
            _listener.Prefixes.Add($"http://*:{BoundPort}/");
            _listener.Start();
            LanAvailable = true;
        }
        catch
        {
            try { _listener.Prefixes.Clear(); } catch { }
            try { _listener.Close(); } catch { }
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{BoundPort}/");
            _listener.Prefixes.Add($"http://localhost:{BoundPort}/");
            _listener.Start();
            LanAvailable = false;
            LanHint = $"For LAN access run as admin once: netsh http add urlacl url=http://*:{BoundPort}/ user=Everyone";
        }

        IsRunning = true;
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        lock (_streamLock)
        {
            foreach (var c in _mjpegStreams) try { c.Cancel(); } catch { }
            foreach (var c in _camStreams) try { c.Cancel(); } catch { }
            _mjpegStreams.Clear();
            _camStreams.Clear();
        }
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        _listener = null;
        try { _cts?.Dispose(); } catch { }
        _cts = null;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener?.IsListening == true)
        {
            HttpListenerContext? ctx = null;
            try { ctx = await _listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch { continue; }
            _ = Task.Run(() => HandleAsync(ctx), ct);
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Иначе кириллица уезжает в \uXXXX и в WebApp видны "неизвестные символы"
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private bool Authorized(HttpListenerRequest req)
    {
        var t = req.QueryString["token"];
        if (!string.IsNullOrEmpty(t) && SlowEquals(t, Token)) return true;
        var h = req.Headers["X-Token"];
        return !string.IsNullOrEmpty(h) && SlowEquals(h, Token);
    }

    private static bool SlowEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        int d = 0;
        for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i];
        return d == 0;
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            res.AddHeader("Access-Control-Allow-Origin", "*");
            res.AddHeader("Access-Control-Allow-Headers", "Content-Type, X-Token, Authorization");
            res.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            // WebView на телефоне шлёт preflight чаще десктопа — кэшируем разрешение на сутки.
            res.AddHeader("Access-Control-Max-Age", "86400");
            if (req.HttpMethod == "OPTIONS") { res.StatusCode = 204; res.Close(); return; }

            var path = (req.Url?.AbsolutePath ?? "/").ToLowerInvariant();
            if (path == "/" || path == "/api" || path == "/api/")
            {
                // Probe для проверки туннеля снаружи: минимум информации
                // (без версии — не палим релиз сканерам). SelfTest ждёт только "ok".
                await WriteJson(res, new { ok = true, service = "SystemGuard Remote" }).ConfigureAwait(false);
                return;
            }
            if (!Authorized(req))
            {
                await WriteJson(res, new { ok = false, error = "Bad token" }, 401).ConfigureAwait(false);
                return;
            }

            switch (path)
            {
                case "/api/status":
                    await WriteJson(res, RemoteActions.GetStatus()).ConfigureAwait(false);
                    break;
                case "/api/shot.jpg":
                {
                    int w = QInt(req, "w", 1280), q = QInt(req, "q", 60);
                    var jpg = await ScreenCaptureService.CaptureScreenJpegAsync(w, q).ConfigureAwait(false);
                    if (jpg == null) { await WriteJson(res, new { ok = false, error = "Capture failed" }, 500).ConfigureAwait(false); break; }
                    await WriteBytes(res, jpg, "image/jpeg").ConfigureAwait(false);
                    break;
                }
                case "/api/cam.jpg":
                {
                    var (jpg, err) = await RemoteActions.GetCamJpegAsync().ConfigureAwait(false);
                    if (jpg == null) { await WriteJson(res, new { ok = false, error = err }, 503).ConfigureAwait(false); break; }
                    await WriteBytes(res, jpg, "image/jpeg").ConfigureAwait(false);
                    break;
                }
                case "/api/mjpeg":
                {
                    int fps = Math.Clamp(QInt(req, "fps", 30), 1, 60);
                    int q2 = Math.Clamp(QInt(req, "q", 60), 30, 90);
                    int w = Math.Clamp(QInt(req, "w", 1280), 320, 1920);
                    var stream = TakeStreamToken(false);
                    try { await ServeMjpegAsync(res, fps, q2, w, false, stream.Token).ConfigureAwait(false); }
                    finally { DropStreamToken(false, stream); }
                    break;
                }
                case "/api/cammjpeg":
                {
                    int fps = Math.Clamp(QInt(req, "fps", 30), 1, 60);
                    int q2 = Math.Clamp(QInt(req, "q", 65), 30, 90);
                    var stream = TakeStreamToken(true);
                    try { await ServeMjpegAsync(res, fps, q2, 0, true, stream.Token).ConfigureAwait(false); }
                    finally { DropStreamToken(true, stream); }
                    break;
                }
                case "/api/stop":
                {
                    // Мгновенный стоп трансляций: kind=mjpeg | cam | all.
                    var kind = (req.QueryString["kind"] ?? "all").ToLowerInvariant();
                    var stopped = StopStreams(kind);
                    await WriteJson(res, new { ok = true, stopped }).ConfigureAwait(false);
                    break;
                }
                case "/api/policy":
                {
                    var kind = (req.QueryString["kind"] ?? "safety").ToLowerInvariant();
                    string text = kind switch
                    {
                        "privacy" => PoliciesService.Privacy,
                        "terms" => PoliciesService.Terms,
                        _ => PoliciesService.Safety
                    };
                    await WriteJson(res, new { ok = true, kind, text }).ConfigureAwait(false);
                    break;
                }
                case "/api/file":
                {
                    // Прямая ссылка туннеля: лимит 500 МБ (в чат бот всё равно
                    // упрётся в лимит Bot API 50 МБ — большие файлы только отсюда).
                    var p = (req.QueryString["path"] ?? "").Trim().Trim('"');
                    var blocked = SelfProtection.BlockedForRemoteRead(p);
                    if (blocked != null) { await WriteJson(res, new { ok = false, error = blocked }, 403).ConfigureAwait(false); break; }
                    if (!File.Exists(p) || new FileInfo(p).Length > 500L * 1024 * 1024)
                    { await WriteJson(res, new { ok = false, error = "File not found or over 500 MB" }, 404).ConfigureAwait(false); break; }
                    res.ContentType = "application/octet-stream";
                    res.AddHeader("Content-Disposition", $"attachment; filename=\"{Path.GetFileName(p)}\"");
                    await using (var fs = File.Open(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        await fs.CopyToAsync(res.OutputStream).ConfigureAwait(false);
                    res.Close();
                    break;
                }
                case "/api/action":
                {
                    if (req.HttpMethod != "POST") { await WriteJson(res, new { ok = false, error = "POST only" }, 405).ConfigureAwait(false); break; }
                    string body;
                    using (var sr = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
                        body = await sr.ReadToEndAsync().ConfigureAwait(false);
                    string action = "", arg = "";
                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        var r = doc.RootElement;
                        if (r.TryGetProperty("action", out var a)) action = a.GetString() ?? "";
                        if (r.TryGetProperty("arg", out var g))
                            arg = g.ValueKind == JsonValueKind.String ? g.GetString() ?? "" : g.ToString();
                    }
                    catch { }
                    var result = await RemoteActions.ExecuteAsync(action, arg).ConfigureAwait(false);
                    await WriteJson(res, result).ConfigureAwait(false);
                    break;
                }
                default:
                    await WriteJson(res, new { ok = false, error = "Unknown endpoint" }, 404).ConfigureAwait(false);
                    break;
            }
        }
        catch { try { res.Abort(); } catch { } }
    }

    /// <summary>
    /// Регистрирует новый стрим. Старые того же вида НЕ гасим:
    /// зрителя может быть два (Экран + зеркало у мыши), брошенные
    /// соединения умирают сами при обрыве записи в сокет.
    /// </summary>
    private CancellationTokenSource TakeStreamToken(bool cam)
    {
        var cts = new CancellationTokenSource();
        lock (_streamLock) { (cam ? _camStreams : _mjpegStreams).Add(cts); }
        return cts;
    }

    private void DropStreamToken(bool cam, CancellationTokenSource cts)
    {
        lock (_streamLock) { (cam ? _camStreams : _mjpegStreams).Remove(cts); }
        try { cts.Dispose(); } catch { }
    }

    /// <summary>Мгновенно гасит ВСЕ стримы вида. Возвращает что остановлено.</summary>
    private string StopStreams(string kind)
    {
        lock (_streamLock)
        {
            bool mj = kind is "mjpeg" or "screen" or "all";
            bool cm = kind is "cam" or "camera" or "all";
            if (mj) { foreach (var c in _mjpegStreams) try { c.Cancel(); } catch { } _mjpegStreams.Clear(); }
            if (cm) { foreach (var c in _camStreams) try { c.Cancel(); } catch { } _camStreams.Clear(); }
            if (!mj && !cm) return "none";
            return mj && cm ? "all" : mj ? "mjpeg" : "cam";
        }
    }

    private async Task ServeMjpegAsync(HttpListenerResponse res, int fps, int quality, int width, bool cam, CancellationToken streamToken)
    {
        const string boundary = "--sgframe";
        res.ContentType = $"multipart/x-mixed-replace; boundary={boundary[2..]}";
        res.SendChunked = true;
        res.StatusCode = 200;
        res.AddHeader("Cache-Control", "no-store");
        var delay = TimeSpan.FromMilliseconds(1000.0 / fps);
        var header = Encoding.ASCII.GetBytes(
            $"{boundary}\r\nContent-Type: image/jpeg\r\nContent-Length: ");
        // Камера: постоянная сессия (реалтайм). Не открылась — падаем
        // на покадровые one-shot (старое поведение), стрим всё равно идёт.
        bool camSession = false;
        if (cam)
        {
            try
            {
                var acq = await WebcamService.AcquireStreamAsync(streamToken).ConfigureAwait(false);
                camSession = acq.Ok;
            }
            catch { camSession = false; }
        }
        try
        {
            while (IsRunning && !streamToken.IsCancellationRequested)
            {
                byte[]? jpg;
                if (cam)
                {
                    if (camSession)
                    {
                        jpg = WebcamService.TryGetStreamFrame(640);
                    }
                    else
                    {
                        var (frame, _) = await RemoteActions.GetCamJpegFreshAsync().ConfigureAwait(false);
                        jpg = frame;
                    }
                }
                else
                {
                    jpg = await ScreenCaptureService.CaptureScreenJpegAsync(width > 0 ? width : 960, quality).ConfigureAwait(false);
                }
                if (streamToken.IsCancellationRequested) break; // стоп — сразу, кадр в сеть не идёт
                if (jpg == null) { try { await Task.Delay(delay, streamToken).ConfigureAwait(false); } catch { break; } continue; }
                var tail = Encoding.ASCII.GetBytes(jpg.Length + "\r\n\r\n");
                try
                {
                    await res.OutputStream.WriteAsync(header, streamToken).ConfigureAwait(false);
                    await res.OutputStream.WriteAsync(tail, streamToken).ConfigureAwait(false);
                    await res.OutputStream.WriteAsync(jpg, streamToken).ConfigureAwait(false);
                    await res.OutputStream.WriteAsync(Encoding.ASCII.GetBytes("\r\n"), streamToken).ConfigureAwait(false);
                    await res.OutputStream.FlushAsync(streamToken).ConfigureAwait(false);
                }
                catch { break; /* клиент отключился или стоп */ }
                try { await Task.Delay(delay, streamToken).ConfigureAwait(false); } catch { break; }
            }
        }
        catch { /* клиент отключился */ }
        finally
        {
            if (camSession) { try { WebcamService.ReleaseStream(); } catch { } }
            try { res.Close(); } catch { }
        }
    }

    private static int QInt(HttpListenerRequest req, string key, int def)
    {
        return int.TryParse(req.QueryString[key], out var v) ? v : def;
    }

    private static async Task WriteJson(HttpListenerResponse res, object obj, int status = 200)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(obj, JsonOpts);
        res.ContentType = "application/json; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        res.StatusCode = status;
        try { await res.OutputStream.WriteAsync(bytes).ConfigureAwait(false); } catch { }
        try { res.Close(); } catch { }
    }

    private static async Task WriteBytes(HttpListenerResponse res, byte[] bytes, string mime)
    {
        res.ContentType = mime;
        res.ContentLength64 = bytes.Length;
        res.StatusCode = 200;
        res.AddHeader("Cache-Control", "no-store");
        try { await res.OutputStream.WriteAsync(bytes).ConfigureAwait(false); } catch { }
        try { res.Close(); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
