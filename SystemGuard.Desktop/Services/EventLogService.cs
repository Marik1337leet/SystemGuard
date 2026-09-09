using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace SystemGuard.Desktop.Services;

public class EventLogEntry
{
    public DateTime Time { get; set; }
    public string Level { get; set; } = "";
    public string Source { get; set; } = "";
    public string Message { get; set; } = "";
}

// Журнал событий Windows: последние N записей (Application/System). Вне Windows — пусто.
public static class EventLogService
{
    public static List<EventLogEntry> Read(string logName = "Application", int max = 50)
    {
        var list = new List<EventLogEntry>();
        try
        {
            if (!OperatingSystem.IsWindows()) return list;
            using var log = new EventLog(logName);
            foreach (System.Diagnostics.EventLogEntry e in log.Entries.Cast<System.Diagnostics.EventLogEntry>().TakeLast(max))
            {
                list.Add(new EventLogEntry
                {
                    Time = e.TimeGenerated,
                    Level = e.EntryType.ToString(),
                    Source = e.Source,
                    Message = e.Message.Length > 300 ? e.Message[..300] + "…" : e.Message
                });
            }
            list.Reverse();
        }
        catch { }
        return list;
    }
}
