// SystemGuard.LogoGen — рисует строгую SG-иконку (только буквы SG)
// и складывает ассеты:
//   Desktop/Assets/logo-{16..256}.png, logo.ico, logo.png (сайдбар),
//   Installer/wizard.bmp (164x314), wizardsmall.bmp (55x58).
using SkiaSharp;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
var assets = Path.Combine(root, "SystemGuard.Desktop", "Assets");
var inst = Path.Combine(root, "SystemGuard.Installer");
Directory.CreateDirectory(assets);
Directory.CreateDirectory(inst);

void DrawLogo(SKCanvas canvas, float s)
{
    // СТРОГАЯ SG-иконка: только буквы SG на тёмном скруглённом квадрате.
    // Без щита, рамок, плашек и градиентов — читается даже в 16px в трее.
    var rect = new SKRect(64 * s, 64 * s, 960 * s, 960 * s);
    using var fill = new SKPaint { Color = new SKColor(0x0F, 0x1F, 0x3C), IsAntialias = true };
    canvas.DrawRoundRect(rect, 232 * s, 232 * s, fill);

    // Буквы SG — крупные, по центру, строгий гротеск.
    SKTypeface face = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold)
        ?? SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold)
        ?? SKTypeface.Default;
    using var text = new SKPaint { Typeface = face, TextSize = 440 * s, Color = SKColors.White, IsAntialias = true, TextAlign = SKTextAlign.Center };
    var fm = text.FontMetrics;
    canvas.DrawText("SG", 512 * s, 512 * s - (fm.Ascent + fm.Descent) / 2, text);
}

byte[] RenderLogo(int size)
{
    using var bmp = new SKBitmap(size, size);
    using var canvas = new SKCanvas(bmp);
    canvas.Clear(SKColors.Transparent);
    DrawLogo(canvas, size / 1024f);
    using var img = SKImage.FromBitmap(bmp);
    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

foreach (var size in new[] { 16, 24, 32, 48, 64, 128, 256 })
    File.WriteAllBytes(Path.Combine(assets, $"logo-{size}.png"), RenderLogo(size));
File.WriteAllBytes(Path.Combine(assets, "logo.png"), RenderLogo(256));

// ICO: PNG-сжатые записи (Vista+ понимает)
{
    int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
    int offset = 6 + 16 * sizes.Length;
    var blobs = sizes.Select(s => RenderLogo(s)).ToArray();
    for (int i = 0; i < sizes.Length; i++)
    {
        w.Write(sizes[i] >= 256 ? (byte)0 : (byte)sizes[i]);
        w.Write(sizes[i] >= 256 ? (byte)0 : (byte)sizes[i]);
        w.Write((byte)0); w.Write((byte)0);
        w.Write((ushort)1); w.Write((ushort)32);
        w.Write(blobs[i].Length); w.Write(offset);
        offset += blobs[i].Length;
    }
    foreach (var b in blobs) w.Write(b);
    File.WriteAllBytes(Path.Combine(assets, "logo.ico"), ms.ToArray());
}

// Картинки мастера Inno Setup — строгий тёмно-синий фон под новую иконку.
void WizardImage(string path, int w, int h, bool big)
{
    using var bmp = new SKBitmap(w, h);
    using var canvas = new SKCanvas(bmp);
    using var bg = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(w, h),
        new SKColor[] { new(0x0F, 0x1F, 0x3C), new(0x14, 0x2E, 0x5C), new(0x0A, 0x66, 0xFF) },
        new float[] { 0f, 0.7f, 1f }, SKShaderTileMode.Clamp);
    canvas.DrawRect(new SKRect(0, 0, w, h), new SKPaint { Shader = bg });
    if (big)
    {
        canvas.Save();
        canvas.Translate(18, 24);
        canvas.Scale(128f / 1024f);
        DrawLogo(canvas, 1f);
        canvas.Restore();
        var face = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold);
        using var p1 = new SKPaint { Typeface = face, TextSize = 19, Color = SKColors.White, IsAntialias = true };
        canvas.DrawText("SYSTEM", 18, 190, p1);
        canvas.DrawText("GUARD", 18, 214, p1);
        using var p2 = new SKPaint { Typeface = SKTypeface.FromFamilyName("Segoe UI"), TextSize = 12, Color = new SKColor(0xC7, 0xD7, 0xFE), IsAntialias = true };
        canvas.DrawText("Монитор, твикер", 18, 244, p2);
        canvas.DrawText("и пульт для ПК", 18, 262, p2);
    }
    else
    {
        canvas.Save();
        canvas.Translate(6, 7);
        canvas.Scale(44f / 1024f);
        DrawLogo(canvas, 1f);
        canvas.Restore();
    }
    using var img = SKImage.FromBitmap(bmp);
    using var png = img.Encode(SKEncodedImageFormat.Png, 100);
    // BMP пишем через System.Drawing (энкодер Skia под Windows отдаёт null).
    using var ms = new MemoryStream(png.ToArray());
    using var sd = System.Drawing.Image.FromStream(ms);
    using var canvasBmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
    using (var g = System.Drawing.Graphics.FromImage(canvasBmp))
        g.DrawImage(sd, 0, 0, w, h);
    canvasBmp.Save(path, System.Drawing.Imaging.ImageFormat.Bmp);
}
WizardImage(Path.Combine(inst, "wizard.bmp"), 164, 314, big: true);
WizardImage(Path.Combine(inst, "wizardsmall.bmp"), 55, 58, big: false);

Console.WriteLine($"Assets -> {assets}");
Console.WriteLine($"Installer images -> {inst}");
return 0;
