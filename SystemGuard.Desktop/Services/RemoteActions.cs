using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SystemGuard.Desktop.Services;

// Единая точка исполнения команд для HTTP live-API.
// Бот вызывает те же базовые сервисы (Volume/Brightness/Webcam/Screen),
// здесь — тонкая обвязка с JSON-результатами.
public static class RemoteActions
{
    private static byte[]? _camCache;
    private static DateTime _camCacheAt;
    private static readonly SemaphoreSlim _camGate = new(1, 1);

    public static object GetStatus()
    {
        try
        {
            var (totalGb, availGb) = DetailedSystemInfoService.GetPhysicalMemory();
            var usedGb = Math.Max(0, totalGb - availGb);
            var upStr = SystemUptime.UptimeText;
            string batt;
            try
            {
                var b = new PowerService().GetBatteryInfo();
                batt = b == null ? "desktop" : $"{b.ChargePercent}% ({b.Status})";
            }
            catch { batt = "?"; }
            var disks = new List<object>();
            try
            {
                foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
                    disks.Add(new
                    {
                        name = d.Name,
                        freeGb = Math.Round(d.AvailableFreeSpace / 1073741824.0, 1),
                        totalGb = Math.Round(d.TotalSize / 1073741824.0, 1)
                    });
            }
            catch { }
            return new
            {
                ok = true,
                time = DateTime.Now.ToString("HH:mm:ss"),
                machine = Environment.MachineName,
                os = Environment.OSVersion.VersionString,
                cores = Environment.ProcessorCount,
                ramTotalGb = Math.Round(totalGb, 1),
                ramUsedGb = Math.Round(usedGb, 1),
                ramPct = totalGb > 0 ? Math.Round(usedGb / totalGb * 100, 1) : 0,
                vol = VolumeService.GetPercent(),
                battery = batt,
                uptime = upStr,
                disks
            };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = ex.Message };
        }
    }

    public static async Task<(byte[]? Jpeg, string Error)> GetCamJpegAsync()
    {
        // Кадр кэшируем на 4с: и бот, и HTTP дёргают часто
        await _camGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_camCache != null && (DateTime.UtcNow - _camCacheAt).TotalSeconds < 4)
                return (_camCache, "");
            var (jpg, err) = await WebcamService.CaptureJpegAsync().ConfigureAwait(false);
            if (jpg != null) { _camCache = jpg; _camCacheAt = DateTime.UtcNow; }
            return (jpg, err);
        }
        finally { _camGate.Release(); }
    }

    public static async Task<object> ExecuteAsync(string action, string arg)
    {
        action = (action ?? "").Trim().TrimStart('/').ToLowerInvariant();
        arg = (arg ?? "").Trim();
        try
        {
            switch (action)
            {
                case "status": return GetStatus();
                case "volume":
                {
                    if (arg is "up" or "+") { await Task.Run(() => VolumeService.StepUp()).ConfigureAwait(false); return Ok($"Volume: {VolumeService.GetPercent()}%"); }
                    if (arg is "down" or "-") { await Task.Run(() => VolumeService.StepDown()).ConfigureAwait(false); return Ok($"Volume: {VolumeService.GetPercent()}%"); }
                    if (arg is "mute" or "0" && !HasDigits(arg)) { await Task.Run(() => VolumeService.MuteToggle()).ConfigureAwait(false); return Ok("Muted"); }
                    var digits = new string(arg.Where(char.IsDigit).ToArray());
                    if (int.TryParse(digits, out int v))
                    {
                        var set = await Task.Run(() => VolumeService.SetPercent(v)).ConfigureAwait(false);
                        return set >= 0 ? Ok($"Volume: {set}%") : Fail("Volume control unavailable");
                    }
                    return Fail("Use 0-100, up, down, mute");
                }
                case "mute":
                    await Task.Run(() => VolumeService.MuteToggle()).ConfigureAwait(false);
                    await Task.Delay(150).ConfigureAwait(false);
                    return Ok(VolumeService.IsMuted() ? "Sound muted" : $"Sound on ({VolumeService.GetPercent()}%)");
                case "brightness":
                {
                    var digits = new string(arg.Where(char.IsDigit).ToArray());
                    if (!int.TryParse(digits, out int b)) return Fail("Use 0-100");
                    var (ok, msg) = await BrightnessService.SetAsync(b).ConfigureAwait(false);
                    return ok ? Ok(msg) : Fail(msg);
                }
                case "play": case "pause":
                    await VolumeService.MediaAsync(VolumeService.VK_MEDIA_PLAY_PAUSE).ConfigureAwait(false);
                    return Ok("Play/Pause");
                case "next":
                    await VolumeService.MediaAsync(VolumeService.VK_MEDIA_NEXT).ConfigureAwait(false);
                    return Ok("Next track");
                case "prev":
                    await VolumeService.MediaAsync(VolumeService.VK_MEDIA_PREV).ConfigureAwait(false);
                    return Ok("Previous track");
                case "shutdown": await ExecAsync("shutdown /s /t 60").ConfigureAwait(false); return Ok("Shutdown in 60s (/cancel aborts)");
                case "restart": await ExecAsync("shutdown /r /t 60").ConfigureAwait(false); return Ok("Restart in 60s");
                case "cancel": await ExecAsync("shutdown /a").ConfigureAwait(false); return Ok("Timer cancelled");
                case "sleep": await ExecAsync("rundll32.exe powrprof.dll,SetSuspendState 0,1,0").ConfigureAwait(false); return Ok("Sleep");
                case "lock": await ExecAsync("rundll32.exe user32.dll,LockWorkStation").ConfigureAwait(false); return Ok("Locked");
                case "clean":
                    var r = await ExecAsync("del /q /f %temp%\\* 2>nul & echo Done").ConfigureAwait(false);
                    return Ok("Cleanup: " + Trim(r, 200));
                case "ram": await ExecAsync("powershell [GC]::Collect()").ConfigureAwait(false); return Ok("RAM trim requested");
                case "open": return Ok(await OpenAppAsync(arg).ConfigureAwait(false));
                case "close": return Ok(await KillProcessAsync(arg).ConfigureAwait(false));
                case "cmd":
                    if (string.IsNullOrWhiteSpace(arg)) return Fail("Empty command");
                    var o = await ExecAsync(arg).ConfigureAwait(false);
                    return new { ok = true, message = "Done", output = Trim(string.IsNullOrWhiteSpace(o) ? "(no output)" : o, 3800) };
                case "ls": return ListDir(arg);
                case "ip":
                    var ext = await new NetworkService().GetPublicIpAsync().ConfigureAwait(false);
                    return Ok($"External: {ext}");
                case "ping": return Ok($"Ping 8.8.8.8: {await new NetworkService().TestLatency().ConfigureAwait(false)} ms");
                case "uptime": return Ok("Uptime: " + SystemUptime.UptimeText);
                case "apps": return Ok(await RunningAppsAsync().ConfigureAwait(false));
                case "screenshot": case "cam": case "stream": case "stop":
                    return Ok("Photo/stream arrives in the bot chat");
                default: return Fail("Unknown action: " + action);
            }
        }
        catch (Exception ex) { return Fail(Trim(ex.Message, 200)); }
    }

    private static object Ok(string message) => new { ok = true, message };
    private static object Fail(string error) => new { ok = false, error };
    private static bool HasDigits(string s) => s.Any(char.IsDigit);
    private static string Trim(string? s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");

    private static async Task<string> ExecAsync(string cmd)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo("cmd.exe", $"/c {cmd}")
                {
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                }
            };
            p.Start();
            var o = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            var e = await p.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await p.WaitForExitAsync().ConfigureAwait(false);
            return string.IsNullOrEmpty(o) ? e : o;
        }
        catch (Exception ex) { return ex.Message; }
    }

    // Поиск приложения: сначала ярлыки меню Пуск (мгновенно), потом Program Files
    // с жёстким лимитом 8 секунд — раньше полный обход вешал ответ на минуту.
    public static async Task<string> OpenAppAsync(string name)
    {
        name = name.Trim().Trim('"');
        if (name.Length == 0) return "Enter app name";
        try
        {
            var n = name.Replace(".exe", "").Trim();
            if (Process.GetProcessesByName(n).Length > 0) return $"{n} is already running";
            // 1) ярлыки меню Пуск
            var startMenu = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs")
            };
            foreach (var sm in startMenu)
            {
                try
                {
                    if (!Directory.Exists(sm)) continue;
                    var lnk = Directory.EnumerateFiles(sm, "*.lnk", SearchOption.AllDirectories)
                        .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f)
                            .Contains(n, StringComparison.OrdinalIgnoreCase));
                    if (lnk != null)
                    {
                        Process.Start(new ProcessStartInfo(lnk) { UseShellExecute = true });
                        return $"Opening {Path.GetFileNameWithoutExtension(lnk)}";
                    }
                }
                catch { }
            }
            // 2) прямой запуск
            try
            {
                Process.Start(new ProcessStartInfo(name) { UseShellExecute = true });
                return $"Opening {name}";
            }
            catch { }
            // 3) Program Files с таймаутом
            var found = await Task.Run(() =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                foreach (var path in new[] { @"C:\Program Files", @"C:\Program Files (x86)" })
                {
                    try
                    {
                        foreach (var f in Directory.EnumerateFiles(path, "*.exe", SearchOption.AllDirectories))
                        {
                            if (cts.IsCancellationRequested) return null;
                            if (Path.GetFileName(f).Equals(n + ".exe", StringComparison.OrdinalIgnoreCase))
                                return f;
                        }
                    }
                    catch { }
                }
                return null;
            }).ConfigureAwait(false);
            if (found != null)
            {
                Process.Start(new ProcessStartInfo(found) { UseShellExecute = true });
                return $"Opened: {Path.GetFileName(found)}";
            }
            return $"Not found: {name}";
        }
        catch (Exception ex) { return Trim(ex.Message, 160); }
    }

    public static async Task<string> KillProcessAsync(string name)
    {
        var n = name.Replace(".exe", "").Trim();
        if (n.Length == 0) return "Enter process name";
        if (SelfProtection.IsProtectedProcess(n)) return $"Protected system process '{n}' — refused";
        return await Task.Run(() =>
        {
            var procs = Process.GetProcessesByName(n);
            if (procs.Length == 0) return $"Not found: {n}";
            int k = 0;
            foreach (var p in procs) { try { p.Kill(); k++; } catch { } p.Dispose(); }
            return $"Terminated {n} ({k})";
        }).ConfigureAwait(false);
    }

    private static object ListDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir))
                dir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            dir = dir.Trim().Trim('"');
            if (!Directory.Exists(dir)) return Fail("Not a folder");
            var items = new List<object>();
            foreach (var e in Directory.GetFileSystemEntries(dir).Take(60))
            {
                try
                {
                    if (Directory.Exists(e))
                        items.Add(new { name = Path.GetFileName(e), type = "dir", size = "" });
                    else
                    {
                        var fi = new FileInfo(e);
                        items.Add(new { name = fi.Name, type = "file", size = $"{fi.Length / 1024} KB" });
                    }
                }
                catch { }
            }
            return new { ok = true, path = dir, items };
        }
        catch (Exception ex) { return Fail(Trim(ex.Message, 160)); }
    }

    private static async Task<string> RunningAppsAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
                var list = Process.GetProcesses()
                    .Where(p => { try { return !string.IsNullOrEmpty(p.MainWindowTitle); } catch { return false; } })
                    .OrderByDescending(p => { try { return p.WorkingSet64; } catch { return 0; } })
                    .Take(15)
                    .Select(p =>
                    {
                        string m;
                        try { m = $"{p.WorkingSet64 / 1048576}MB"; } catch { m = "?"; }
                        var t = p.ProcessName;
                        try { p.Dispose(); } catch { }
                        return $"{t} — {m}";
                    });
                return "Running:\n" + string.Join("\n", list);
            }
            catch (Exception ex) { return ex.Message; }
        }).ConfigureAwait(false);
    }
}
