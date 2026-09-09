using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Media;

namespace SystemGuard.Desktop.Services;

public class AppTheme
{
    public string Name { get; set; } = "Custom";
    public string Bg { get; set; } = "#050508";
    public string Card { get; set; } = "#0C0A15";
    public string CardAlt { get; set; } = "#080610";
    public string Border { get; set; } = "#1A1530";
    public string Accent { get; set; } = "#C96C9E";
    public string AccentLight { get; set; } = "#D48CB5";
    public string Text { get; set; } = "#FAFAFA";
    public string TextMuted { get; set; } = "#71798A";
    public string Danger { get; set; } = "#FB7185";
    public string Success { get; set; } = "#34D399";
    public string Warning { get; set; } = "#FBBF24";
    public string Info { get; set; } = "#38BDF8";

    public AppTheme Clone() => (AppTheme)MemberwiseClone();
}

public class ColorThemeService
{
    public static event Action<AppTheme>? ThemeChanged;

    private readonly string _path;
    private readonly string _profilesPath;

    public ColorThemeService()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SystemGuard");
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "theme.json");
        _profilesPath = Path.Combine(folder, "theme_profiles.json");
    }

    public static readonly string[] ColorKeys =
    {
        "BgColor", "CardColor", "CardAltColor", "BorderColor",
        "AccentColor", "AccentLightColor", "TextColor", "TextMutedColor",
        "DangerColor", "SuccessColor", "WarningColor", "InfoColor"
    };

    public void ApplyTheme(AppTheme theme, bool save = true)
    {
        var app = Application.Current;
        if (app == null) return;

        SetColor(app, "BgColor", theme.Bg);
        SetColor(app, "CardColor", theme.Card);
        SetColor(app, "CardAltColor", theme.CardAlt);
        SetColor(app, "BorderColor", theme.Border);
        SetColor(app, "AccentColor", theme.Accent);
        SetColor(app, "AccentLightColor", theme.AccentLight);
        SetColor(app, "TextColor", theme.Text);
        SetColor(app, "TextMutedColor", theme.TextMuted);
        SetColor(app, "DangerColor", theme.Danger);
        SetColor(app, "SuccessColor", theme.Success);
        SetColor(app, "WarningColor", theme.Warning);
        SetColor(app, "InfoColor", theme.Info);

        if (save) SaveCurrent(theme);
        ThemeChanged?.Invoke(theme.Clone());
    }

    // Совместимость со старым вызовом (8 цветов)
    public void ApplyColors(string bg, string card, string cardAlt, string border, string accent, string accentLight, string text, string textMuted)
    {
        var current = Load() ?? new AppTheme();
        current.Bg = bg; current.Card = card; current.CardAlt = cardAlt; current.Border = border;
        current.Accent = accent; current.AccentLight = accentLight; current.Text = text; current.TextMuted = textMuted;
        ApplyTheme(current);
    }

    private static void SetColor(Application app, string key, string hex)
    {
        try { app.Resources[key] = Color.Parse(hex); } catch { }
    }

    private void SaveCurrent(AppTheme theme)
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(theme, new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }

    public AppTheme? Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var theme = JsonSerializer.Deserialize<AppTheme>(File.ReadAllText(_path), opts);
                if (theme != null) return theme;
            }
        }
        catch { }
        return null;
    }

    public void ApplySavedOrDefault()
    {
        var saved = Load();
        ApplyTheme(saved ?? new AppTheme(), save: false);
    }

    public static Color GetColor(string key, string fallbackHex)
    {
        try
        {
            var app = Application.Current;
            if (app != null && app.Resources.TryGetValue(key, out var v) && v is Color c)
                return c;
            return Color.Parse(fallbackHex);
        }
        catch { return Color.Parse(fallbackHex); }
    }

    // ── Кастомные профили ──
    public List<AppTheme> LoadProfiles()
    {
        try
        {
            if (File.Exists(_profilesPath))
            {
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var list = JsonSerializer.Deserialize<List<AppTheme>>(File.ReadAllText(_profilesPath), opts);
                if (list != null) return list;
            }
        }
        catch { }
        return new List<AppTheme>();
    }

    public void SaveProfiles(List<AppTheme> profiles)
    {
        try { File.WriteAllText(_profilesPath, JsonSerializer.Serialize(profiles, new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }

    public void SaveProfile(AppTheme theme)
    {
        var list = LoadProfiles();
        var existing = list.FirstOrDefault(p => p.Name.Equals(theme.Name, StringComparison.OrdinalIgnoreCase));
        if (existing != null) list.Remove(existing);
        list.Insert(0, theme.Clone());
        SaveProfiles(list);
    }

    public void DeleteProfile(string name)
    {
        var list = LoadProfiles();
        list.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        SaveProfiles(list);
    }
}
