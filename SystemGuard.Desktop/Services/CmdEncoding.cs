using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace SystemGuard.Desktop.Services;

// Единый запуск cmd.exe БЕЗ кракозябр.
// Проблема была: StandardOutputEncoding = UTF8, а cmd.exe на RU-Windows
// пишет в OEM-кодовой странице (обычно CP866). Кириллица превращалась
// в "неизвестные символы". Теперь читаем в кодировке консоли.
public static class CmdEncoding
{
    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleCP();

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleOutputCP();

    public static Encoding Oem
    {
        get
        {
            try
            {
                var cp = GetConsoleOutputCP();
                if (cp == 0) cp = GetConsoleCP();
                if (cp != 0) return Encoding.GetEncoding((int)cp);
            }
            catch { }
            // RU-Windows по умолчанию: 866
            try { return Encoding.GetEncoding(866); } catch { }
            return Encoding.UTF8;
        }
    }

    public static async Task<string> RunAsync(string cmd, int timeoutMs = 30000)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo("cmd.exe", $"/c {cmd}")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Oem,
                    StandardErrorEncoding = Oem
                }
            };
            p.Start();
            var oTask = p.StandardOutput.ReadToEndAsync();
            var eTask = p.StandardError.ReadToEndAsync();
            var exited = await Task.Run(() => p.WaitForExit(timeoutMs)).ConfigureAwait(false);
            if (!exited) { try { p.Kill(); } catch { } return "Timed out"; }
            var o = await oTask.ConfigureAwait(false);
            var e = await eTask.ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(o)) return e ?? "";
            if (!string.IsNullOrWhiteSpace(e)) return o + "\n" + e;
            return o;
        }
        catch (Exception ex) { return ex.Message; }
    }

    public static string Clean(string? s, int max = 3800)
    {
        if (string.IsNullOrWhiteSpace(s)) return "(no output)";
        s = s.Trim();
        // Убираем мусорные \0 и управляющие символы кроме \n\r\t
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c == '\0') continue;
            if (char.IsControl(c) && c != '\n' && c != '\r' && c != '\t') continue;
            sb.Append(c);
        }
        var t = sb.ToString();
        return t.Length <= max ? t : t[..max] + "\n…(truncated)";
    }
}
