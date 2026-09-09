using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SystemGuard.Desktop.Services;

public class AuditEntry
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string Area { get; set; } = "";
    public string Action { get; set; } = "";
    public string Details { get; set; } = "";
}

// Аудит действий (для корпоративных отчётов): дописывает JSONL, читает последние N.
public class AuditLogService
{
    private readonly string _path;
    private readonly object _lock = new();

    public AuditLogService(string? baseDir = null)
    {
        var dir = baseDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SystemGuard");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "audit.jsonl");
    }

    public void Log(string area, string action, string details = "")
    {
        try
        {
            var line = JsonSerializer.Serialize(new AuditEntry { Area = area, Action = action, Details = details });
            lock (_lock) File.AppendAllText(_path, line + Environment.NewLine);
        }
        catch { }
    }

    public List<AuditEntry> Last(int n)
    {
        try
        {
            if (!File.Exists(_path)) return new List<AuditEntry>();
            var lines = File.ReadAllLines(_path);
            return lines.TakeLast(n).Select(l =>
            {
                try { return JsonSerializer.Deserialize<AuditEntry>(l); }
                catch { return null; }
            }).Where(e => e != null).Cast<AuditEntry>().Reverse().ToList();
        }
        catch { return new List<AuditEntry>(); }
    }
}
