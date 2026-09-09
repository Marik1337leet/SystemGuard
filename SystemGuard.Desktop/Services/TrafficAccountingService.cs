using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SystemGuard.Desktop.Services;

public class TrafficDay
{
    public string Date { get; set; } = DateTime.Now.ToString("yyyy-MM-dd");
    public long Downloaded { get; set; }
    public long Uploaded { get; set; }
}

// Учёт трафика по дням и месяцам. Record вызывается раз в минуту/при тике.
public class TrafficAccountingService
{
    private readonly string _path;
    private readonly object _lock = new();

    public TrafficAccountingService(string? baseDir = null)
    {
        var dir = baseDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SystemGuard");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "traffic_accounting.json");
    }

    public void Record(long totalDownloaded, long totalUploaded)
    {
        try
        {
            lock (_lock)
            {
                var days = LoadLocked();
                var key = DateTime.Now.ToString("yyyy-MM-dd");
                var day = days.FirstOrDefault(d => d.Date == key);
                if (day == null) { day = new TrafficDay { Date = key }; days.Add(day); }
                day.Downloaded = Math.Max(day.Downloaded, totalDownloaded);
                day.Uploaded = Math.Max(day.Uploaded, totalUploaded);
                while (days.Count > 400) days.RemoveAt(0);
                File.WriteAllText(_path, JsonSerializer.Serialize(days));
            }
        }
        catch { }
    }

    public List<TrafficDay> LastDays(int n)
    {
        lock (_lock) return LoadLocked().OrderBy(d => d.Date).TakeLast(n).ToList();
    }

    public (long Down, long Up) CurrentMonthTotal()
    {
        var prefix = DateTime.Now.ToString("yyyy-MM");
        lock (_lock)
        {
            var days = LoadLocked().Where(d => d.Date.StartsWith(prefix)).ToList();
            return (days.Sum(d => d.Downloaded), days.Sum(d => d.Uploaded));
        }
    }

    private List<TrafficDay> LoadLocked()
    {
        try
        {
            if (!File.Exists(_path)) return new List<TrafficDay>();
            return JsonSerializer.Deserialize<List<TrafficDay>>(File.ReadAllText(_path)) ?? new List<TrafficDay>();
        }
        catch { return new List<TrafficDay>(); }
    }
}
