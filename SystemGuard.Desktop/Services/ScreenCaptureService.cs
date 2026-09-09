using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SystemGuard.Desktop.Services;

// Общий захват экрана (GDI+). Один экземпляр лочится: CopyFromScreen
// не потокобезопасен при параллельных вызовах (бот + HTTP MJPEG).
public static class ScreenCaptureService
{
    private static readonly SemaphoreSlim _gate = new(1, 1);

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    public static async Task<byte[]?> CaptureScreenJpegAsync(
        int maxWidth = 1120, int quality = 50, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                try { return CaptureScreenJpeg(maxWidth, quality); }
                catch { return null; }
            }, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public static byte[] CaptureScreenJpeg(int maxWidth = 1120, int quality = 50)
    {
        int vx, vy, vw, vh;
        try
        {
            vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
            vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
            vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            if (vw <= 0 || vh <= 0) { vx = 0; vy = 0; vw = 1920; vh = 1080; }
        }
        catch { vx = 0; vy = 0; vw = 1920; vh = 1080; }

        double scale = vw > maxWidth ? (double)maxWidth / vw : 1.0;
        int w = Math.Max(320, (int)(vw * scale));
        int h = Math.Max(200, (int)(vh * scale));

        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(vx, vy, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);

        var enc = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
        var prm = new EncoderParameters(1);
        prm.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        using var ms = new MemoryStream();
        bmp.Save(ms, enc, prm);
        return ms.ToArray();
    }
}
