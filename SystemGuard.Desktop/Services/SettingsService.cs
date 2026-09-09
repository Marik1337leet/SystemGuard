using System;
using System.IO;
using System.Text.Json;
using SystemGuard.Desktop.Models;

namespace SystemGuard.Desktop.Services;

public class SettingsService
{
    private readonly string _settingsPath;

    public SettingsService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var folder = Path.Combine(appData, "SystemGuard");
        Directory.CreateDirectory(folder);
        _settingsPath = Path.Combine(folder, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);
        }
        catch { }
    }

    public void Export(string path)
    {
        try { File.Copy(_settingsPath, path, true); } catch { }
    }

    public void Import(string path)
    {
        try { File.Copy(path, _settingsPath, true); } catch { }
    }

    public void Reset()
    {
        try { File.Delete(_settingsPath); } catch { }
    }
}