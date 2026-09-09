using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SystemGuard.Desktop.Services;

public class MonitoringSample
{
    public DateTime Time { get; set; } = DateTime.Now;
    public double CpuLoad { get; set; }
    public double CpuTemp { get; set; }
    public double RamPercent { get; set; }
    public double NetDownMbps { get; set; }
    public double NetUpMbps { get; set; }
    public double DiskFreeGb { get; set; }
}

// История мониторинга: пишется каждый семпл, хранится 30 дней, отдаёт 24ч/7д/30д.
public class MonitoringHistoryService
{
    private readonly string _path;
    private readonly List<MonitoringSample> _samples = new();
    private readonly object _lock = new();
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    public MonitoringHistoryService()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SystemGuard");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "monitoring_history.json");
        Load();
    }

    public void Record(MonitoringSample s)
    {
        lock (_lock)
        {
            _samples.Add(s);
            var cutoff = DateTime.Now - Retention;
            _samples.RemoveAll(x => x.Time < cutoff);
        }
    }

    public void Save()
    {
        try
        {
            List<MonitoringSample> copy;
            lock (_lock) copy = _samples.ToList();
            File.WriteAllText(_path, JsonSerializer.Serialize(copy));
        }
        catch { }
    }

    public List<MonitoringSample> GetRange(TimeSpan span)
    {
        var cutoff = DateTime.Now - span;
        lock (_lock) return _samples.Where(x => x.Time >= cutoff).OrderBy(x => x.Time).ToList();
    }

    public List<MonitoringSample> Get24h() => GetRange(TimeSpan.FromHours(24));
    public List<MonitoringSample> Get7d() => GetRange(TimeSpan.FromDays(7));
    public List<MonitoringSample> Get30d() => GetRange(TimeSpan.FromDays(30));

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var list = JsonSerializer.Deserialize<List<MonitoringSample>>(File.ReadAllText(_path));
            if (list == null) return;
            var cutoff = DateTime.Now - Retention;
            lock (_lock)
            {
                _samples.Clear();
                _samples.AddRange(list.Where(x => x.Time >= cutoff));
            }
        }
        catch { }
    }
}
