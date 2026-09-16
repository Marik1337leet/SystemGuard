using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace SystemGuard.Desktop.Services;

public class SystemAlert
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string Kind { get; set; } = ""; // Temp | Disk | Network
    public string Message { get; set; } = "";
}

public class AlertCheckResult
{
    public List<SystemAlert> NewAlerts { get; set; } = new();
}

// Реальные проверки без ложных срабатываний:
// - температура: только правдоподобные значения 0..120, порог 85°
// - диск: только Fixed-диски от 8 ГБ (recovery-разделы игнорируем), порог 1 ГБ
// - сеть: алерт только если нет поднятого NIC; ICMP-блокировки провайдера/VPN
//   больше не дают ложный «Network is down» (fail-open).
public class AlertService
{
    private readonly string _path;
    private readonly List<SystemAlert> _history = new();
    private DateTime _lastTempAlert = DateTime.MinValue;
    private DateTime _lastDiskAlert = DateTime.MinValue;
    private DateTime _lastNetAlert = DateTime.MinValue;
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(15);

    public IReadOnlyList<SystemAlert> History
    {
        get { lock (_history) return _history.ToList(); }
    }

    public event Action<SystemAlert>? OnAlert;

    public AlertService()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SystemGuard");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "alerts.json");
        Load();
    }

    public AlertCheckResult CheckNow(double cpuTemp, double minDiskFreeGb, bool networkUp)
    {
        var res = new AlertCheckResult();
        var now = DateTime.Now;

        bool tempValid = cpuTemp > 0 && cpuTemp < 120;
        if (tempValid && cpuTemp > 85 && now - _lastTempAlert > Cooldown)
        {
            _lastTempAlert = now;
            res.NewAlerts.Add(Raise("Temp", $"CPU temperature {cpuTemp:F0}°C is above 85°C"));
        }

        if (minDiskFreeGb >= 0 && minDiskFreeGb < 1 && now - _lastDiskAlert > Cooldown)
        {
            _lastDiskAlert = now;
            res.NewAlerts.Add(Raise("Disk", $"Low disk space: {minDiskFreeGb:F1} GB free (less than 1 GB)"));
        }

        if (!networkUp && now - _lastNetAlert > Cooldown)
        {
            _lastNetAlert = now;
            res.NewAlerts.Add(Raise("Network", "Network is down (no active network adapter)"));
        }

        if (res.NewAlerts.Count > 0) Save();
        return res;
    }

    /// <summary>
    /// Проверка сети без ложных срабатываний.
    /// Раньше был один пинг 8.8.8.8 — его режут провайдеры/VPN/файрволы,
    /// и приложение висело с ложным «Network is down».
    /// Теперь: алерт только если нет НИ ОДНОГО поднятого физического NIC.
    /// ICMP/TCP-пробы — best-effort для информации, но при живом NIC
    /// считаем сеть UP (fail-open): лучше пропустить реальное падение,
    /// чем спамить ложными ошибками.
    /// </summary>
    public static bool IsNetworkUp()
    {
        try
        {
            bool nicUp = NetworkInterface.GetAllNetworkInterfaces().Any(n =>
                n.OperationalStatus == OperationalStatus.Up &&
                n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                n.NetworkInterfaceType != NetworkInterfaceType.Tunnel);
            if (!nicUp) return false;

            // Best-effort: хоть один ответ — точно UP.
            string[] targets = { "8.8.8.8", "1.1.1.1", "77.88.8.8" };
            using var ping = new Ping();
            foreach (var t in targets)
            {
                try
                {
                    var reply = ping.Send(t, 800);
                    if (reply?.Status == IPStatus.Success) return true;
                }
                catch { }
            }
            // TCP-фолбэк: ICMP часто зарезан, а TCP 443 открыт.
            foreach (var (host, port) in new[] { ("8.8.8.8", 53), ("1.1.1.1", 443), ("77.88.8.8", 443) })
            {
                try
                {
                    using var c = new System.Net.Sockets.TcpClient();
                    var task = c.ConnectAsync(host, port);
                    if (task.Wait(TimeSpan.FromMilliseconds(1200)) && c.Connected) return true;
                }
                catch { }
            }
            // NIC поднят, но пробы не прошли (VPN/файрвол/каптив) — считаем UP,
            // чтобы не было ложного «Network is down».
            return true;
        }
        catch { return true; }
    }

    public static double MinDiskFreeGb()
    {
        try
        {
            double min = double.MaxValue;
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue;
                    if (d.DriveType != DriveType.Fixed) continue;
                    // Recovery/системные разделы <8 ГБ игнорируем — иначе ложные
                    // «Low disk space» при сотнях ГБ свободных на основном диске.
                    double totalGb;
                    try { totalGb = d.TotalSize / 1073741824.0; }
                    catch { continue; }
                    if (totalGb < 8) continue;
                    double freeGb;
                    try { freeGb = d.AvailableFreeSpace / 1073741824.0; }
                    catch { continue; }
                    if (freeGb < 0 || freeGb > totalGb) continue; // битые данные — пропускаем
                    min = Math.Min(min, freeGb);
                }
                catch { }
            }
            return min == double.MaxValue ? -1 : min;
        }
        catch { return -1; }
    }

    private SystemAlert Raise(string kind, string message)
    {
        var a = new SystemAlert { Kind = kind, Message = message };
        lock (_history)
        {
            _history.Add(a);
            while (_history.Count > 200) _history.RemoveAt(0);
        }
        try { SpeakAsync(message); } catch { }
        OnAlert?.Invoke(a);
        return a;
    }

    private static void SpeakAsync(string message)
    {
        // Голосовые уведомления: только если в системе есть SAPI, иначе тихо пропускаем.
        try
        {
            var t = Type.GetTypeFromProgID("SAPI.SpVoice");
            if (t == null) return;
            dynamic voice = Activator.CreateInstance(t)!;
            voice.Speak(message, 1); // async
        }
        catch { }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var list = JsonSerializer.Deserialize<List<SystemAlert>>(File.ReadAllText(_path));
            if (list == null) return;
            lock (_history)
            {
                _history.Clear();
                _history.AddRange(list.TakeLast(200));
            }
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            List<SystemAlert> copy;
            lock (_history) copy = _history.ToList();
            File.WriteAllText(_path, JsonSerializer.Serialize(copy, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
