using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SystemGuard.Core.Interfaces;

namespace SystemGuard.Desktop.Services;

public class GameProfileStorageService
{
    private readonly string _path;

    public GameProfileStorageService()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SystemGuard");
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "game_profiles.json");
    }

    public List<GameProfileSnapshot> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new List<GameProfileSnapshot>();
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<List<GameProfileSnapshot>>(json) ?? new();
        }
        catch
        {
            return new List<GameProfileSnapshot>();
        }
    }

    public void Save(IEnumerable<GameProfileSnapshot> profiles)
    {
        try
        {
            var json = JsonSerializer.Serialize(profiles.ToList(), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json);
        }
        catch { }
    }
}