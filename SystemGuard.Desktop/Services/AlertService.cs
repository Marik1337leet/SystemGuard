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

// Реальные проверки: температура >80°, свободное место <5 ГБ, сеть упала (ping шлюза/DNS).
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

        if (cpuTemp > 80 && now - _lastTempAlert > Cooldown)
        {
            _lastTempAlert = now;
            res.NewAlerts.Add(Raise("Temp", $"CPU temperature {cpuTemp:F0}°C is above 80°C"));
        }

        if (minDiskFreeGb >= 0 && minDiskFreeGb < 5 && now - _lastDiskAlert > Cooldown)
        {
            _lastDiskAlert = now;
            res.NewAlerts.Add(Raise("Disk", $"Low disk space: {minDiskFreeGb:F1} GB free (less than 5 GB)"));
        }

        if (!networkUp && now - _lastNetAlert > Cooldown)
        {
            _lastNetAlert = now;
            res.NewAlerts.Add(Raise("Network", "Network is down (ping failed)"));
        }

        if (res.NewAlerts.Count > 0) Save();
        return res;
    }

    public static bool IsNetworkUp()
    {
        try
        {
            using var ping = new Ping();
            var reply = ping.Send("8.8.8.8", 1500);
            return reply?.Status == IPStatus.Success;
        }
        catch { return false; }
    }

    public static double MinDiskFreeGb()
    {
        try
        {
            double min = double.MaxValue;
            foreach (var d in DriveInfo.GetDrives())
            {
                if (!d.IsReady) continue;
                if (d.DriveType != DriveType.Fixed) continue;
                min = Math.Min(min, d.AvailableFreeSpace / 1073741824.0);
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
