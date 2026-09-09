using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SystemGuard.Desktop.Models;

namespace SystemGuard.Desktop.Services;

public class BenchmarkRun
{
    public DateTime Time { get; set; } = DateTime.Now;
    public List<BenchmarkResult> Results { get; set; } = new();
    public string Note { get; set; } = "";
}

// История бенчмарков: сохранение прогонов, сравнение с предыдущим, экспорт.
public class BenchmarkHistoryService
{
    private readonly string _path;

    public BenchmarkHistoryService(string? baseDir = null)
    {
        var dir = baseDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SystemGuard");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "benchmark_history.json");
    }

    public void SaveRun(List<BenchmarkResult> results, string note = "")
    {
        try
        {
            var runs = ListRuns();
            runs.Add(new BenchmarkRun { Results = results, Note = note });
            while (runs.Count > 50) runs.RemoveAt(0);
            File.WriteAllText(_path, JsonSerializer.Serialize(runs, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public List<BenchmarkRun> ListRuns()
    {
        try
        {
            if (!File.Exists(_path)) return new List<BenchmarkRun>();
            return JsonSerializer.Deserialize<List<BenchmarkRun>>(File.ReadAllText(_path)) ?? new List<BenchmarkRun>();
        }
        catch { return new List<BenchmarkRun>(); }
    }

    // Сравнение последнего прогона с предпоследним: +X% / -X% по каждому тесту.
    public static List<string> CompareRuns(BenchmarkRun current, BenchmarkRun previous)
    {
        var lines = new List<string>();
        var prev = previous.Results.ToDictionary(r => r.TestName, r => r.Score);
        foreach (var r in current.Results)
        {
            if (prev.TryGetValue(r.TestName, out var oldScore) && oldScore > 0)
            {
                var delta = (r.Score - oldScore) / oldScore * 100.0;
                lines.Add($"{r.TestName}: {(delta >= 0 ? "+" : "")}{delta:F1}% vs previous");
            }
            else lines.Add($"{r.TestName}: no previous data");
        }
        return lines;
    }
}
