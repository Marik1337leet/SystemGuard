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

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(out CURSORINFO pci);

    [DllImport("user32.dll")]
    private static extern bool DrawIcon(IntPtr hDC, int x, int y, IntPtr hIcon);

    private const int CURSOR_SHOWING = 0x00000001;

    public static async Task<byte[]?> CaptureScreenJpegAsync(
        int maxWidth = 1280, int quality = 60, CancellationToken ct = default)
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

    public static byte[] CaptureScreenJpeg(int maxWidth = 1280, int quality = 60)
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

        // Весь виртуальный экран (все мониторы, напр. 1920x1080) — сначала
        // захватываем ПОЛНЫЙ кадр, потом уменьшаем. Раньше CopyFromScreen
        // копировал сразу в уменьшенный битмап и отдавал только левый верхний
        // угол вместо всего экрана.
        double scale = vw > maxWidth ? (double)maxWidth / vw : 1.0;
        int w = Math.Max(320, (int)(vw * scale));
        int h = Math.Max(200, (int)(vh * scale));

        using var full = new Bitmap(vw, vh);
        using (var g = Graphics.FromImage(full))
        {
            g.CopyFromScreen(vx, vy, 0, 0, new Size(vw, vh), CopyPixelOperation.SourceCopy);
            // Рисуем курсор вручную: CopyFromScreen его не захватывает,
            // без этого в трансляции не видно куда ведёшь мышь.
            try
            {
                var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
                if (GetCursorInfo(out ci) && ci.flags == CURSOR_SHOWING && ci.hCursor != IntPtr.Zero)
                {
                    int cx = ci.ptScreenPos.X - vx;
                    int cy = ci.ptScreenPos.Y - vy;
                    if (cx >= 0 && cy >= 0 && cx < vw && cy < vh)
                    {
                        IntPtr hdc = g.GetHdc();
                        try { DrawIcon(hdc, cx, cy, ci.hCursor); }
                        finally { g.ReleaseHdc(hdc); }
                    }
                }
            }
            catch { }
        }

        Bitmap work = full;
        Bitmap? scaled = null;
        try
        {
            if (w != vw || h != vh)
            {
                scaled = new Bitmap(w, h);
                using (var g = Graphics.FromImage(scaled))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                    g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
                    g.DrawImage(full, 0, 0, w, h);
                }
                work = scaled;
            }
            var enc = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
            var prm = new EncoderParameters(1);
            prm.Param[0] = new EncoderParameter(Encoder.Quality, Math.Clamp(quality, 30, 90));
            using var ms = new MemoryStream();
            work.Save(ms, enc, prm);
            return ms.ToArray();
        }
        finally { if (scaled != null) scaled.Dispose(); }
    }
}
