using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace SystemGuard.Desktop.Services;

// Яркость через CIM (WMI). Работает на ноутбуках/моноблоках с драйвером
// монитора; на десктопах с внешним монитором без DDC чаще всего
// не поддерживается — тогда честно возвращаем ошибку, а не тишину.
public static class BrightnessService
{
    public static async Task<(bool Ok, string Message)> SetAsync(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        // Get-CimInstance надёжнее Get-WmiObject (его нет в PS7) и не требует
        // парсинга: берём первый метод-объект и вызываем WmiSetBrightness.
        var ps = "powershell -NoProfile -NonInteractive -Command " +
            "\"$m=Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightnessMethods -ErrorAction Stop | Select-Object -First 1; " +
            $"Invoke-CimMethod -InputObject $m -MethodName WmiSetBrightness -Arguments @{{Timeout=1; Brightness={percent}}} -ErrorAction Stop | Out-Null; " +
            "echo BRIGHTNESS_OK\"";
        var (code, output) = await RunAsync(ps, 15000).ConfigureAwait(false);
        if (code == 0 && output.Contains("BRIGHTNESS_OK"))
            return (true, $"Brightness: {percent}%");
        var err = output.Trim();
        if (err.Length == 0) err = "no WMI brightness interface";
        if (err.Contains("Not supported", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("Invalid namespace", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("Invalid class", StringComparison.OrdinalIgnoreCase))
            err = "display does not expose brightness control (desktop monitor?)";
        return (false, $"Brightness failed: {Trim(err, 180)}");
    }

    private static async Task<(int Code, string Output)> RunAsync(string cmd, int timeoutMs)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo("cmd.exe", $"/c {cmd}")
                {
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true,
                    StandardOutputEncoding = CmdEncoding.Oem, StandardErrorEncoding = CmdEncoding.Oem
                }
            };
            p.Start();
            var oTask = p.StandardOutput.ReadToEndAsync();
            var eTask = p.StandardError.ReadToEndAsync();
            var exited = await Task.Run(() => p.WaitForExit(timeoutMs)).ConfigureAwait(false);
            if (!exited) { try { p.Kill(); } catch { } return (1, "Timed out"); }
            return (p.ExitCode, (await oTask.ConfigureAwait(false)) + (await eTask.ConfigureAwait(false)));
        }
        catch (Exception ex) { return (1, ex.Message); }
    }

    private static string Trim(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");
}
