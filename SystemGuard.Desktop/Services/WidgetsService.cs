using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace SystemGuard.Desktop.Services;

public class WidgetsSettings
{
    public bool Enabled { get; set; }
    public bool ShowClock { get; set; } = true;
    public bool ShowSystem { get; set; } = true;
    public bool ShowWeather { get; set; }
    public string City { get; set; } = "Moscow";
    public int RefreshSeconds { get; set; } = 30; // погода
    // Порядок виджетов для drag-and-drop: имена плиток ("Clock","System","Weather",...).
    public List<string> WidgetOrder { get; set; } = new() { "Clock", "System", "Weather" };
}

public class WeatherInfo
{
    public string TempC { get; set; } = "—";
    public string Desc { get; set; } = "";
    public string WindKph { get; set; } = "";
    public string Humidity { get; set; } = "";
}

// ── Нативные виджеты вместо Rainmeter: часы / система / погода ──────────────
// Погода — wttr.in (без API-ключа). Системные счётчики — PerformanceCounter.
public class WidgetsService
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly string _settingsPath;

    private static System.Diagnostics.PerformanceCounter? _cpuCounter;
    private static System.Diagnostics.PerformanceCounter? _ramCounter;

    public WidgetsService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SystemGuard");
        Directory.CreateDirectory(dir);
        _settingsPath = Path.Combine(dir, "widgets.json");
    }

    public WidgetsSettings Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
                return JsonSerializer.Deserialize<WidgetsSettings>(File.ReadAllText(_settingsPath)) ?? new();
        }
        catch { }
        return new WidgetsSettings();
    }

    public void Save(WidgetsSettings s)
    {
        try { File.WriteAllText(_settingsPath, JsonSerializer.Serialize(s)); } catch { }
    }

    // Drag-and-drop переупорядочивание: переместить виджет на новую позицию и сохранить.
    public WidgetsSettings MoveWidget(WidgetsSettings s, string widgetName, int newIndex)
    {
        s.WidgetOrder ??= new List<string> { "Clock", "System", "Weather" };
        s.WidgetOrder.Remove(widgetName);
        newIndex = Math.Clamp(newIndex, 0, s.WidgetOrder.Count);
        s.WidgetOrder.Insert(newIndex, widgetName);
        Save(s);
        return s;
    }

    public static (double Cpu, double RamUsedGb, double RamTotalGb) GetSystemStats()
    {
        try
        {
            _cpuCounter ??= new System.Diagnostics.PerformanceCounter("Processor", "% Processor Time", "_Total");
            _ramCounter ??= new System.Diagnostics.PerformanceCounter("Memory", "Available MBytes");
            // Первый замер всегда 0 — прогреваем один раз
            _ = _cpuCounter.NextValue();
            double cpu = Math.Round(_cpuCounter.NextValue(), 1);
            double availMb = _ramCounter.NextValue();
            // Total — физическая RAM (GlobalMemoryStatusEx), НЕ GC-метрика с pagefile
            double totalGb = DetailedSystemInfoService.GetTotalPhysicalGb();
            double usedGb = Math.Max(0, Math.Round(totalGb - availMb / 1024.0, 1));
            return (cpu, usedGb, Math.Round(totalGb, 1));
        }
        catch { return (0, 0, 0); }
    }

    public static async Task<WeatherInfo> GetWeatherAsync(string city)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(city)) return new WeatherInfo();
            var url = $"https://wttr.in/{Uri.EscapeDataString(city.Trim())}?format=j1";
            using var res = await _http.GetAsync(url);
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var cur = doc.RootElement.GetProperty("current_condition")[0];
            return new WeatherInfo
            {
                TempC = cur.GetProperty("temp_C").GetString() ?? "—",
                Desc = cur.GetProperty("weatherDesc")[0].GetProperty("value").GetString() ?? "",
                WindKph = cur.TryGetProperty("windspeedKmph", out var w) ? (w.GetString() ?? "") : "",
                Humidity = cur.TryGetProperty("humidity", out var h) ? (h.GetString() ?? "") : ""
            };
        }
        catch { return new WeatherInfo { Desc = "offline" }; }
    }

    public static void OpenWeatherPage(string city)
    {
        try
        {
            Process.Start(new ProcessStartInfo($"https://wttr.in/{Uri.EscapeDataString(city.Trim())}")
            { UseShellExecute = true });
        }
        catch { }
    }
}
