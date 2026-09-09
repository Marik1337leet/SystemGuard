using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Win32;
using SystemGuard.Core.Interfaces;

namespace SystemGuard.Desktop.Services;

public class TweaksService : ITweaksService
{
    private class TweakHandler
    {
        public required TweakDefinition Definition;
        public required Func<bool> IsApplied;
        public required Func<TweakActionResult> Apply;
        public required Func<TweakActionResult> Revert;
    }

    private readonly Dictionary<string, TweakHandler> _handlers = new();
    private readonly List<TweakDefinition> _backlog = new();
    private readonly Dictionary<string, bool> _sessionState = new();

    public TweaksService()
    {
        RegisterStartup();
        RegisterDevices();
        RegisterServices();
        RegisterVisuals();
        RegisterSystem();
        RegisterNetwork();
        RegisterRegistry();
        RegisterMisc();
        RegisterGamingAndApps();
        RegisterBacklog();
    }

    public IReadOnlyList<TweakDefinition> GetAllTweaks() =>
        _handlers.Values.Select(h => h.Definition)
            .Concat(_backlog)
            .OrderBy(d => d.Category)
            .ThenBy(d => d.Name)
            .ToList();

    public bool IsApplied(string id) => _handlers.TryGetValue(id, out var h) && h.IsApplied();

    public TweakActionResult Apply(string id) =>
        _handlers.TryGetValue(id, out var h) ? SafeRun(h.Apply) : TweakActionResult.Fail("Not implemented yet.");

    public TweakActionResult Revert(string id) =>
        _handlers.TryGetValue(id, out var h) ? SafeRun(h.Revert) : TweakActionResult.Fail("Not implemented yet.");

    private static TweakActionResult SafeRun(Func<TweakActionResult> action)
    {
        try { return action(); }
        catch (Exception ex) { return TweakActionResult.Fail(ex.Message); }
    }

    private void AddRegistry(string id, string name, string desc, TweakCategory cat, bool dangerous, bool restart,
        RegistryHive hive, string subKey, string valueName, object onValue, object offValue, RegistryValueKind kind)
    {
        bool requiresAdmin = hive == RegistryHive.LocalMachine;
        RegistryKey Base() => hive == RegistryHive.LocalMachine ? Registry.LocalMachine : Registry.CurrentUser;

        bool IsApplied()
        {
            try
            {
                using var key = Base().OpenSubKey(subKey);
                var current = key?.GetValue(valueName);
                return current != null && current.Equals(onValue);
            }
            catch { return false; }
        }

        TweakActionResult Write(object value)
        {
            try
            {
                using var key = Base().CreateSubKey(subKey, true);
                key.SetValue(valueName, value, kind);
                return TweakActionResult.Ok($"{name} updated.");
            }
            catch (UnauthorizedAccessException) { return TweakActionResult.Fail("Requires administrator rights. Restart SystemGuard as admin."); }
            catch (Exception ex) { return TweakActionResult.Fail(ex.Message); }
        }

        _handlers[id] = new TweakHandler
        {
            Definition = new TweakDefinition(id, name, desc, cat, dangerous, requiresAdmin, restart, true),
            IsApplied = IsApplied,
            Apply = () => Write(onValue),
            Revert = () => Write(offValue)
        };
    }

    private void AddService(string id, string name, string desc, TweakCategory cat, bool dangerous, bool restart,
        string serviceName, string onStartMode = "disabled", string offStartMode = "demand", bool stopOnApply = true)
    {
        bool IsApplied()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
                var start = key?.GetValue("Start") as int?;
                return start == 4;
            }
            catch { return false; }
        }

        TweakActionResult SetStart(string mode, bool stop)
        {
            try
            {
                if (stop)
                {
                    using var stopP = Process.Start(new ProcessStartInfo("sc", $"stop \"{serviceName}\"")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
                    stopP?.WaitForExit(5000);
                }
                using var p = Process.Start(new ProcessStartInfo("sc", $"config \"{serviceName}\" start= {mode}")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
                p?.WaitForExit(5000);

                if (p != null && p.ExitCode != 0)
                    return TweakActionResult.Fail("Requires administrator rights, or the service isn't present on this system.");
                return TweakActionResult.Ok($"{name} {(mode == "disabled" ? "disabled" : "restored")}.");
            }
            catch (Exception ex) { return TweakActionResult.Fail(ex.Message); }
        }

        _handlers[id] = new TweakHandler
        {
            Definition = new TweakDefinition(id, name, desc, cat, dangerous, true, restart, true),
            IsApplied = IsApplied,
            Apply = () => SetStart(onStartMode, stopOnApply),
            Revert = () => SetStart(offStartMode, false)
        };
    }

    private void AddCommand(string id, string name, string desc, TweakCategory cat, bool dangerous, bool restart, bool requiresAdmin,
        Func<bool>? isApplied, Func<TweakActionResult> apply, Func<TweakActionResult> revert)
    {
        _handlers[id] = new TweakHandler
        {
            Definition = new TweakDefinition(id, name, desc, cat, dangerous, requiresAdmin, restart, true),
            IsApplied = isApplied ?? (() => _sessionState.TryGetValue(id, out var v) && v),
            Apply = () => { var r = SafeRun(apply); if (r.Success) _sessionState[id] = true; return r; },
            Revert = () => { var r = SafeRun(revert); if (r.Success) _sessionState[id] = false; return r; }
        };
    }

    private void AddRunKeyToggle(string id, string name, string desc, TweakCategory cat, string valueName)
    {
        const string runPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        const string backupPath = @"SOFTWARE\SystemGuard\TweakBackups";

        bool IsApplied()
        {
            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(runPath);
                using var backup = Registry.CurrentUser.OpenSubKey(backupPath);
                return run?.GetValue(valueName) == null && backup?.GetValue(valueName) != null;
            }
            catch { return false; }
        }

        TweakActionResult Apply()
        {
            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(runPath, true);
                var current = run?.GetValue(valueName) as string;
                if (string.IsNullOrEmpty(current))
                    return TweakActionResult.Fail($"{name} isn't in startup — nothing to disable.");

                using var backup = Registry.CurrentUser.CreateSubKey(backupPath, true);
                backup.SetValue(valueName, current, RegistryValueKind.String);
                run!.DeleteValue(valueName, false);
                return TweakActionResult.Ok($"{name} removed from startup.");
            }
            catch (Exception ex) { return TweakActionResult.Fail(ex.Message); }
        }

        TweakActionResult Revert()
        {
            try
            {
                using var backup = Registry.CurrentUser.OpenSubKey(backupPath);
                var value = backup?.GetValue(valueName) as string;
                if (string.IsNullOrEmpty(value))
                    return TweakActionResult.Fail("No backup found — nothing to restore.");

                using var run = Registry.CurrentUser.CreateSubKey(runPath, true);
                run.SetValue(valueName, value, RegistryValueKind.String);
                return TweakActionResult.Ok($"{name} restored to startup.");
            }
            catch (Exception ex) { return TweakActionResult.Fail(ex.Message); }
        }

        _handlers[id] = new TweakHandler
        {
            Definition = new TweakDefinition(id, name, desc, cat, false, false, false, true),
            IsApplied = IsApplied,
            Apply = Apply,
            Revert = Revert
        };
    }

    private static TweakActionResult RunProcess(string file, string args, string? successMsg = null)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            p?.WaitForExit(8000);
            if (p != null && p.ExitCode != 0)
                return TweakActionResult.Fail($"Command failed (exit {p.ExitCode}). Restart SystemGuard as administrator.");
            return TweakActionResult.Ok(successMsg ?? "Applied.");
        }
        catch (Exception ex) { return TweakActionResult.Fail(ex.Message); }
    }

    private static TweakActionResult RunPowerShell(string script, string? successMsg = null) =>
        RunProcess("powershell", $"-NoProfile -Command \"{script}\"", successMsg);

    private void RegisterStartup()
    {
        AddRegistry("bg-apps-all", "Background apps (all)", "Stops all UWP/Store apps from running in the background.",
            TweakCategory.Startup, false, false, RegistryHive.CurrentUser,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled",
            1, 0, RegistryValueKind.DWord);

        AddRunKeyToggle("onedrive-autostart", "OneDrive autostart", "Removes OneDrive from startup.", TweakCategory.Startup, "OneDrive");
        AddRunKeyToggle("skype-autostart", "Skype autostart", "Removes classic Skype from startup.", TweakCategory.Startup, "Skype");

        AddRegistry("cortana", "Cortana", "Disables Cortana via Windows Search policy.",
            TweakCategory.Startup, false, false, RegistryHive.LocalMachine,
            @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCortana",
            0, 1, RegistryValueKind.DWord);

        AddRegistry("gamebar", "Xbox Game Bar", "Disables Game Bar overlay capture.",
            TweakCategory.Startup, false, false, RegistryHive.CurrentUser,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled",
            0, 1, RegistryValueKind.DWord);

        AddRegistry("gamedvr", "Xbox Game DVR", "Disables background game recording.",
            TweakCategory.Startup, false, false, RegistryHive.CurrentUser,
            @"System\GameConfigStore", "GameDVR_Enabled",
            0, 1, RegistryValueKind.DWord);

        AddRegistry("telemetry", "Telemetry & diagnostics", "Sets telemetry to the minimum level allowed.",
            TweakCategory.Startup, false, false, RegistryHive.LocalMachine,
            @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry",
            0, 1, RegistryValueKind.DWord);

        AddRegistry("advertising-id", "Advertising ID", "Disables the per-user advertising ID.",
            TweakCategory.Startup, false, false, RegistryHive.CurrentUser,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled",
            0, 1, RegistryValueKind.DWord);

        AddCommand("windows-tips", "Windows tips & suggestions", "Hides tips and suggestions.",
            TweakCategory.Startup, false, false, requiresAdmin: false, null,
            () =>
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", true);
                key.SetValue("SoftLandingEnabled", 0, RegistryValueKind.DWord);
                key.SetValue("SubscribedContent-338389Enabled", 0, RegistryValueKind.DWord);
                return TweakActionResult.Ok("Tips disabled.");
            },
            () =>
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", true);
                key.SetValue("SoftLandingEnabled", 1, RegistryValueKind.DWord);
                key.SetValue("SubscribedContent-338389Enabled", 1, RegistryValueKind.DWord);
                return TweakActionResult.Ok("Tips re-enabled.");
            });

        AddRegistry("app-launch-tracking", "App launch tracking", "Stops app-launch tracking.",
            TweakCategory.Startup, false, false, RegistryHive.CurrentUser,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_TrackProgs",
            0, 1, RegistryValueKind.DWord);
    }

    private void RegisterDevices()
    {
        AddRegistry("webcam-access", "Webcam access", "Blocks system-wide access to the camera.",
            TweakCategory.Devices, false, false, RegistryHive.LocalMachine,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam", "Value",
            "Deny", "Allow", RegistryValueKind.String);

        AddRegistry("microphone-access", "Microphone access", "Blocks system-wide access to the microphone.",
            TweakCategory.Devices, false, false, RegistryHive.LocalMachine,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone", "Value",
            "Deny", "Allow", RegistryValueKind.String);

        AddCommand("bluetooth-radio", "Bluetooth radio", "Disables all Bluetooth adapters.",
            TweakCategory.Devices, false, false, requiresAdmin: true, null,
            () => RunPowerShell("Disable-PnpDevice -Class Bluetooth -Confirm:$false", "Bluetooth disabled."),
            () => RunPowerShell("Enable-PnpDevice -Class Bluetooth -Confirm:$false", "Bluetooth enabled."));

        AddService("cd-dvd-drive", "CD/DVD drive", "Disables the optical drive controller.",
            TweakCategory.Devices, false, true, "cdrom", onStartMode: "disabled", offStartMode: "system", stopOnApply: false);

        AddService("fingerprint-bio", "Fingerprint / Windows Hello biometrics", "Disables the Windows Biometric Service.",
            TweakCategory.Devices, false, false, "WbioSrvc");
    }

    private void RegisterServices()
    {
        AddService("svc-search", "Windows Search", "Disables background file indexing.", TweakCategory.Services, false, false, "WSearch");
        AddService("svc-sysmain", "SysMain (Superfetch)", "Disables app pre-loading into RAM.", TweakCategory.Services, false, false, "SysMain");
        AddService("svc-spooler", "Print Spooler", "Disables printing support.", TweakCategory.Services, false, false, "Spooler");
        AddService("svc-wupdate-manual", "Windows Update (manual only)", "Sets Windows Update to manual.", TweakCategory.Services, false, false, "wuauserv", onStartMode: "demand", offStartMode: "auto", stopOnApply: false);
        AddService("svc-fax", "Fax", "Disables the fax service.", TweakCategory.Services, false, false, "Fax");
        AddService("svc-touch-keyboard", "Touch Keyboard & Tablet PC service", "Disables the touch keyboard/pen service.", TweakCategory.Services, false, false, "TabletInputService");
        AddService("svc-defender", "Windows Defender", "Disables Defender — only if you use a third-party antivirus.", TweakCategory.Services, true, true, "WinDefend");
        AddService("svc-time", "Windows Time", "Disables automatic time sync.", TweakCategory.Services, false, false, "W32Time");
        AddService("svc-geolocation", "Geolocation Service", "Disables location services.", TweakCategory.Services, false, false, "lfsvc");
        AddService("svc-remote-registry", "Remote Registry", "Disables remote registry editing.", TweakCategory.Services, false, false, "RemoteRegistry");
        AddService("svc-rras", "Routing and Remote Access", "Disables the RRAS service.", TweakCategory.Services, false, false, "RemoteAccess");
        AddService("svc-secondary-logon", "Secondary Logon", "Disables running processes as a different user.", TweakCategory.Services, false, false, "seclogon");
        AddService("svc-error-reporting", "Windows Error Reporting", "Stops sending crash reports.", TweakCategory.Services, false, false, "WerSvc");
        AddService("svc-compat-assistant", "Program Compatibility Assistant", "Disables compatibility warnings.", TweakCategory.Services, false, false, "PcaSvc");
        AddService("svc-license-manager", "Client License Service", "Disables Store licensing service.", TweakCategory.Services, false, false, "ClipSVC");
        AddService("svc-font-cache", "Windows Font Cache", "Disables font caching.", TweakCategory.Services, true, true, "FontCache");
        AddService("svc-image-acquisition", "Windows Image Acquisition", "Disables scanner support.", TweakCategory.Services, false, false, "stisvc");
        AddService("svc-bitlocker", "BitLocker Drive Encryption", "Do not disable if a drive is encrypted.", TweakCategory.Services, true, true, "BDESVC");
        AddService("svc-smart-card", "Smart Card", "Disables smart card support.", TweakCategory.Services, false, false, "SCardSvr");
    }

    private void RegisterVisuals()
    {
        AddRegistry("vis-minimize-animation", "Minimize/restore animation", "Disables minimize/restore animation.",
            TweakCategory.Visuals, false, false, RegistryHive.CurrentUser, @"Control Panel\Desktop\WindowMetrics", "MinAnimate", "0", "1", RegistryValueKind.String);

        AddRegistry("vis-transparency", "Transparency effects", "Disables translucent Start/taskbar.",
            TweakCategory.Visuals, false, false, RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0, 1, RegistryValueKind.DWord);

        AddRegistry("vis-font-smoothing", "Font smoothing (ClearType)", "Disables font anti-aliasing.",
            TweakCategory.Visuals, false, false, RegistryHive.CurrentUser, @"Control Panel\Desktop", "FontSmoothing", "0", "2", RegistryValueKind.String);

        AddRegistry("vis-thumbnails", "Thumbnails vs icons", "Shows icons instead of thumbnails.",
            TweakCategory.Visuals, false, false, RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "IconsOnly", 1, 0, RegistryValueKind.DWord);

        AddRegistry("vis-taskbar-animations", "Taskbar animations", "Disables taskbar animations.",
            TweakCategory.Visuals, false, false, RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAnimations", 0, 1, RegistryValueKind.DWord);

        AddRegistry("vis-peek", "Desktop peek", "Disables the desktop preview button.",
            TweakCategory.Visuals, false, false, RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "EnableAeroPeek", 0, 1, RegistryValueKind.DWord);

        AddRegistry("vis-aero-shake", "Aero Shake", "Disables minimizing via window shake.",
            TweakCategory.Visuals, false, false, RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "DisallowShaking", 1, 0, RegistryValueKind.DWord);
    }

    private void RegisterSystem()
    {
        AddCommand("sys-hibernate", "Hibernation", "Disables hibernate and removes hiberfil.sys.",
            TweakCategory.System, false, false, requiresAdmin: true, null,
            () => RunProcess("powercfg", "/hibernate off", "Hibernation disabled."),
            () => RunProcess("powercfg", "/hibernate on", "Hibernation enabled."));

        AddCommand("sys-restore-points", "System Restore", "Disables automatic restore points on C:\\.",
            TweakCategory.System, false, false, requiresAdmin: true, null,
            () => RunPowerShell("Disable-ComputerRestore -Drive 'C:\\'", "System Restore disabled."),
            () => RunPowerShell("Enable-ComputerRestore -Drive 'C:\\'", "System Restore enabled."));

        AddRegistry("sys-uac", "User Account Control (UAC)", "Disables UAC. Reduces security significantly.",
            TweakCategory.System, true, true, RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA", 0, 1, RegistryValueKind.DWord);

        AddCommand("sys-defrag-schedule", "Scheduled optimization", "Disables the weekly optimization task.",
            TweakCategory.System, false, false, requiresAdmin: true, null,
            () => RunProcess("schtasks", "/Change /TN \"Microsoft\\Windows\\Defrag\\ScheduledDefrag\" /Disable", "Scheduled optimization disabled."),
            () => RunProcess("schtasks", "/Change /TN \"Microsoft\\Windows\\Defrag\\ScheduledDefrag\" /Enable", "Scheduled optimization enabled."));

        AddRegistry("sys-fast-startup", "Fast Startup", "Disables Fast Startup (hybrid shutdown).",
            TweakCategory.System, false, true, RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 0, 1, RegistryValueKind.DWord);

        AddRegistry("sys-system-sounds", "System sounds", "Mutes Windows event sounds.",
            TweakCategory.System, false, false, RegistryHive.CurrentUser, @"AppEvents\Schemes", "", ".None", ".Default", RegistryValueKind.String);

        AddRegistry("sys-notifications", "Notifications (all)", "Silences all toast notifications.",
            TweakCategory.System, false, false, RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\PushNotifications", "ToastEnabled", 0, 1, RegistryValueKind.DWord);

        AddRegistry("sys-screensaver", "Screensaver", "Disables the screensaver.",
            TweakCategory.System, false, false, RegistryHive.CurrentUser, @"Control Panel\Desktop", "ScreenSaveActive", "0", "1", RegistryValueKind.String);

        AddRegistry("sys-delivery-optimization", "Delivery Optimization (P2P updates)", "Stops uploading updates to other PCs.",
            TweakCategory.System, false, false, RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0, 1, RegistryValueKind.DWord);

        AddCommand("sys-memory-compression", "Memory compression", "Disables RAM compression.",
            TweakCategory.System, false, true, requiresAdmin: true, null,
            () => RunPowerShell("Disable-MMAgent -MemoryCompression", "Memory compression disabled."),
            () => RunPowerShell("Enable-MMAgent -MemoryCompression", "Memory compression enabled."));

        AddRegistry("sys-fast-shutdown", "Fast service shutdown", "Reduces the service shutdown timeout.",
            TweakCategory.System, false, false, RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control", "WaitToKillServiceTimeout", "2000", "5000", RegistryValueKind.String);

        AddRegistry("sys-crash-dump", "Memory dump on crash", "Disables memory dump on crash.",
            TweakCategory.System, false, true, RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\CrashControl", "CrashDumpEnabled", 0, 3, RegistryValueKind.DWord);
    }

    private void RegisterNetwork()
    {
        AddCommand("net-discovery", "Network Discovery", "Hides this PC from the local network.",
            TweakCategory.Network, false, false, requiresAdmin: true, null,
            () => RunProcess("netsh", "advfirewall firewall set rule group=\"Network Discovery\" new enable=No", "Network Discovery disabled."),
            () => RunProcess("netsh", "advfirewall firewall set rule group=\"Network Discovery\" new enable=Yes", "Network Discovery enabled."));

        AddCommand("net-file-sharing", "File & Printer Sharing", "Disables inbound sharing.",
            TweakCategory.Network, false, false, requiresAdmin: true, null,
            () => RunProcess("netsh", "advfirewall firewall set rule group=\"File and Printer Sharing\" new enable=No", "File sharing disabled."),
            () => RunProcess("netsh", "advfirewall firewall set rule group=\"File and Printer Sharing\" new enable=Yes", "File sharing enabled."));

        AddRegistry("net-ipv6", "IPv6", "Disables IPv6 on all adapters.",
            TweakCategory.Network, false, true, RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters", "DisabledComponents", 0xFF, 0x0, RegistryValueKind.DWord);

        AddRegistry("net-remote-desktop", "Remote Desktop", "Denies incoming Remote Desktop.",
            TweakCategory.Network, false, false, RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections", 1, 0, RegistryValueKind.DWord);

        AddRegistry("net-remote-assistance", "Remote Assistance", "Disables incoming Remote Assistance.",
            TweakCategory.Network, false, false, RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Remote Assistance", "fAllowToGetHelp", 0, 1, RegistryValueKind.DWord);

        AddRegistry("net-qos", "QoS bandwidth reservation", "Removes the QoS bandwidth reservation.",
            TweakCategory.Network, false, false, RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Psched", "NonBestEffortLimit", 0, 20, RegistryValueKind.DWord);

        AddCommand("net-tcp-autotuning", "TCP auto-tuning", "Disables TCP receive window auto-tuning.",
            TweakCategory.Network, false, false, requiresAdmin: true, null,
            () => RunProcess("netsh", "int tcp set global autotuninglevel=disabled", "Auto-tuning disabled."),
            () => RunProcess("netsh", "int tcp set global autotuninglevel=normal", "Auto-tuning restored."));

        AddCommand("net-wifi-adapter", "Wi-Fi adapter", "Disables the adapter named \"Wi-Fi\". Don't use if connected via Wi-Fi.",
            TweakCategory.Network, true, false, requiresAdmin: true, null,
            () => RunPowerShell("Disable-NetAdapter -Name 'Wi-Fi' -Confirm:$false", "Wi-Fi adapter disabled."),
            () => RunPowerShell("Enable-NetAdapter -Name 'Wi-Fi' -Confirm:$false", "Wi-Fi adapter enabled."));

        AddService("net-timezone-auto", "Automatic time zone", "Disables automatic time zone detection.", TweakCategory.Network, false, false, "tzautoupdate", stopOnApply: false);
    }

    private void RegisterRegistry()
    {
        AddRegistry("reg-menu-delay", "Menu show delay", "Removes the delay before submenus open.",
            TweakCategory.Registry, false, false, RegistryHive.CurrentUser, @"Control Panel\Desktop", "MenuShowDelay", "0", "400", RegistryValueKind.String);

        AddRegistry("reg-low-disk-warning", "Low disk space warnings", "Disables the low disk space warning.",
            TweakCategory.Registry, false, false, RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoLowDiskSpaceChecks", 1, 0, RegistryValueKind.DWord);

        AddCommand("reg-ntfs-last-access", "NTFS last-access timestamp", "Stops NTFS updating last-access time.",
            TweakCategory.Registry, false, true, requiresAdmin: true, null,
            () => RunProcess("fsutil", "behavior set disablelastaccess 1", "Last-access timestamps disabled."),
            () => RunProcess("fsutil", "behavior set disablelastaccess 0", "Last-access timestamps enabled."));

        AddRegistry("reg-quick-access-recent", "Quick Access: recent files", "Hides recent files in Quick Access.",
            TweakCategory.Registry, false, false, RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowRecent", 0, 1, RegistryValueKind.DWord);

        AddRegistry("reg-quick-access-frequent", "Quick Access: frequent folders", "Hides frequent folders.",
            TweakCategory.Registry, false, false, RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowFrequent", 0, 1, RegistryValueKind.DWord);

        AddRegistry("reg-jump-lists", "Jump Lists", "Stops remembering recently opened documents.",
            TweakCategory.Registry, false, false, RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_TrackDocs", 0, 1, RegistryValueKind.DWord);
    }

    private void RegisterMisc()
    {
        AddRegistry("misc-explorer-compact", "Explorer compact mode", "Reduces spacing in Explorer.",
            TweakCategory.Misc, false, false, RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "UseCompactMode", 1, 0, RegistryValueKind.DWord);

        AddRegistry("misc-lock-screen-spotlight", "Lock screen Spotlight", "Disables rotating lock screen images.",
            TweakCategory.Misc, false, false, RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "RotatingLockScreenEnabled", 0, 1, RegistryValueKind.DWord);
    }

    private void RegisterGamingAndApps()
    {
        // Службы Xbox (по одной — можно отключать точечно, не всё разом)
        AddService("svc-xbox-auth", "Xbox Auth Manager", "Disables Xbox Live authentication.", TweakCategory.Services, false, false, "XblAuthManager", stopOnApply: false);
        AddService("svc-xbox-save", "Xbox Game Save", "Disables Xbox cloud saves.", TweakCategory.Services, false, false, "XblGameSave", stopOnApply: false);
        AddService("svc-xbox-accessory", "Xbox Accessory Management", "Disables Xbox controller accessory service.", TweakCategory.Services, false, false, "XboxGipSvc", stopOnApply: false);
        AddService("svc-xbox-net", "Xbox Live Networking", "Disables Xbox multiplayer networking.", TweakCategory.Services, false, false, "XboxNetApiSvc", stopOnApply: false);

        // Полное удаление OneDrive (не только из автозапуска)
        AddCommand("app-onedrive-remove", "OneDrive (uninstall)", "Uninstalls OneDrive completely. Reversible via Store installer.",
            TweakCategory.Misc, false, false, requiresAdmin: false, null,
            () => RunProcess("cmd", "/c \"%SystemRoot%\\SysWOW64\\OneDriveSetup.exe\" /uninstall 2>nul & \"%SystemRoot%\\System32\\OneDriveSetup.exe\" /uninstall", "OneDrive uninstall started."),
            () => TweakActionResult.Fail("Reinstall OneDrive from microsoft.com/onedrive."));

        // Удаление предустановленных Xbox-приложений (игнорирует отсутствующие)
        AddCommand("app-xbox-appx", "Xbox preinstalled apps", "Removes Xbox/XboxGamingOverlay AppX for current user.",
            TweakCategory.Misc, false, false, requiresAdmin: false, null,
            () => RunPowerShell("Get-AppxPackage *Xbox* | Remove-AppxPackage -ErrorAction SilentlyContinue", "Xbox apps removed for current user."),
            () => TweakActionResult.Fail("Reinstall from Microsoft Store."));

        // Игровые оверлеи: полное отключение Game Bar политикой (сильнее, чем пер-пользователь)
        AddRegistry("gamebar-policy", "Game Bar (machine policy)", "Disables Game Bar for all users via policy.",
            TweakCategory.Registry, false, false, RegistryHive.LocalMachine,
            @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsRunInBackground",
            2, 0, RegistryValueKind.DWord);

        // Мастер-переключатель анимаций (плавность ↔ скорость)
        AddRegistry("vis-animations-master", "All animations (master)", "Visual effects: best performance preset.",
            TweakCategory.Visuals, false, false, RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting", 2, 0, RegistryValueKind.DWord);

        // Скорость анимаций: мгновенные меню (дополняет MenuShowDelay=0 из Registry)
        AddRegistry("reg-animation-speed", "Animation speed", "Minimizes window animation duration.",
            TweakCategory.Registry, false, false, RegistryHive.CurrentUser, @"Control Panel\Desktop\WindowMetrics", "MinAnimate", "0", "1", RegistryValueKind.String);
    }

    private void RegisterBacklog()
    {
        _backlog.AddRange(new[]
        {
            new TweakDefinition("bl-startup-manager", "Individual startup items", "Enable/disable specific startup entries.", TweakCategory.Startup, false, false, false, false),
            new TweakDefinition("bl-printer", "Printer", "Enable/disable individual printers.", TweakCategory.Devices, false, true, false, false),
            new TweakDefinition("bl-touchscreen", "Touch screen", "Enable/disable the touch digitizer.", TweakCategory.Devices, false, true, true, false),
            new TweakDefinition("bl-tv-tuner", "TV tuner", "Enable/disable TV tuner hardware.", TweakCategory.Devices, false, true, true, false),
            new TweakDefinition("bl-hyperv-adapters", "Hyper-V virtual adapters", "Enable/disable Hyper-V adapters.", TweakCategory.Devices, false, true, true, false),
            new TweakDefinition("bl-unused-audio", "Unused audio outputs", "Disable unused audio endpoints.", TweakCategory.Devices, false, true, false, false),
            new TweakDefinition("bl-hidden-devices", "Hidden devices", "Browse and remove hidden devices.", TweakCategory.Devices, true, true, false, false),
            new TweakDefinition("bl-readyboost", "ReadyBoost", "Use a USB drive as extra cache.", TweakCategory.System, false, true, false, false),
            new TweakDefinition("bl-pagefile", "Page file size", "Manually resize the page file.", TweakCategory.System, true, true, true, false),
            new TweakDefinition("bl-disk-compression", "NTFS drive compression", "Compress drive contents.", TweakCategory.System, false, true, false, false),
            new TweakDefinition("bl-wallpaper-slideshow", "Wallpaper slideshow", "Disable wallpaper rotation.", TweakCategory.Misc, false, false, false, false),
            new TweakDefinition("bl-onscreen-input", "On-screen keyboard & voice typing", "Disable on-screen keyboard/dictation.", TweakCategory.Misc, false, false, false, false),
            new TweakDefinition("bl-accessibility", "Accessibility shortcuts", "Disable Narrator shortcuts.", TweakCategory.Misc, false, false, false, false),
            new TweakDefinition("bl-taskbar-thumbnails", "Taskbar hover thumbnails", "Disable live window previews.", TweakCategory.Visuals, false, false, false, false),
            new TweakDefinition("bl-cloud-recovery", "Cloud recovery", "Manage cloud reset options.", TweakCategory.System, false, false, false, false),
        });
    }
}