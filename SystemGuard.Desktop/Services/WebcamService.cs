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
            var devices = new CaptureDevices();
            var descriptors = devices.EnumerateDescriptors()
                .Where(d => d.Characteristics.Any(c => c.PixelFormat != PixelFormats.Unknown))
                .ToList();
            if (descriptors.Count == 0)
                return (null, "No camera found (no DirectShow/MediaFoundation device)");

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

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            var image = descriptor.TakeOneShotAsync(ch, linked.Token)
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
