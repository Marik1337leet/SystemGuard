using DynamicData;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SystemGuard.Core.Interfaces;

namespace SystemGuard.Desktop.Services;

public class GameModeService : IGameModeService, IDisposable
{
    private readonly List<string> _stoppedServices = new();
    private readonly List<string> _closedBackgroundApps = new();
    private readonly List<int> _loweredPids = new();
    private readonly KeyInputBlockerService _keyBlocker = new();

    private string? _previousPowerPlan;
    private bool _isGameModeActive;
    private GameProfileSnapshot? _activeProfile;
    private Process? _gameProcess;
    private string? _boostedExeName;

    private CancellationTokenSource? _boostCts;
    private Task? _boostTask;
    private bool _boostPriority;

    private int? _prevToastEnabled;
    private int? _prevAppCapture;
    private int? _prevGameDvrEnabled;

    // Бэкап реестра для foreground/GPU/visual твиков (полный откат в DisableAsync)
    private readonly List<RegBackup> _regBackups = new();
    private sealed record RegBackup(bool IsLm, string SubKey, string ValueName, object? OldValue, RegistryValueKind Kind, bool Existed);

    private static readonly int _currentProcessId = Environment.ProcessId;

    private const string HighPerfGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string UltimateGuid = "e9a42b02-d5df-448b-aa00-03f14749eb61";

    // Реальные жруны CPU/RAM перед матчем. Discord/Spotify специально НЕ убиваем —
    // их юзают во время игры (войс/музыка), только понижаем приоритет (см. LowerBackgroundLoad).
    private static readonly string[] _browserNames =
        { "chrome", "msedge", "firefox", "opera", "brave", "vivaldi", "yandex" };

    private static readonly string[] _bloatNames =
    {
        "OneDrive", "Teams", "Skype", "Cortana", "Widgets", "WidgetService",
        "GameBarFTServer", "XboxGameBar", "OfficeClickToRun", "AdobeARM",
        "YourPhone", "PhoneExperienceHost", "AdobeUpdateService", "WpsUpdate"
    };

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
        _loweredPids.Clear();
        _regBackups.Clear();
        _boostedExeName = SafeExeName(profile.GameExecutablePath);

        await Task.Run(() =>
        {
            _previousPowerPlan = GetCurrentPowerPlan();

            string powerNote = "";
            if (profile.EnableHighPerformancePowerPlan)
                powerNote = EnableUltimatePerformance();

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

            _lastKilledCount = KillListedProcesses(profile.ProcessesToKill, profile.ProcessesToKeep);
            int lowered = LowerBackgroundLoad(_boostedExeName, profile.ProcessesToKeep);

            if (profile.DisableBackgroundApps)
                CloseBackgroundApps(profile.ProcessesToKeep);

            // Новые свитчи: закрытие браузеров, Xbox-службы, прозрачность.
            string browsersNote = "";
            if (profile.CloseBrowsersOnStart)
            {
                int closed = KillListedProcesses(_browserNames, profile.ProcessesToKeep);
                browsersNote = closed > 0 ? $"browsers closed {closed}" : "no browsers running";
            }

            string xboxNote = "";
            if (profile.StopXboxServices)
                xboxNote = StopXboxServices();

            string transparencyNote = "";
            if (profile.DisableTransparency)
            {
                try
                {
                    SaveCu(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                        "EnableTransparency", 0, RegistryValueKind.DWord);
                    transparencyNote = "transparency off";
                }
                catch { }
            }

            if (profile.DisableNotifications)
                DisableToastNotifications();

            if (profile.DisableGameBar || profile.DisableGameDvr)
                DisableGameBarAndDvr(profile.DisableGameBar, profile.DisableGameDvr);

            // Раньше флаг DisableAnimations вообще ничего не делал (в EnableAsync
            // не было ни одной строчки под него). Теперь реально режет визуал.
            string visualNote = "";
            if (profile.DisableAnimations)
                visualNote = ApplyVisualBestPerformance();

            // Приоритет foreground-игры у планировщика + GPU-приоритет.
            // Дёшево, обратимо, реально выравнивает 1% low FPS.
            string fgNote = ApplyForegroundGamingTweaks();

            long freedMb = 0;
            string ramNote = "";
            if (profile.ClearRamBeforeLaunch)
            {
                freedMb = ClearRamAggressive(profile.ProcessesToKeep, out bool standby);
                ramNote = $"RAM +{freedMb} MB free" + (standby ? " (standby purged)" : " (standby: need admin)");
            }

            // Таймер 0.5ms держим всю сессию — меньше микрофризов и инпут-лага.
            SetTimerResolutionOnce();
            _boostPriority = profile.SetHighPriority;
            StartBoostLoop();

            // Запуск игры из профиля (как раньше) + буст уже запущенной игры.
            // Раньше приоритет ставился ТОЛЬКО самостоятельно запущенному процессу:
            // игра через Steam/лаунчер вообще не бустилась. Теперь ищем по exe.
            string gameNote;
            if (!string.IsNullOrEmpty(profile.GameExecutablePath))
            {
                try
                {
                    // Если такой процесс уже висит (запущен вручную) — не плодим второй,
                    // а цепляемся к существующему и бустим его.
                    var existing = FindGamePids(_boostedExeName, _gameProcess?.Id);
                    if (existing.Count > 0)
                    {
                        int boosted = profile.SetHighPriority ? BoostGamePids(existing) : 0;
                        gameNote = boosted > 0
                            ? $"game already running — boosted {boosted}"
                            : "game already running";
                    }
                    else
                    {
                        _gameProcess = Process.Start(profile.GameExecutablePath);
                        if (profile.SetHighPriority && _gameProcess != null)
                            BoostOneGameProcess(_gameProcess.Id);
                        gameNote = _gameProcess != null ? "game launched + boosted" : "game launch failed";

                        if (profile.AutoRestoreOnGameExit && _gameProcess != null)
                        {
                            try
                            {
                                _gameProcess.EnableRaisingEvents = true;
                                _gameProcess.Exited += OnGameProcessExited;
                            }
                            catch { }
                        }
                    }
                }
                catch { gameNote = "game launch failed"; }
            }
            else
            {
                // exe не привязан: бустим по имени только если имя известно,
                // иначе делаем системный буст без per-process.
                if (!string.IsNullOrEmpty(_boostedExeName) && profile.SetHighPriority)
                {
                    int boosted = BoostGamePids(FindGamePids(_boostedExeName, null));
                    gameNote = boosted > 0 ? $"boosted running {_boostedExeName} x{boosted}" : "no game exe linked";
                }
                else
                {
                    gameNote = "system boost (link exe for per-process)";
                }
            }

            // Даже без запущенной игры держим High-приоритет будущему процессу:
            // фоновый цикл подхватит игру в течение ~5 сек после старта.
            if (profile.SetHighPriority && !string.IsNullOrEmpty(_boostedExeName))
                BoostGamePids(FindGamePids(_boostedExeName, _gameProcess?.Id));

            // Хук клавиатуры ставим НЕ здесь: WH_KEYBOARD_LL требует поток
            // с message loop (UI-поток). На пул-потоке Task.Run хук молча
            // не срабатывал — поэтому Block Win/Alt+Tab/Alt+F4 «не работали».
            // Старт хука — после Task.Run, в EnableAsync на UI-контексте.

            LastReport = $"Power: {(powerNote == "" ? "kept" : powerNote)} • " +
                         $"stopped {_stoppedServices.Count} svc • " +
                         $"killed {_lastKilledCount} • lowered {lowered} • closed {_closedBackgroundApps.Count} apps" +
                         (ramNote == "" ? "" : " • " + ramNote) +
                         " • timer 0.5ms" +
                         (fgNote == "" ? "" : " • " + fgNote) +
                         (visualNote == "" ? "" : " • " + visualNote) +
                         (browsersNote == "" ? "" : " • " + browsersNote) +
                         (xboxNote == "" ? "" : " • " + xboxNote) +
                         (transparencyNote == "" ? "" : " • " + transparencyNote) +
                         " • " + gameNote;
        });

        // Ставим после Task.Run — на вызвавшем (UI) потоке с message loop.
        try { _keyBlocker.Start(profile.BlockWinKeys, profile.BlockAltTab, profile.BlockAltF4); } catch { }
        if (profile.BlockWinKeys || profile.BlockAltTab || profile.BlockAltF4)
            LastReport += " • keys locked";
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

        // Хук снимаем сразу на UI-потоке (ставился там же).
        try { _keyBlocker.Stop(); } catch { }

        await Task.Run(() =>
        {
            try { _boostCts?.Cancel(); } catch { }
            try { _boostTask?.Wait(1500); } catch { }
            _boostCts?.Dispose();
            _boostCts = null;
            _boostTask = null;
            _boostPriority = false;

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
            RestoreRegBackups();
            RestoreLoweredPriorities();

            LastReport = "System restored — power plan, services, visuals and priorities back to normal";

            _stoppedServices.Clear();
            _closedBackgroundApps.Clear();
            _loweredPids.Clear();
            _isGameModeActive = false;
            _activeProfile = null;
            _boostedExeName = null;

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
                true, true, true,
                new List<string>(),
                new List<string> { "chrome", "msedge", "firefox", "opera", "brave", "vivaldi", "yandex",
                    "OneDrive", "Teams", "Skype", "Cortana", "Widgets", "WidgetService",
                    "GameBarFTServer", "OfficeClickToRun", "AdobeARM", "YourPhone" }),
            new("Balanced Gaming", "", true, false, true, false, false, false, false, false,
                false, false, false, false, false, false, true, false, false,
                false, true, false,
                new List<string>(),
                new List<string> { "chrome", "msedge", "firefox", "OneDrive", "Widgets", "Cortana" }),
            new("Minimal Impact", "", false, false, false, false, false, false, false, false,
                false, false, false, false, false, false, false, false, false,
                false, false, false,
                new List<string>(), new List<string>())
        };
    }

    public GameProfileSnapshot CreateCustomProfile(string name, string executablePath)
    {
        return new GameProfileSnapshot(name, executablePath,
            true, true, true, true, true, true, true, true,
            false, false, false, false, false, false, true, false, false,
            false, true, false,
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

    public async Task<string> BoostNowAsync()
    {
        long freed = await Task.Run(() => ClearRamAggressive(Array.Empty<string>(), out _));
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
        int lowered = await Task.Run(() => LowerBackgroundLoad(null));
        LastReport = $"Boost: +{freed} MB free (working set + standby), DNS flushed, {lowered} background lowered. Full timer/priority hold lives in Game Mode.";
        return LastReport;
    }

    // ── Process control ──────────────────────────────────────────────────

    private static readonly string[] _xboxServices =
        { "XblGameSave", "XboxGipSvc", "XboxNetApiSvc", "XblAuthManager" };

    // Остановка Xbox-служб на время игры. Имена складываем в _stoppedServices —
    // общий цикл в DisableAsync их перезапустит (net start).
    private string StopXboxServices()
    {
        int n = 0;
        foreach (var name in _xboxServices)
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
                if (process == null || process.ExitCode == 0)
                {
                    _stoppedServices.Add(name);
                    n++;
                }
            }
            catch { }
        }
        return n > 0 ? $"xbox svc stopped {n}" : "xbox svc already off";
    }

    private static bool IsSelf(Process proc) => proc.Id == _currentProcessId;

    private static string NormExe(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var n = name.Trim();
        if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            n = n[..^4];
        return n.Trim();
    }

    private int KillListedProcesses(IReadOnlyList<string> namesToKill, IReadOnlyList<string>? keep = null)
    {
        if (namesToKill.Count == 0) return 0;
        var set = new HashSet<string>(namesToKill.Select(NormExe).Where(s => s.Length > 0), StringComparer.OrdinalIgnoreCase);
        // Keep-лист важнее kill-листа: что пользователь сохранил — не трогаем.
        var keepSet = new HashSet<string>((keep ?? Array.Empty<string>()).Select(NormExe).Where(s => s.Length > 0), StringComparer.OrdinalIgnoreCase);
        if (set.Count == 0) return 0;
        int killed = 0;

        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (IsSelf(proc)) continue;
                if (keepSet.Contains(proc.ProcessName)) continue;
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

    // Не убиваем, а опускаем фоновый мусор в BelowNormal — CPU отдаётся игре
    // без потери несохранённых вкладок/чатов. Убивать — только по Kill-листу.
    private int LowerBackgroundLoad(string? gameExeName, IReadOnlyList<string>? keep = null)
    {
        var targets = new HashSet<string>(_browserNames, StringComparer.OrdinalIgnoreCase);
        foreach (var b in _bloatNames) targets.Add(b);
        if (!string.IsNullOrEmpty(gameExeName)) targets.Remove(gameExeName);
        // Keep-лист не понижаем тоже (войс/музыка пользователя).
        var keepSet = new HashSet<string>((keep ?? Array.Empty<string>()).Select(NormExe).Where(s => s.Length > 0), StringComparer.OrdinalIgnoreCase);
        int lowered = 0;

        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (IsSelf(proc)) continue;
                if (!targets.Contains(proc.ProcessName)) continue;
                if (keepSet.Contains(proc.ProcessName)) continue;
                if (SelfProtection.IsProtectedProcess(proc.ProcessName)) continue;
                if (!string.IsNullOrEmpty(gameExeName) &&
                    proc.ProcessName.Equals(gameExeName, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (proc.PriorityClass == ProcessPriorityClass.Normal)
                    {
                        proc.PriorityClass = ProcessPriorityClass.BelowNormal;
                        lock (_loweredPids) _loweredPids.Add(proc.Id);
                        lowered++;
                    }
                }
                catch { }
            }
            catch { }
            finally { try { proc.Dispose(); } catch { } }
        }
        return lowered;
    }

    private void RestoreLoweredPriorities()
    {
        List<int> copy;
        lock (_loweredPids) copy = _loweredPids.ToList();
        foreach (var pid in copy)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (SelfProtection.IsProtectedProcess(p.ProcessName)) continue;
                try { if (p.PriorityClass == ProcessPriorityClass.BelowNormal) p.PriorityClass = ProcessPriorityClass.Normal; } catch { }
            }
            catch { }
        }
    }

    // Быстрое закрытие браузеров перед игрой (реальный TOP жрунов RAM)
    public async Task<string> QuickKillBrowsersAsync()
    {
        int n = await Task.Run(() => KillListedProcesses(_browserNames));
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

    // ── Game process boost (High + no power-throttle) ────────────────────

    private static string? SafeExeName(string? exePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(exePath)) return null;
            return Path.GetFileNameWithoutExtension(exePath.Trim());
        }
        catch { return null; }
    }

    private List<int> FindGamePids(string? exeName, int? knownPid)
    {
        var pids = new List<int>();
        if (knownPid.HasValue)
        {
            try
            {
                using var kp = Process.GetProcessById(knownPid.Value);
                pids.Add(knownPid.Value);
            }
            catch { }
        }
        if (string.IsNullOrEmpty(exeName)) return pids;
        try
        {
            foreach (var p in Process.GetProcessesByName(exeName))
            {
                try
                {
                    if (p.Id == _currentProcessId) continue;
                    if (!pids.Contains(p.Id)) pids.Add(p.Id);
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
        }
        catch { }
        return pids;
    }

    private int BoostGamePids(List<int> pids)
    {
        int n = 0;
        foreach (var pid in pids)
            if (BoostOneGameProcess(pid)) n++;
        return n;
    }

    private static bool BoostOneGameProcess(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (pid == _currentProcessId) return false;
            if (SelfProtection.IsProtectedProcess(p.ProcessName)) return false;
            try
            {
                // High даёт игре приоритет планировщика. Realtime НЕ ставим —
                // вешает мышь/звук и роняет 1% low вместо роста FPS.
                if (p.PriorityClass != ProcessPriorityClass.High)
                    p.PriorityClass = ProcessPriorityClass.High;
            }
            catch { return false; }
            try { p.PriorityBoostEnabled = true; } catch { }
            try { DisablePowerThrottling(p.Handle); } catch { }
            return true;
        }
        catch { return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr hProcess, int ProcessInformationClass,
        ref ProcessPowerThrottlingState ProcessInformation, uint ProcessInformationSize);

    private static void DisablePowerThrottling(IntPtr hProcess)
    {
        try
        {
            var s = new ProcessPowerThrottlingState { Version = 1, ControlMask = 1, StateMask = 0 };
            SetProcessInformation(hProcess, 4, ref s, (uint)Marshal.SizeOf<ProcessPowerThrottlingState>());
        }
        catch { }
    }

    private void StartBoostLoop()
    {
        try { _boostCts?.Cancel(); } catch { }
        _boostCts = new CancellationTokenSource();
        var token = _boostCts.Token;
        var exe = _boostedExeName;
        var knownPid = _gameProcess?.Id;
        _boostTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    SetTimerResolutionOnce();
                    if (_boostPriority && !string.IsNullOrEmpty(exe))
                        BoostGamePids(FindGamePids(exe, knownPid));
                }
                catch { }
                try { await Task.Delay(5000, token); }
                catch (TaskCanceledException) { break; }
                catch { break; }
            }
        }, token);
    }

    // ── Timer resolution 0.5ms ───────────────────────────────────────────

    [DllImport("ntdll.dll")]
    private static extern int NtSetTimerResolution(uint DesiredResolution, bool SetResolution, out uint CurrentResolution);

    private void SetTimerResolutionOnce()
    {
        try { NtSetTimerResolution(5000, true, out _); } catch { }
    }

    // ── RAM: working set + standby purge, с замером МБ ───────────────────

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int SystemInformationClass, IntPtr SystemInformation, int SystemInformationLength);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    private static ulong AvailPhysMb()
    {
        try
        {
            var st = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref st)) return st.ullAvailPhys / 1024 / 1024;
        }
        catch { }
        return 0;
    }

    private static bool PurgeStandbyList()
    {
        // SystemMemoryListInformation (80) + MemoryPurgeStandbyList (4).
        // Требует админа; без него возвращаем false и честно пишем в отчёт.
        IntPtr buf = IntPtr.Zero;
        try
        {
            buf = Marshal.AllocHGlobal(4);
            Marshal.WriteInt32(buf, 4);
            return NtSetSystemInformation(80, buf, 4) == 0;
        }
        catch { return false; }
        finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
    }

    private long ClearRamAggressive(IReadOnlyList<string> keepList, out bool standbyPurged)
    {
        var keep = new HashSet<string>(keepList ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        ulong before = AvailPhysMb();
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (IsSelf(proc)) continue;
                    if (keep.Contains(proc.ProcessName)) continue;
                    if (SelfProtection.IsProtectedProcess(proc.ProcessName)) continue;
                    // Игру не тримим — её working set горячий, сброс даст фриз.
                    if (!string.IsNullOrEmpty(_boostedExeName) &&
                        proc.ProcessName.Equals(_boostedExeName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (_gameProcess != null && proc.Id == _gameProcess.Id) continue;
                    EmptyWorkingSet(proc.Handle);
                }
                catch { }
                finally { try { proc.Dispose(); } catch { } }
            }
            GC.Collect();
        }
        catch { }
        standbyPurged = PurgeStandbyList();
        ulong after = AvailPhysMb();
        long freed = (long)after - (long)before;
        return freed > 0 ? freed : 0;
    }

    private void ClearWorkingSet(IReadOnlyList<string> keepList)
    {
        ClearRamAggressive(keepList, out _);
    }

    private static bool SafeHasExited(Process p)
    {
        try { return p.HasExited; } catch { return true; }
    }

    // ── Power plan: Ultimate + CPU 100% + без парковки ───────────────────

    private static void RunQuiet(string file, string args, int timeoutMs = 8000)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            p?.WaitForExit(timeoutMs);
        }
        catch { }
    }

    private static string EnableUltimatePerformance()
    {
        try
        {
            // Создаём Ultimate, если его нет (на десктопах его часто нет из коробки).
            RunQuiet("powercfg", $"-duplicatescheme {UltimateGuid}", 8000);
            using var probe = Process.Start(new ProcessStartInfo("powercfg", $"/setactive {UltimateGuid}")
            { UseShellExecute = false, CreateNoWindow = true });
            probe?.WaitForExit(5000);
            bool ultimateOn = probe == null || probe.ExitCode == 0;
            if (!ultimateOn)
                SetPowerPlan(HighPerfGuid);

            // CPU всегда 100% + без парковки ядер на время игры.
            RunQuiet("powercfg", "/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN 100");
            RunQuiet("powercfg", "/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX 100");
            RunQuiet("powercfg", "/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR CPMINCORES 100");
            RunQuiet("powercfg", "/setactive SCHEME_CURRENT");
            return ultimateOn ? "Ultimate + CPU 100%" : "HighPerf + CPU 100%";
        }
        catch { return ""; }
    }

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
                    CreateNoWindow = true,
                    StandardOutputEncoding = CmdEncoding.Oem
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

    // ── Registry tweaks (foreground/GPU/visuals) с откатом ──────────────

    private void SaveCu(string subKey, string valueName, object newValue, RegistryValueKind kind)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(subKey, true)
                ?? Registry.CurrentUser.CreateSubKey(subKey, true);
            if (key == null) return;
            var old = key.GetValue(valueName);
            _regBackups.Add(new RegBackup(false, subKey, valueName, old, kind, old != null));
            key.SetValue(valueName, newValue, kind);
        }
        catch { }
    }

    private void SaveLm(string subKey, string valueName, object newValue, RegistryValueKind kind)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(subKey, true)
                ?? Registry.LocalMachine.CreateSubKey(subKey, true);
            if (key == null) return;
            var old = key.GetValue(valueName);
            _regBackups.Add(new RegBackup(true, subKey, valueName, old, kind, old != null));
            key.SetValue(valueName, newValue, kind);
        }
        catch { } // без админа HKLM не пишется — молча пропускаем
    }

    private void RestoreRegBackups()
    {
        foreach (var b in _regBackups)
        {
            try
            {
                RegistryKey? root = b.IsLm ? Registry.LocalMachine : Registry.CurrentUser;
                using var key = root.OpenSubKey(b.SubKey, true);
                if (key == null) continue;
                if (!b.Existed)
                {
                    try { key.DeleteValue(b.ValueName, false); } catch { }
                }
                else if (b.OldValue != null)
                {
                    try { key.SetValue(b.ValueName, b.OldValue, b.Kind); } catch { }
                }
            }
            catch { }
        }
        _regBackups.Clear();
    }

    private string ApplyForegroundGamingTweaks()
    {
        try
        {
            // Меньше резерва системы под мультимедиа (было 20) — больше CPU игре.
            SaveLm(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
                "SystemResponsiveness", 10, RegistryValueKind.DWord);
            // GPU-приоритет игровой задачи.
            SaveCu(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games",
                "GPU Priority", 8, RegistryValueKind.DWord);
            SaveCu(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games",
                "Priority", 6, RegistryValueKind.DWord);
            SaveCu(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games",
                "Scheduling Category", "High", RegistryValueKind.String);
            // Foreground-окну — длинный квант (классика 0x26).
            SaveLm(@"SYSTEM\CurrentControlSet\Control\PriorityControl",
                "Win32PrioritySeparation", 38, RegistryValueKind.DWord);
            return "fg+GPU boost";
        }
        catch { return ""; }
    }

    private string ApplyVisualBestPerformance()
    {
        try
        {
            SaveCu(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects",
                "VisualFXSetting", 2, RegistryValueKind.DWord);
            SaveCu(@"Control Panel\Desktop", "MenuShowDelay", "0", RegistryValueKind.String);
            SaveCu(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                "TaskbarAnimations", 0, RegistryValueKind.DWord);
            SaveCu(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                "EnableAeroPeek", 0, RegistryValueKind.DWord);
            SaveCu(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "EnableTransparency", 0, RegistryValueKind.DWord);
            return "visuals off";
        }
        catch { return ""; }
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
        try { _boostCts?.Cancel(); } catch { }
        _keyBlocker.Dispose();
    }
}
