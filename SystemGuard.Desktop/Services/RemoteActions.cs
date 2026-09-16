using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
            string lang = "en", accent = "#C96C9E";
            bool dark = true;
            try { lang = LocalizationService.Instance.CurrentLanguage ?? "en"; } catch { }
            try { dark = new SettingsService().Load().DarkTheme; } catch { }
            try { accent = new ColorThemeService().Load()?.Accent ?? accent; } catch { }
            if (string.IsNullOrWhiteSpace(accent) || !accent.StartsWith("#")) accent = "#C96C9E";
            return new
            {
                ok = true,
                time = DateTime.Now.ToString("HH:mm:ss"),
                machine = Environment.MachineName,
                os = Environment.OSVersion.VersionString,
                cores = Environment.ProcessorCount,
                lang,
                theme = new { dark, accent },
                ramTotalGb = Math.Round(totalGb, 1),
                ramUsedGb = Math.Round(usedGb, 1),
                ramPct = totalGb > 0 ? Math.Round(usedGb / totalGb * 100, 1) : 0,
                vol = VolumeService.GetPercent(),
                battery = batt,
                uptime = upStr,
                mouseOn = RemoteInputService.MouseEnabled,
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

    // Свежий кадр без кэша — для MJPEG-потока камеры (до 60 FPS в сессии).
    public static async Task<(byte[]? Jpeg, string Error)> GetCamJpegFreshAsync()
    {
        var (jpg, err) = await WebcamService.CaptureJpegAsync(640).ConfigureAwait(false);
        return (jpg, err);
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
                    return new { ok = true, message = "Done", output = CmdEncoding.Clean(o, 6000) };
                case "ls": return ListDir(arg);
                // Один канонический список процессов (богатый: items + текст).
                // "procs" оставлен как alias для совместимости старых клиентов.
                case "processes": case "procs": return ProcList();
                case "ip":
                    var ext = await new NetworkService().GetPublicIpAsync().ConfigureAwait(false);
                    return new { ok = true, message = $"External: {ext}", output = $"External: {ext}" };
                case "ping": return new { ok = true, message = $"Ping 8.8.8.8: {await new NetworkService().TestLatency().ConfigureAwait(false)} ms", output = $"Ping done" };
                case "uptime": return new { ok = true, message = "Uptime: " + SystemUptime.UptimeText, output = SystemUptime.UptimeText };
                case "battery": return new { ok = true, message = GetBatteryLine(), output = GetBatteryLine() };
                case "free": return new { ok = true, message = GetFreeLine(), output = GetFreeLine() };
                case "apps": return new { ok = true, message = await RunningAppsAsync().ConfigureAwait(false), output = await RunningAppsAsync().ConfigureAwait(false) };
                case "perf": return new { ok = true, message = PerfText(), output = PerfText() };
                case "sysinfo": return new { ok = true, message = SysInfoText(), output = SysInfoText() };
                case "startup": return StartupList();
                case "startup_disable": return StartupToggle(arg, false);
                case "startup_enable": return StartupToggle(arg, true);
                case "cleanup_info": return new { ok = true, message = CleanupInfo(), output = CleanupInfo() };
                case "uninstall_list": return await UninstallListAsync().ConfigureAwait(false);
                case "uninstall":
                    if (string.IsNullOrWhiteSpace(arg)) return Fail("Enter program name");
                    return Ok(await UninstallAsync(arg).ConfigureAwait(false));
                case "game_boost": return Ok(await new GameModeService().BoostNowAsync().ConfigureAwait(false));
                case "game_killbrowsers": return Ok(await new GameModeService().QuickKillBrowsersAsync().ConfigureAwait(false));
                case "netstat": return new { ok = true, message = "Done", output = CmdEncoding.Clean(await ExecAsync("netstat -ano | findstr ESTABLISHED").ConfigureAwait(false), 6000) };
                case "connections": return NetConnections();
                case "wifi": return new { ok = true, message = "Done", output = CmdEncoding.Clean(await ExecAsync("netsh wlan show networks mode=bssid").ConfigureAwait(false), 6000) };
                case "dnsflush":
                    try { new NetworkService().FlushDns(); } catch { }
                    return Ok("DNS cache flushed");
                case "ports":
                    if (string.IsNullOrWhiteSpace(arg)) arg = "127.0.0.1:80,443,3389,8899";
                    return new { ok = true, message = "Done", output = CmdEncoding.Clean(await PortScanAsync(arg).ConfigureAwait(false), 4000) };
                case "defender_status": return new { ok = true, message = SecurityService.DefenderStatus(), output = SecurityService.DefenderStatus() };
                case "defender_scan": return new { ok = true, message = SecurityService.DefenderQuickScan(), output = SecurityService.DefenderQuickScan() };
                case "power_plans": return PowerPlans();
                case "power_set":
                    if (string.IsNullOrWhiteSpace(arg)) return Fail("Enter plan GUID");
                    try { new PowerService().SetActivePowerPlan(arg.Trim()); return Ok("Power plan set"); }
                    catch (Exception ex) { return Fail(ex.Message); }
                case "sched_list": return SchedList();
                case "sched_run":
                    if (string.IsNullOrWhiteSpace(arg)) return Fail("Enter task id");
                    try { new SchedulerService().RunNow(arg.Trim()); return Ok("Task started"); }
                    catch (Exception ex) { return Fail(ex.Message); }
                case "eventlog": return new { ok = true, message = "Done", output = CmdEncoding.Clean(await ExecAsync("wevtutil qe System /c:20 /f:text /rd:true").ConfigureAwait(false), 6000) };
                case "services": return new { ok = true, message = "Done", output = CmdEncoding.Clean(await ExecAsync("sc query type= service state= all | findstr SERVICE_NAME").ConfigureAwait(false), 6000) };
                case "license_status":
                {
                    var lic = new LicenseService().CurrentLicense;
                    var s = $"{lic.Tier} (valid: {lic.IsValid}, expires: {lic.ExpiresAt:g})";
                    return new { ok = true, message = s, output = s };
                }
                case "screenshot": case "cam": case "stream": case "stop":
                    return Ok("Photo/stream arrives in the bot chat");
                case "wake":
                    return Ok(RemoteInputService.WakeOnly());
                case "unlock":
                    // Пароль никогда не логируем и не возвращаем
                    if (string.IsNullOrWhiteSpace(arg)) return Fail("Enter Windows password");
                    return Ok(await RemoteInputService.UnlockAsync(arg).ConfigureAwait(false));
                case "mouse_enable":
                {
                    // Свич мыши из WebApp: on | off (1/0, вкл/выкл, true/false).
                    var v = arg.Trim().ToLowerInvariant();
                    bool on = v is "on" or "1" or "true" or "yes" or "вкл" or "да";
                    bool off = v is "off" or "0" or "false" or "no" or "выкл" or "нет";
                    if (!on && !off) return Fail("Use mouse_enable on|off");
                    RemoteInputService.MouseEnabled = on;
                    return Ok(on ? "Mouse control ON" : "Mouse control OFF");
                }
                case "mouse_move":
                {                    // arg: "dx,dy" — относительные пиксели от тачпада WebApp.
                    var parts = arg.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2
                        || !int.TryParse(parts[0].Trim(), out var dx)
                        || !int.TryParse(parts[1].Trim(), out var dy))
                        return Fail("Use dx,dy (e.g. 30,-10)");
                    return Ok(RemoteInputService.MoveRelative(dx, dy));
                }
                case "mouse_move_to":
                {
                    // arg: "fx,fy" — доли экрана 0..1 (тап по скриншоту).
                    var parts = arg.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2
                        || !double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fx)
                        || !double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fy))
                        return Fail("Use fx,fy 0..1 (e.g. 0.5,0.3)");
                    return Ok(RemoteInputService.MoveToFraction(fx, fy));
                }
                case "mouse_click":
                    // arg: left | right | middle | double (пусто = left).
                    return Ok(RemoteInputService.Click(string.IsNullOrWhiteSpace(arg) ? "left" : arg));
                case "mouse_click_at":
                {
                    // arg: "fx,fy[,button]" — навести и кликнуть.
                    var parts = arg.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2
                        || !double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fx)
                        || !double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fy))
                        return Fail("Use fx,fy[,button] (e.g. 0.5,0.3,right)");
                    var btn = parts.Length > 2 ? parts[2].Trim() : "left";
                    var move = RemoteInputService.MoveToFraction(fx, fy);
                    if (move != "OK") return Fail(move);
                    await Task.Delay(80).ConfigureAwait(false);
                    return Ok(RemoteInputService.Click(btn));
                }
                case "scroll":
                {
                    // arg: число кликов колеса (+ вверх, - вниз), по умолчанию 3.
                    if (!int.TryParse(arg.Trim(), out var n)) n = 3;
                    return Ok(RemoteInputService.Scroll(n));
                }
                case "key":
                    // arg: enter, esc, tab, arrows, alt+tab, ctrl+c, win+d, …
                    if (string.IsNullOrWhiteSpace(arg)) return Fail("Enter key name (e.g. enter, alt+tab, ctrl+c)");
                    return Ok(RemoteInputService.PressKey(arg));
                case "type":
                    // arg: текст (кириллица можно, до 500 символов).
                    if (string.IsNullOrWhiteSpace(arg)) return Fail("Enter text to type");
                    return Ok(await RemoteInputService.TypeTextAsync(arg).ConfigureAwait(false));
                case "wol":
                {
                    // arg: "MAC [broadcastIp]" — включает ПК в LAN / через роутер
                    var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0) return Fail("Enter MAC, e.g. AA:BB:CC:DD:EE:FF");
                    var bcast = parts.Length > 1 ? parts[1] : "255.255.255.255";
                    return Ok(WakeOnLanService.Send(parts[0], bcast));
                }
                case "hibernate":
                    await ExecAsync("shutdown /h").ConfigureAwait(false);
                    return Ok("Hibernating");
                // ── Сторонний удалённый доступ (хаб) ──────────────────
                case "remote": return RemoteStatus();
                case "rdp": return Ok(RdpStateLine());
                case "rdp_on": return Ok(RemoteAccessService.RdpEnable());
                case "rdp_off": return Ok(RemoteAccessService.RdpDisable());
                case "remote_launch":
                    if (string.IsNullOrWhiteSpace(arg)) return Fail("Use: remote_launch <anydesk|rustdesk|teamviewer|obs|droidcam>");
                    return Ok(RemoteAccessService.Launch(arg.Split(' ')[0]));
                case "remote_install":
                {
                    // Скачивание 50–150 МБ — не ждём, стартуем в фоне.
                    var tool = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                    if (!RemoteAccessService.Catalog.ContainsKey(tool))
                        return Fail("Use: remote_install <" + string.Join("|", RemoteAccessService.Catalog.Keys) + ">");
                    _ = Task.Run(async () =>
                    {
                        try { await RemoteAccessService.InstallAsync(tool).ConfigureAwait(false); }
                        catch { }
                    });
                    return Ok($"Установка {tool} запущена в фоне, проверьте через remote через минуту");
                }
                case "policy": case "privacy": return new { ok = true, message = PoliciesService.Privacy, output = PoliciesService.Privacy };
                case "terms": return new { ok = true, message = PoliciesService.Terms, output = PoliciesService.Terms };
                case "safety": return new { ok = true, message = PoliciesService.Safety, output = PoliciesService.Safety };
                case "fan": case "fan_status": return new { ok = true, message = FanControlService.StatusText(), output = FanControlService.StatusText() };
                case "fan_set":
                {
                    if (!int.TryParse(new string(arg.Where(c => char.IsDigit(c) || c == '-').ToArray()), out int pct))
                        return Fail("Use fan_set 0..100");
                    return Ok(FanControlService.TrySetPercent(pct));
                }
                case "fan_mode":
                {
                    if (string.IsNullOrWhiteSpace(arg)) return Fail("Use fan_mode Auto|Silent|Balanced|Performance|Manual[:percent]");
                    var parts = arg.Split(':', StringSplitOptions.RemoveEmptyEntries);
                    int pct = 50;
                    if (parts.Length > 1) int.TryParse(new string(parts[1].Where(char.IsDigit).ToArray()), out pct);
                    return Ok(FanControlService.ApplyMode(parts[0].Trim(), pct));
                }
                case "hotkey":
                {
                    if (string.IsNullOrWhiteSpace(arg)) return Fail("Use hotkey <optimize|gameboost|widget|screenshot|mute|lock>");
                    return Ok(await HotkeyActionRunner.RunAsync(arg.Split(' ')[0]).ConfigureAwait(false));
                }
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

    private static Task<string> ExecAsync(string cmd) => CmdEncoding.RunAsync(cmd);

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

    private static string GetBatteryLine()
    {
        try
        {
            var b = new PowerService().GetBatteryInfo();
            return b == null ? "No battery (desktop)" : $"Battery: {b.ChargePercent}% ({b.Status})";
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static string GetFreeLine()
    {
        try
        {
            var lines = new List<string>();
            foreach (var d in DriveInfo.GetDrives())
            {
                if (!d.IsReady || d.DriveType != DriveType.Fixed) continue;
                lines.Add($"{d.Name} {d.AvailableFreeSpace / 1073741824.0:F1} GB free of {d.TotalSize / 1073741824.0:F0} GB");
            }
            return lines.Count > 0 ? string.Join("\n", lines) : "No fixed drives";
        }
        catch (Exception ex) { return ex.Message; }
    }

    // ── Полное зеркало десктопа ──────────────────────────────────────────
    private static object ProcList()
    {
        try
        {
            var items = Process.GetProcesses()
                .Select(p =>
                {
                    string name = p.ProcessName;
                    int pid = 0; long mem = 0;
                    try { pid = p.Id; } catch { }
                    try { mem = p.WorkingSet64 / 1048576; } catch { }
                    try { p.Dispose(); } catch { }
                    return new { name, pid, mem };
                })
                .OrderByDescending(x => x.mem)
                .Take(60)
                .Cast<object>()
                .ToList();
            var txt = string.Join("\n", items.Cast<dynamic>().Take(30).Select(x => $"{x.pid,6}  {x.name,-28} {x.mem,5} MB"));
            return new { ok = true, message = $"Processes: {items.Count} (top by RAM)", output = txt, items };
        }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    private static string PerfText()
    {
        try
        {
            var (totalGb, availGb) = DetailedSystemInfoService.GetPhysicalMemory();
            var used = Math.Max(0, totalGb - availGb);
            return $"CPU: {Environment.ProcessorCount} cores\n" +
                   $"RAM: {used:F1}/{totalGb:F1} GB\n" +
                   $"Uptime: {SystemUptime.UptimeText}\n" +
                   GetFreeLine() + "\n" + GetBatteryLine();
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static string SysInfoText()
    {
        try
        {
            return $"Machine: {Environment.MachineName}\n" +
                   $"OS: {Environment.OSVersion}\n" +
                   $"64-bit: {Environment.Is64BitOperatingSystem}\n" +
                   $"CPU cores: {Environment.ProcessorCount}\n" +
                   $"User: {Environment.UserName}\n" +
                   $"Uptime: {SystemUptime.UptimeText}\n" +
                   DetailedSystemInfoService.FormatSummary();
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static object StartupList()
    {
        try
        {
            var items = new StartupService().GetStartupItems()
                .Select(s => new { name = s.Name, enabled = s.IsEnabled, path = s.Path ?? "" })
                .Cast<object>().ToList();
            var txt = items.Count == 0 ? "Startup list is empty"
                : string.Join("\n", new StartupService().GetStartupItems().Select(s => $"{(s.IsEnabled ? "[ON] " : "[OFF]")} {s.Name}"));
            return new { ok = true, message = $"Startup: {items.Count}", output = txt, items };
        }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    private static object StartupToggle(string name, bool enable)
    {
        try
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return Fail("Enter startup item name");
            var svc = new StartupService();
            var item = svc.GetStartupItems().FirstOrDefault(s => s.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (item == null) return Fail("Not found: " + name);
            if (enable) svc.EnableStartupItem(item); else svc.DisableStartupItem(item);
            return Ok((enable ? "Enabled: " : "Disabled: ") + item.Name);
        }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    private static string CleanupInfo()
    {
        try
        {
            long bytes = 0;
            foreach (var dir in new[] { Path.GetTempPath(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp") })
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
                    {
                        try { bytes += new FileInfo(f).Length; } catch { }
                    }
                }
                catch { }
            }
            return $"Temp garbage: ~{bytes / 1048576} MB\nUse Clean to remove.";
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static async Task<object> UninstallListAsync()
    {
        try
        {
            var list = await new DeepUninstallService().GetInstalledProgramsAsync().ConfigureAwait(false);
            var items = list.OrderBy(p => p.Name).Take(80)
                .Select(p => new { name = p.Name, version = p.Version ?? "", size = p.EstimatedSizeMb > 0 ? p.EstimatedSizeMb + " MB" : "" })
                .Cast<object>().ToList();
            var txt = string.Join("\n", list.OrderBy(p => p.Name).Take(40).Select(p => p.Name));
            return new { ok = true, message = $"Programs: {list.Count}", output = txt, items };
        }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    private static async Task<string> UninstallAsync(string name)
    {
        try
        {
            var svc = new DeepUninstallService();
            var list = await svc.GetInstalledProgramsAsync().ConfigureAwait(false);
            var prog = list.FirstOrDefault(p => p.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (prog == null) return "Not found: " + name;
            var ok = await svc.RunStandardUninstallerAsync(prog).ConfigureAwait(false);
            return ok ? "Uninstaller started: " + prog.Name : "Uninstaller failed: " + prog.Name;
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static object NetConnections()
    {
        try
        {
            var conns = TcpConnectionsService.GetConnections().Take(40)
                .Select(c => new { proto = c.Proto ?? "TCP", local = c.Local ?? "", remote = c.Remote ?? "", state = c.State ?? "", pid = c.Pid })
                .Cast<object>().ToList();
            var txt = conns.Count == 0 ? "No connections"
                : string.Join("\n", TcpConnectionsService.GetConnections().Take(25).Select(c => $"{c.Remote} ({c.State})"));
            return new { ok = true, message = $"Connections: {conns.Count}", output = txt, items = conns };
        }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    private static object RemoteStatus()
    {
        try
        {
            var tools = RemoteAccessService.DetectAll();
            var items = tools.Select(t => new { name = t.Name, installed = t.Installed, detail = t.Detail })
                .Cast<object>().ToList();
            var txt = RemoteAccessService.StatusText();
            return new { ok = true, message = $"Remote tools: {tools.Count(t => t.Installed)}/{tools.Count} installed", output = txt, items };
        }
        catch (Exception ex) { return Fail(Trim(ex.Message, 160)); }
    }

    private static string RdpStateLine()
    {
        try
        {
            var t = RemoteAccessService.DetectAll().FirstOrDefault(x => x.Key == "rdp");
            return t == null ? "RDP: unknown" : $"RDP: {t.Detail}";
        }
        catch (Exception ex) { return ex.Message; }
    }
    private static object PowerPlans()
    {
        try
        {
            var plans = new PowerService().GetPowerPlans()
                .Select(p => new { name = p.Name, guid = p.Guid, active = p.IsActive })
                .Cast<object>().ToList();
            var txt = string.Join("\n", new PowerService().GetPowerPlans().Select(p => $"{(p.IsActive ? "* " : "  ")}{p.Name}"));
            return new { ok = true, message = "Power plans", output = txt, items = plans };
        }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    private static object SchedList()
    {
        try
        {
            var tasks = new SchedulerService().GetTasks()
                .Select(t => new { id = t.Id, name = t.Name ?? t.Id, enabled = t.IsEnabled, at = t.ExecuteAt.ToString("g") })
                .Cast<object>().ToList();
            var txt = tasks.Count == 0 ? "No scheduled tasks"
                : string.Join("\n", new SchedulerService().GetTasks().Select(t => $"{t.Id}  {t.Name}  {(t.IsEnabled ? "ON" : "OFF")}  {t.ExecuteAt:g}"));
            return new { ok = true, message = $"Tasks: {tasks.Count}", output = txt, items = tasks };
        }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    private static async Task<string> PortScanAsync(string arg)
    {
        // arg: "host:port1,port2" или "host"
        try
        {
            var host = "127.0.0.1";
            var ports = new[] { 80, 443, 3389, 8899 };
            var parts = arg.Split(':');
            if (parts.Length >= 1 && !string.IsNullOrWhiteSpace(parts[0])) host = parts[0].Trim();
            if (parts.Length >= 2)
                ports = parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => int.TryParse(s.Trim(), out var p) ? p : -1)
                    .Where(p => p > 0 && p < 65536).Take(20).ToArray();
            var sb = new System.Text.StringBuilder();
            foreach (var port in ports)
            {
                bool open = false;
                try
                {
                    using var c = new System.Net.Sockets.TcpClient();
                    var t = c.ConnectAsync(host, port);
                    open = await Task.WhenAny(t, Task.Delay(1200)).ConfigureAwait(false) == t && c.Connected;
                }
                catch { }
                sb.AppendLine($"{host}:{port} — {(open ? "OPEN" : "closed")}");
            }
            return sb.ToString();
        }
        catch (Exception ex) { return ex.Message; }
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
