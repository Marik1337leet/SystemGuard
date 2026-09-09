using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace SystemGuard.Desktop.Services;

// Экспорт отчётов мониторинга в CSV и JSON. Всё реально пишется на диск.
public static class ReportExportService
{
    public static string ExportCsv(List<MonitoringSample> samples, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Time,CpuLoad,CpuTemp,RamPercent,NetDownMbps,NetUpMbps,DiskFreeGb");
        foreach (var s in samples)
            sb.AppendLine($"{s.Time:O},{s.CpuLoad:F1},{s.CpuTemp:F1},{s.RamPercent:F1},{s.NetDownMbps:F2},{s.NetUpMbps:F2},{s.DiskFreeGb:F2}");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        return path;
    }

    public static string ExportJson(List<MonitoringSample> samples, string path)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    public static string DefaultPath(string rangeName, string format)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        return Path.Combine(desktop, $"SystemGuard_{rangeName}_{stamp}.{format}");
    }
}
