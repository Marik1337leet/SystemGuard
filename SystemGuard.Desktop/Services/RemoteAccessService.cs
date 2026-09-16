// Хаб стороннего удалённого доступа: детект, установка в один клик,
// запуск, RDP-хост (только Pro+), чтение ID (AnyDesk/RustDesk/TeamViewer).
// Зачем: свой Live-туннель на free-провайдерах периодически мёртв —
// классика (AnyDesk/RustDesk/CRD) даёт независимый канал с телефона.
// winget-поиск на части сетей пуст → качаем с официальных сайтов напрямую.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SystemGuard.Desktop.Services;

public sealed record RemoteToolInfo(
    string Key, string Name, bool Installed, string Detail, string Hint);

public static class RemoteAccessService
{
    // key → (человеческое имя, winget-id [может не резолвиться], прямые URL).
    // URL только официальные: anydesk.com, teamviewer.com, dl.google.com,
    // GitHub-релизы rustdesk/obs-studio (резолвятся через API, без хардкода версий).
    public static readonly IReadOnlyDictionary<string, (string Name, string WingetId, string DirectUrl, string SilentArgs)> Catalog =
        new Dictionary<string, (string, string, string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["anydesk"] = ("AnyDesk", "AnyDeskSoftwareGmbH.AnyDesk",
                "https://download.anydesk.com/AnyDesk.exe", "--install"),
            ["rustdesk"] = ("RustDesk", "RustDesk.RustDesk",
                "github:rustdesk/rustdesk:x86_64.exe", "--install"),
            ["teamviewer"] = ("TeamViewer", "TeamViewer.TeamViewer",
                "https://download.teamviewer.com/download/TeamViewer_Setup.exe", "/S"),
            ["crd"] = ("Chrome Remote Desktop", "Google.ChromeRemoteDesktopHost",
                "https://dl.google.com/edgedl/chrome-remote-desktop/chromeremotesktophost.msi", "/qn"),
            ["obs"] = ("OBS Studio", "OBSProject.OBSStudio",
                "github:obsproject/obs-studio:Windows-Installer.exe", "/S"),
            ["droidcam"] = ("DroidCam", "DEV47APPS.DroidCam",
                "https://www.dev47apps.com/files/600/droidcam_setup.exe", "/SILENT"),
        };

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static string ToolsDir
    {
        get
        {
            var d = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SystemGuard", "tools");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    // ── Детект ─────────────────────────────────────────────────────────
    public static List<RemoteToolInfo> DetectAll()
    {
        var list = new List<RemoteToolInfo>
        {
            DetectAnyDesk(), DetectRustDesk(), DetectTeamViewer(),
            DetectCrd(), DetectObs(), DetectDroidCam(), DetectRdp()
        };
        return list;
    }

    private static RemoteToolInfo DetectAnyDesk()
    {
        var exe = FindExe(@"AnyDesk\AnyDesk.exe",
            @"SOFTWARE\AnyDesk", "InstallPath");
        if (exe == null)
            return new("anydesk", "AnyDesk", false, "not installed",
                "remote_install anydesk — portable EXE с anydesk.com");
        return new("anydesk", "AnyDesk", true,
            $"ID: {GetAnyDeskId(exe) ?? "?"} ({exe})",
            "ID покажите на телефоне в AnyDesk, пароль — в настройках (Unattended)");
    }

    private static RemoteToolInfo DetectRustDesk()
    {
        var exe = FindExe(@"RustDesk\rustdesk.exe", null, null);
        if (exe == null)
            return new("rustdesk", "RustDesk", false, "not installed",
                "remote_install rustdesk — open-source, свой сервер опционально");
        return new("rustdesk", "RustDesk", true,
            $"ID: {GetRustDeskId() ?? "?"} ({exe})",
            "Пароль: RustDesk → Настройки → Безопасность → постоянный пароль");
    }

    private static RemoteToolInfo DetectTeamViewer()
    {
        string? ver = null;
        try
        {
            foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                using var tv = root.OpenSubKey(@"SOFTWARE\TeamViewer");
                if (tv == null) continue;
                foreach (var sn in tv.GetSubKeyNames())
                {
                    using var v = tv.OpenSubKey(sn);
                    var cid = v?.GetValue("ClientID");
                    if (cid != null) { ver = $"ID: {cid}"; break; }
                }
                if (ver != null) break;
            }
        }
        catch { }
        var exe = FindExe(@"TeamViewer\TeamViewer.exe", null, null);
        if (exe == null && ver == null)
            return new("teamviewer", "TeamViewer", false, "not installed",
                "remote_install teamviewer — осторожно с флагом 'коммерческое использование'");
        return new("teamviewer", "TeamViewer", true, ver ?? exe ?? "installed",
            "Привязка к аккаунту — в самом TeamViewer");
    }

    private static RemoteToolInfo DetectCrd()
    {
        bool svc = false;
        try
        {
            svc = System.ServiceProcess.ServiceController.GetServices().Any(s =>
                (s.DisplayName ?? "").Contains("Chrome Remote Desktop", StringComparison.OrdinalIgnoreCase));
        }
        catch { }
        if (!svc)
            return new("crd", "Chrome Remote Desktop", false, "host not installed",
                "remote_install crd — дальше OAuth в браузере Google-аккаунтом");
        return new("crd", "Chrome Remote Desktop", true, "host service present",
            "Доступ: remotedesktop.google.com → Удалённый доступ");
    }

    private static RemoteToolInfo DetectObs()
    {
        var exe = FindExe(@"obs-studio\bin\64bit\obs64.exe", null, null);
        if (exe == null)
            return new("obs", "OBS Studio", false, "not installed",
                "remote_install obs — стрим экрана/камеры на приватный YouTube/RTMP, смотреть с телефона");
        return new("obs", "OBS Studio", true, exe,
            "Захват экрана+камера → стрим (YouTube приватно / свой RTMP)");
    }

    private static RemoteToolInfo DetectDroidCam()
    {
        var exe = FindExe(@"DroidCam\DroidCamApp.exe", null, null);
        if (exe == null)
            return new("droidcam", "DroidCam", false, "not installed",
                "remote_install droidcam — вебка ПК как IP-камера, просмотр с телефона");
        return new("droidcam", "DroidCam", true, exe,
            "DroidCamApp → Start, на телефоне клиент/DroidCamX или браузер");
    }

    private static RemoteToolInfo DetectRdp()
    {
        var edition = GetEditionId();
        var capable = IsRdpCapableEdition(edition);
        int deny = 1;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server");
            deny = Convert.ToInt32(k?.GetValue("fDenyTSConnections", 1) ?? 1);
        }
        catch { }
        if (!capable)
            return new("rdp", "Microsoft RDP", false,
                $"Windows {edition} (Home) — входящий RDP только на Pro+",
                "Вариант: RustDesk/AnyDesk вместо RDP, либо VPN + RDP-клиент наружу");
        return new("rdp", "Microsoft RDP", deny == 0,
            deny == 0 ? "host включён (только LAN/VPN)" : "host выключен",
            deny == 0 ? "rdp_off — выключить" : "rdp_on — включить + правило firewall");
    }

    // ── ID ─────────────────────────────────────────────────────────────
    public static string? GetAnyDeskId(string? exe = null)
    {
        try
        {
            exe ??= FindExe(@"AnyDesk\AnyDesk.exe", @"SOFTWARE\AnyDesk", "InstallPath");
            if (exe == null) return null;
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo(exe, "--get-id")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                }
            };
            p.Start();
            if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return null; }
            var id = (p.StandardOutput.ReadToEnd() ?? "").Trim();
            return ParseAnyDeskIdOutput(id);
        }
        catch { return null; }
    }

    public static string? ParseAnyDeskIdOutput(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var digits = new string(s.Where(char.IsDigit).ToArray());
        return digits.Length >= 6 ? digits : null;
    }

    public static string? GetRustDeskId()
    {
        try
        {
            var cfg = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RustDesk", "config", "RustDesk.toml");
            if (File.Exists(cfg))
            {
                var id = ParseRustDeskIdFromToml(File.ReadAllText(cfg));
                if (id != null) return id;
            }
        }
        catch { }
        return null;
    }

    public static string? ParseRustDeskIdFromToml(string? toml)
    {
        if (string.IsNullOrEmpty(toml)) return null;
        var m = Regex.Match(toml, @"(?m)^\s*id\s*=\s*['""]([^'""]+)['""]");
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    // ── RDP ────────────────────────────────────────────────────────────
    public static string GetEditionId()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return (k?.GetValue("EditionID") as string ?? "?").Trim();
        }
        catch { return "?"; }
    }

    public static bool IsRdpCapableEdition(string? editionId) =>
        (editionId ?? "").Trim().Contains("Professional", StringComparison.OrdinalIgnoreCase)
        || (editionId ?? "").Trim().Contains("Enterprise", StringComparison.OrdinalIgnoreCase)
        || (editionId ?? "").Trim().Contains("Education", StringComparison.OrdinalIgnoreCase);

    public static string RdpEnable()
    {
        if (!IsRdpCapableEdition(GetEditionId()))
            return $"RDP-хост недоступен на Windows {GetEditionId()} (нужен Pro+). Используйте RustDesk/AnyDesk.";
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Terminal Server", writable: true);
            if (k == null) return "RDP: нет доступа к реестру (нужны права админа)";
            k.SetValue("fDenyTSConnections", 0, RegistryValueKind.DWord);
            ExecQuiet("netsh", "advfirewall firewall set rule group=\"Remote Desktop\" new enable=Yes");
            return "RDP-хост включён (только LAN/VPN; наружу — через VPN/туннель, порт 3389 не светите в интернет без NLA/VPN)";
        }
        catch (Exception ex) { return "RDP enable failed: " + Trim(ex.Message, 160); }
    }

    public static string RdpDisable()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Terminal Server", writable: true);
            if (k == null) return "RDP: нет доступа к реестру (нужны права админа)";
            k.SetValue("fDenyTSConnections", 1, RegistryValueKind.DWord);
            return "RDP-хост выключен";
        }
        catch (Exception ex) { return "RDP disable failed: " + Trim(ex.Message, 160); }
    }

    // ── Установка ──────────────────────────────────────────────────────
    public static async Task<string> InstallAsync(string key,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!Catalog.TryGetValue(key.Trim(), out var item))
            return "Unknown tool. Use: " + string.Join(", ", Catalog.Keys);
        try
        {
            var url = await ResolveUrlAsync(item.DirectUrl, ct).ConfigureAwait(false);
            var file = Path.Combine(ToolsDir, item.WingetId.Split('.').Last() +
                (url.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ? ".msi" : ".exe"));
            if (!File.Exists(file) || new FileInfo(file).Length < 1024 * 1024)
            {
                progress?.Report($"Downloading {item.Name}…");
                using var res = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                res.EnsureSuccessStatusCode();
                await using var net = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var fs = File.Open(file, FileMode.Create, FileAccess.Write, FileShare.None);
                await net.CopyToAsync(fs, ct).ConfigureAwait(false);
            }
            progress?.Report($"{item.Name} downloaded, starting installer…");
            var psi = file.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
                ? new ProcessStartInfo("msiexec", $"/i \"{file}\" {item.SilentArgs}")
                : new ProcessStartInfo(file, item.SilentArgs);
            psi.UseShellExecute = true;
            Process.Start(psi);
            var extra = key.Equals("crd", StringComparison.OrdinalIgnoreCase)
                ? " После MSI: remotedesktop.google.com → настройте доступ Google-аккаунтом."
                : key.Equals("anydesk", StringComparison.OrdinalIgnoreCase)
                ? " Portable тоже работает: просто запустите скачанный EXE."
                : "";
            return $"{item.Name}: установщик запущен ({Path.GetFileName(file)}).{extra}";
        }
        catch (Exception ex) { return $"Install {key} failed: " + Trim(ex.Message, 200); }
    }

    public static async Task<string> ResolveUrlAsync(string spec, CancellationToken ct = default)
    {
        // github:owner/repo:подстрока-ассета → свежий URL через GitHub API (без хардкода версий).
        if (spec.StartsWith("github:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = spec["github:".Length..].Split(':', 2);
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/{parts[0]}/releases/latest");
            req.Headers.UserAgent.ParseAdd("SystemGuard/1.0");
            using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                var url = a.GetProperty("browser_download_url").GetString() ?? "";
                if (url.Contains(parts[1], StringComparison.OrdinalIgnoreCase)
                    && (url.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || url.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)))
                    return url;
            }
            throw new InvalidOperationException("asset not found in latest release");
        }
        return spec;
    }

    public static string Launch(string key)
    {
        try
        {
            var exe = key.Trim().ToLowerInvariant() switch
            {
                "anydesk" => FindExe(@"AnyDesk\AnyDesk.exe", @"SOFTWARE\AnyDesk", "InstallPath"),
                "rustdesk" => FindExe(@"RustDesk\rustdesk.exe", null, null),
                "teamviewer" => FindExe(@"TeamViewer\TeamViewer.exe", null, null),
                "obs" => FindExe(@"obs-studio\bin\64bit\obs64.exe", null, null),
                "droidcam" => FindExe(@"DroidCam\DroidCamApp.exe", null, null),
                _ => null
            };
            if (exe == null) return $"{key}: не установлен (remote_install {key})";
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            return $"{key} запущен";
        }
        catch (Exception ex) { return $"Launch failed: " + Trim(ex.Message, 140); }
    }

    public static string StatusText()
    {
        var lines = new List<string>();
        foreach (var t in DetectAll())
            lines.Add($"{(t.Installed ? "[+]" : "[ ]")} {t.Name}: {t.Detail}");
        lines.Add("");
        lines.Add("Команды: remote_install <anydesk|rustdesk|teamviewer|crd|obs|droidcam>, rdp_on/rdp_off (Pro+)");
        return string.Join("\n", lines);
    }

    // ── Утилиты ────────────────────────────────────────────────────────
    private static string? FindExe(string relSuffix, string? regSubkey, string? regValue)
    {
        try
        {
            foreach (var baseDir in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            })
            {
                var p = Path.Combine(baseDir, relSuffix);
                if (File.Exists(p)) return p;
            }
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), relSuffix);
            if (File.Exists(appData)) return appData;
            if (regSubkey != null)
            {
                foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
                {
                    try
                    {
                        using var k = root.OpenSubKey(regSubkey);
                        var v = k?.GetValue(regValue ?? "") as string;
                        if (v != null)
                        {
                            var c = Directory.Exists(v) ? Path.Combine(v, Path.GetFileName(relSuffix)) : v;
                            if (File.Exists(c)) return c;
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }
        return null;
    }

    private static void ExecQuiet(string file, string args)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo(file, args)
                { UseShellExecute = false, CreateNoWindow = true }
            };
            p.Start();
            p.WaitForExit(20000);
        }
        catch { }
    }

    private static string Trim(string? s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");
}
