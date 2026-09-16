using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FlashCap;

namespace SystemGuard.Desktop.Services;

// Вебкамера через FlashCap (DirectShow / MediaFoundation / VFW —
// библиотека сама выбирает рабочий бэкенд, поэтому камера находится
// там, где голый avicap32 ничего не видел).
public static class WebcamService
{
    private static readonly SemaphoreSlim _gate = new(1, 1);

    public static async Task<(byte[]? Jpeg, string Error)> CaptureJpegAsync(
        int maxWidth = 960, CancellationToken ct = default)
    {
        // Если идёт стрим — отдаём свежайший кадр сессии (мгновенно,
        // без второго открытия устройства, иначе "busy").
        var live = TryGetStreamFrame(maxWidth);
        if (live != null) return (live, "");
        // Камеру не дёргаем параллельно (бот + HTTP): железо одно
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false))
            return (null, "Camera is busy, try again");
        try
        {
            return await Task.Run(() => CaptureOnce(maxWidth, ct), ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private static (byte[]?, string) CaptureOnce(int maxWidth, CancellationToken ct)
    {
        try
        {
            var pick = PickDevice();
            if (pick.Descriptor == null)
                return (null, pick.Error);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            var image = pick.Descriptor.TakeOneShotAsync(pick.Characteristics, linked.Token)
                .ConfigureAwait(false).GetAwaiter().GetResult();
            if (image == null || image.Length < 100)
                return (null, "Camera returned an empty frame (busy in another app?)");

            var jpeg = ToJpeg(image, maxWidth);
            return jpeg == null
                ? (null, "Could not decode camera frame")
                : (jpeg, "");
        }
        catch (OperationCanceledException)
        {
            return (null, "Camera timeout (busy in another app?)");
        }
        catch (Exception ex)
        {
            return (null, Trim(ex.Message, 160));
        }
    }

    private static (CaptureDeviceDescriptor? Descriptor, VideoCharacteristics Characteristics, string Error) PickDevice()
    {
        try
        {
            var devices = new CaptureDevices();
            var descriptors = devices.EnumerateDescriptors()
                .Where(d => d.Characteristics.Any(c => c.PixelFormat != PixelFormats.Unknown))
                .ToList();
            if (descriptors.Count == 0)
                return (null, default, "No camera found (no DirectShow/MediaFoundation device)");

            // Предпочитаем "настоящую" камеру виртуальным, MJPEG ≤1280 для скорости
            var descriptor = descriptors
                .OrderBy(d => d.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(d => d.Name.Contains("OBS", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .First();

            var ch = descriptor.Characteristics
                .Where(c => c.PixelFormat != PixelFormats.Unknown)
                .OrderBy(c => IsJpeg(c) ? 0 : 1)
                .ThenBy(c => Math.Abs(c.Width - 1280))
                .First();
            return (descriptor, ch, "");
        }
        catch (Exception ex)
        {
            return (null, default, Trim(ex.Message, 160));
        }
    }

    // ── Постоянная сессия для MJPEG-реалтайма ─────────────────────────────
    // Было: устройство открывалось и закрывалось на КАЖДЫЙ кадр —
    // потолок 5–12 FPS + мигание LED. Стало: открыли один раз,
    // кадры забираем из колбэка. Счётчик ссылок: зрителей может быть
    // несколько (Экран не в счёт — у камеры один: WebApp), устройство
    // гаснет когда ушёл последний (Стоп → LED тухнет сразу).

    private static readonly object _sessionLock = new();
    private static CaptureDevice? _sessionDevice;
    private static byte[]? _sessionRaw;
    private static int _sessionRefs;

    public static async Task<(bool Ok, string Error)> AcquireStreamAsync(CancellationToken ct)
    {
        lock (_sessionLock)
        {
            _sessionRefs++;
            if (_sessionDevice != null) return (true, "");
        }

        CaptureDevice? device = null;
        string err = "";
        try
        {
            var pick = PickDevice();
            if (pick.Descriptor == null) { err = pick.Error; device = null; }
            else
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                CaptureDevice? opened = null;
                PixelBufferArrivedDelegate onFrame = e =>
                {
                    try
                    {
                        // ReferImage отдаёт ArraySegment (буфер устройства) — копируем себе.
                        var seg = e.Buffer.ReferImage();
                        if (seg.Array == null || seg.Count < 100) return;
                        var img = new byte[seg.Count];
                        Buffer.BlockCopy(seg.Array, seg.Offset, img, 0, seg.Count);
                        lock (_sessionLock)
                        {
                            if (opened != null && ReferenceEquals(_sessionDevice, opened)) _sessionRaw = img;
                        }
                    }
                    catch { }
                };
                var dev = await pick.Descriptor.OpenAsync(pick.Characteristics, onFrame, linked.Token).ConfigureAwait(false);
                opened = dev;
                await dev.StartAsync(linked.Token).ConfigureAwait(false);
                device = dev;
            }
        }
        catch (OperationCanceledException) { err = "Camera timeout (busy in another app?)"; }
        catch (Exception ex) { err = Trim(ex.Message, 160); }

        lock (_sessionLock)
        {
            if (device != null && _sessionDevice == null)
            {
                _sessionDevice = device;
                _sessionRaw = null;
                return (true, "");
            }
        }
        if (device != null)
        {
            try { await device.StopAsync().ConfigureAwait(false); } catch { }
            try { device.Dispose(); } catch { }
        }
        lock (_sessionLock)
        {
            if (_sessionDevice != null) return (true, "");
            _sessionRefs = Math.Max(0, _sessionRefs - 1);
            return (false, string.IsNullOrEmpty(err) ? "Camera busy" : err);
        }
    }

    public static void ReleaseStream()
    {
        CaptureDevice? toStop = null;
        lock (_sessionLock)
        {
            _sessionRefs = Math.Max(0, _sessionRefs - 1);
            if (_sessionRefs == 0 && _sessionDevice != null)
            {
                toStop = _sessionDevice;
                _sessionDevice = null;
                _sessionRaw = null;
            }
        }
        if (toStop != null)
        {
            try { toStop.StopAsync().ConfigureAwait(false).GetAwaiter().GetResult(); } catch { }
            try { toStop.Dispose(); } catch { }
        }
    }

    /// <summary>Свежайший кадр сессии (уже JPEG, ужатый). Null — кадра пока нет.</summary>
    public static byte[]? TryGetStreamFrame(int maxWidth = 640)
    {
        byte[]? raw;
        lock (_sessionLock) raw = _sessionRaw;
        if (raw == null || raw.Length < 100) return null;
        return ToJpeg(raw, maxWidth);
    }

    private static bool IsJpeg(VideoCharacteristics c)
    {
        try
        {
            return c.PixelFormat == PixelFormats.JPEG ||
                   c.PixelFormat == PixelFormats.PNG;
        }
        catch { return false; }
    }

    // Кадр может быть JPEG, PNG или DIB — приводим всё к компактному JPEG
    private static byte[]? ToJpeg(byte[] image, int maxWidth)
    {
        try
        {
            if (image.Length > 2 && image[0] == 0xFF && image[1] == 0xD8 && maxWidth >= 1280)
                return image; // уже JPEG и небольшой — отдаём как есть
            using var ms = new MemoryStream(image);
            using var bmp = (Bitmap)Image.FromStream(ms);
            Bitmap work = bmp;
            try
            {
                if (bmp.Width > maxWidth)
                {
                    int h = Math.Max(1, (int)(bmp.Height * (double)maxWidth / bmp.Width));
                    work = new Bitmap(bmp, new Size(maxWidth, h));
                }
                var enc = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
                var prm = new EncoderParameters(1);
                prm.Param[0] = new EncoderParameter(Encoder.Quality, 70L);
                using var out1 = new MemoryStream();
                work.Save(out1, enc, prm);
                return out1.ToArray();
            }
            finally { if (!ReferenceEquals(work, bmp)) work.Dispose(); }
        }
        catch { return null; }
    }

    private static string Trim(string? s, int n) =>
        string.IsNullOrEmpty(s) ? "Unknown error" : (s.Length <= n ? s : s[..n] + "…");
}
