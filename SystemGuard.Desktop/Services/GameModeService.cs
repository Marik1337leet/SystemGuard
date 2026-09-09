using DynamicData;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SystemGuard.Core.Interfaces;

namespace SystemGuard.Desktop.Services;

public class GameModeService : IGameModeService, IDisposable
{
    private readonly List<string> _stoppedServices = new();
    private readonly List<string> _closedBackgroundApps = new();
    private readonly KeyInputBlockerService _keyBlocker = new();

    private string? _previousPowerPlan;
    private bool _isGameModeActive;
    private GameProfileSnapshot? _activeProfile;
    private Process? _gameProcess;

    private int? _prevToastEnabled;
    private int? _prevAppCapture;
    private int? _prevGameDvrEnabled;

    private static readonly int _currentProcessId = Environment.ProcessId;

    private static readonly HashSet<string> _protectedProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SystemGuard", "SystemGuard.Desktop", "explorer", "dwm", "System", "Registry", "csrss", "wininit",
        "winlogon", "services", "lsass", "smss", "ShellExperienceHost", "SearchHost",
        "StartMenuExperienceHost", "TextInputHost", "RuntimeBroker"
    };

    public bool IsActive => _isGameModeActive;
    public GameProfileSnapshot? ActiveProfile => _activeProfile;
    public bool IsGameProcessRunning => _gameProcess != null && !SafeHasExited(_gameProcess);
    public event Action? OnAutoRestored;

    // Последний отчёт для UI (что реально остановлено/закрыто)
    public string LastReport { get; private set; } = "";
    private int _lastKilledCount;

    public async Task EnableAsync(GameProfileSnapshot profile)
    {
        if (_isGameModeActive) return;

        _activeProfile = profile;
        _isGameModeActive = true;
        _stoppedServices.Clear();
        _closedBackgroundApps.Clear();

        await Task.Run(() =>
        {
            _previousPowerPlan = GetCurrentPowerPlan();

            if (profile.EnableHighPerformancePowerPlan)
                SetPowerPlan("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

            var servicesToStop = new Dictionary<string, bool>
            {
                ["wuauserv"] = profile.DisableWindowsUpdates,
                ["WSearch"] = profile.StopWindowsSearch,
                ["Spooler"] = profile.StopPrintSpooler,
                ["SysMain"] = profile.DisableSysMain,
                ["bthserv"] = profile.StopBluetooth
            };

            foreach (var (name, shouldStop) in servicesToStop.Where(s => s.Value))
            {
                try
                {
                    using var process = Process.Start(new ProcessStartInfo
                    {
                        FileName = "net",
                        Arguments = $"stop {name}",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    process?.WaitForExit(3000);
                    _stoppedServices.Add(name);
                }
                catch { }
            }

            _lastKilledCount = KillListedProcesses(profile.ProcessesToKill);

            if (profile.DisableBackgroundApps)
                CloseBackgroundApps(profile.ProcessesToKeep);

            if (profile.DisableNotifications)
                DisableToastNotifications();

            if (profile.DisableGameBar || profile.DisableGameDvr)
                DisableGameBarAndDvr(profile.DisableGameBar, profile.DisableGameDvr);

            if (profile.ClearRamBeforeLaunch)
                ClearWorkingSet(profile.ProcessesToKeep);

            _keyBlocker.Start(profile.BlockWinKeys, profile.BlockAltTab, profile.BlockAltF4);

            LastReport = $"Stopped {_stoppedServices.Count} services • " +
                         $"closed {_closedBackgroundApps.Count} apps • " +
                         $"killed {_lastKilledCount} processes" +
                         (profile.ClearRamBeforeLaunch ? " • RAM cleared" : "") +
                         (_gameProcess != null ? " • game launched" : "");

            if (!string.IsNullOrEmpty(profile.GameExecutablePath))
            {
                try
                {
                    _gameProcess = Process.Start(profile.GameExecutablePath);
                    if (profile.SetHighPriority && _gameProcess != null)
                    {
                        try { _gameProcess.PriorityClass = ProcessPriorityClass.High; } catch { }
                    }

                    if (profile.AutoRestoreOnGameExit && _gameProcess != null)
                    {
                        _gameProcess.EnableRaisingEvents = true;
                        _gameProcess.Exited += OnGameProcessExited;
                    }
                }
                catch { }
            }
        });
    }

    private async void OnGameProcessExited(object? sender, EventArgs e)
    {
        try
        {
            if (sender is Process p) p.Exited -= OnGameProcessExited;
            await DisableAsync();
            OnAutoRestored?.Invoke();
        }
        catch { }
    }

    public async Task DisableAsync()
    {
        if (!_isGameModeActive) return;

        await Task.Run(() =>
        {
            _keyBlocker.Stop();

            if (!string.IsNullOrEmpty(_previousPowerPlan))
                SetPowerPlan(_previousPowerPlan);

            foreach (var service in _stoppedServices)
            {
                try
                {
                    using var process = Process.Start(new ProcessStartInfo
                    {
                        FileName = "net",
                        Arguments = $"start {service}",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    process?.WaitForExit(3000);
                }
                catch { }
            }

            RestoreToastNotifications();
            RestoreGameBarAndDvr();

            LastReport = "System restored — services and settings back to normal";

            _stoppedServices.Clear();
            _closedBackgroundApps.Clear();
            _isGameModeActive = false;
            _activeProfile = null;

            if (_gameProcess != null)
            {
                try { _gameProcess.Exited -= OnGameProcessExited; } catch { }
                _gameProcess = null;
            }
        });
    }

    public IReadOnlyList<GameProfileSnapshot> GetDefaultProfiles()
    {
        return new List<GameProfileSnapshot>
        {
            new("Maximum Performance", "", true, true, true, true, true, true, true, true,
                false, false, false, false, false, false, true, false, false,
                new List<string>(), new List<string>()),
            new("Balanced Gaming", "", true, false, true, false, false, false, false, false,
                false, false, false, false, false, false, true, false, false,
                new List<string>(), new List<string>()),
            new("Minimal Impact", "", false, false, false, false, false, false, false, false,
                false, false, false, false, false, false, false, false, false,
                new List<string>(), new List<string>())
        };
    }

    public GameProfileSnapshot CreateCustomProfile(string name, string executablePath)
    {
        return new GameProfileSnapshot(name, executablePath,
            true, true, true, true, true, true, true, true,
            false, false, false, false, false, false, true, false, false,
            new List<string>(), new List<string>());
    }

    public void SaveProfile(GameProfileSnapshot profile)
    {
        var storage = new GameProfileStorageService();
        var profiles = storage.Load();
        var idx = profiles.FindIndex(p => p.Name == profile.Name);
        if (idx >= 0) profiles[idx] = profile;
        else profiles.Add(profile);
        storage.Save(profiles);
    }

    public void DeleteProfile(string name)
    {
        var storage = new GameProfileStorageService();
        var profiles = storage.Load();
        profiles.RemoveAll(p => p.Name == name);
        storage.Save(profiles);
    }

    // ── Boost Now: быстрая оптимизация без полного Game Mode ────────────────
    // (EmptyWorkingSet уже объявлен в этом классе — переиспользуем)

    public async Task<string> BoostNowAsync()
    {
        int trimmed = await MemoryTrimmer.TrimAllAsync();
        await Task.Run(() =>
        {
            try
            {
                using var dns = Process.Start(new ProcessStartInfo("ipconfig", "/flushdns")
                { UseShellExecute = false, CreateNoWindow = true });
                dns?.WaitForExit(10000);
            }
            catch { }
        });
        LastReport = $"Boost complete: {trimmed} processes trimmed, DNS flushed";
        return LastReport;
    }

    // ── Process control ──────────────────────────────────────────────────

    private static bool IsSelf(Process proc) => proc.Id == _currentProcessId;

    private int KillListedProcesses(IReadOnlyList<string> namesToKill)
    {
        if (namesToKill.Count == 0) return 0;
        var set = new HashSet<string>(namesToKill, StringComparer.OrdinalIgnoreCase);
        int killed = 0;

        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (IsSelf(proc)) continue;
                if (set.Contains(proc.ProcessName) && !SelfProtection.IsProtectedProcess(proc.ProcessName))
                {
                    proc.Kill();
                    killed++;
                }
            }
            catch { }
            finally { try { proc.Dispose(); } catch { } }
        }
        return killed;
    }

    // Быстрое закрытие браузеров перед игрой (реальный TOP жрунов RAM)
    public async Task<string> QuickKillBrowsersAsync()
    {
        var browsers = new[] { "chrome", "msedge", "firefox", "opera", "brave", "vivaldi", "yandex" };
        int n = await Task.Run(() => KillListedProcesses(browsers));
        LastReport = n > 0 ? $"Closed {n} browser processes" : "No browsers running";
        return LastReport;
    }

    private void CloseBackgroundApps(IReadOnlyList<string> keepList)
    {
        var keep = new HashSet<string>(keepList, StringComparer.OrdinalIgnoreCase);
        var gamePid = _gameProcess?.Id;

        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (IsSelf(proc)) continue;
                if (_protectedProcessNames.Contains(proc.ProcessName)) continue;
                if (keep.Contains(proc.ProcessName)) continue;
                if (gamePid.HasValue && proc.Id == gamePid.Value) continue;

                if (proc.MainWindowHandle == IntPtr.Zero) continue;
                if (string.IsNullOrWhiteSpace(proc.MainWindowTitle)) continue;

                var name = proc.ProcessName;
                proc.CloseMainWindow();
                _closedBackgroundApps.Add(name);
            }
            catch { }
            finally { try { proc.Dispose(); } catch { } }
        }
    }

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    private void ClearWorkingSet(IReadOnlyList<string> keepList)
    {
        var keep = new HashSet<string>(keepList, StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (IsSelf(proc)) continue;
                    if (keep.Contains(proc.ProcessName)) continue;
                    EmptyWorkingSet(proc.Handle);
                }
                catch { }
                finally { try { proc.Dispose(); } catch { } }
            }
            GC.Collect();
        }
        catch { }
    }

    private static bool SafeHasExited(Process p)
    {
        try { return p.HasExited; } catch { return true; }
    }

    // ── Power plan ────────────────────────────────────────────────────────

    private static string GetCurrentPowerPlan()
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powercfg",
                    Arguments = "/getactivescheme",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            p.Start();
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            var start = output.IndexOf(": ") + 2;
            var end = output.IndexOf(" ", start);
            if (start > 1 && end > start)
                return output[start..end].Trim();
        }
        catch { }
        return "381b4222-f694-41f0-9685-ff5bb260df2e";
    }

    private static void SetPowerPlan(string guid)
    {
        try { Process.Start("powercfg", $"/setactive {guid}"); } catch { }
    }

    // ── Notifications (Focus mode) ──────────────────────────────────────

    private void DisableToastNotifications()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\PushNotifications", true)
                ?? Registry.CurrentUser.CreateSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\PushNotifications");

            _prevToastEnabled = key.GetValue("ToastEnabled") as int? ?? 1;
            key.SetValue("ToastEnabled", 0, RegistryValueKind.DWord);
        }
        catch { }
    }

    private void RestoreToastNotifications()
    {
        if (_prevToastEnabled == null) return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\PushNotifications", true);
            key?.SetValue("ToastEnabled", _prevToastEnabled.Value, RegistryValueKind.DWord);
        }
        catch { }
        finally { _prevToastEnabled = null; }
    }

    // ── Game Bar / Game DVR ───────────────────────────────────────────────

    private void DisableGameBarAndDvr(bool disableGameBar, bool disableGameDvr)
    {
        try
        {
            if (disableGameBar)
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR", true)
                    ?? Registry.CurrentUser.CreateSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR");
                _prevAppCapture = key.GetValue("AppCaptureEnabled") as int? ?? 1;
                key.SetValue("AppCaptureEnabled", 0, RegistryValueKind.DWord);
            }

            if (disableGameDvr)
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"System\GameConfigStore", true)
                    ?? Registry.CurrentUser.CreateSubKey(@"System\GameConfigStore");
                _prevGameDvrEnabled = key.GetValue("GameDVR_Enabled") as int? ?? 1;
                key.SetValue("GameDVR_Enabled", 0, RegistryValueKind.DWord);
            }
        }
        catch { }
    }

    private void RestoreGameBarAndDvr()
    {
        try
        {
            if (_prevAppCapture != null)
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR", true);
                key?.SetValue("AppCaptureEnabled", _prevAppCapture.Value, RegistryValueKind.DWord);
                _prevAppCapture = null;
            }

            if (_prevGameDvrEnabled != null)
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore", true);
                key?.SetValue("GameDVR_Enabled", _prevGameDvrEnabled.Value, RegistryValueKind.DWord);
                _prevGameDvrEnabled = null;
            }
        }
        catch { }
    }

    public void Dispose()
    {
        _keyBlocker.Dispose();
    }
}