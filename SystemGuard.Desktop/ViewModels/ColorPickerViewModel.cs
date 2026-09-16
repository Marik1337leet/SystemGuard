using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using SystemGuard.Desktop.Services;

namespace SystemGuard.Desktop.ViewModels;

public partial class AppColorSlot : ObservableObject
{
    [ObservableProperty] private string _key = "";
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _hexColor = "#FFFFFF";
    [ObservableProperty] private Color _color = Colors.White;
    [ObservableProperty] private bool _isSelected;

    private bool _updatingColor;

    partial void OnHexColorChanged(string value)
    {
        if (_updatingColor) return;
        try
        {
            _updatingColor = true;
            Color = Color.Parse(value);
        }
        catch { }
        finally { _updatingColor = false; }
    }

    partial void OnColorChanged(Color value)
    {
        if (_updatingColor) return;
        try
        {
            _updatingColor = true;
            HexColor = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
        }
        finally { _updatingColor = false; }
    }
}

public partial class ColorPickerViewModel : ViewModelBase
{
    private readonly ColorThemeService _themeService = new();

    public ObservableCollection<AppColorSlot> ColorSlots { get; } = new();

    [ObservableProperty] private AppColorSlot? _selectedSlot;
    [ObservableProperty] private double _hue;
    [ObservableProperty] private double _saturation;
    [ObservableProperty] private double _brightness;
    [ObservableProperty] private double _alpha = 100;
    [ObservableProperty] private double _svCursorX;
    [ObservableProperty] private double _svCursorY;
    [ObservableProperty] private Color _previewColor = Colors.White;
    [ObservableProperty] private string _previewHex = "#FFFFFF";
    [ObservableProperty] private Color _hueSliderBackground = Colors.Red;
    [ObservableProperty] private string _svSquareHue = "#FF0000";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _newProfileName = "";
    [ObservableProperty] private AppTheme? _selectedCustomProfile;

    public Color SvSquareHueColor
    {
        get
        {
            try { return Color.Parse(SvSquareHue); }
            catch { return Colors.Red; }
        }
    }

    public ObservableCollection<ColorPresetItem> Presets { get; } = new()
    {
        new() { Name = "Cytos Rose",   Bg="#050508", Card="#0C0A15", CardAlt="#080610", Border="#1A1530", Accent="#C96C9E", AccentLight="#D48CB5", Text="#FAFAFA", TextMuted="#71798A", Danger="#FB7185", Success="#34D399", Warning="#FBBF24", Info="#38BDF8" },
        new() { Name = "Deep Violet",  Bg="#050508", Card="#0E0B1A", CardAlt="#120E22", Border="#1D1740", Accent="#A78BFA", AccentLight="#C4B5FD", Text="#EDE9FE", TextMuted="#8B80B0", Danger="#F87171", Success="#34D399", Warning="#FBBF24", Info="#38BDF8" },
        new() { Name = "Ocean Depth",  Bg="#020617", Card="#0F172A", CardAlt="#1E293B", Border="#334155", Accent="#38BDF8", AccentLight="#7DD3FC", Text="#F8FAFC", TextMuted="#94A3B8", Danger="#F87171", Success="#22C55E", Warning="#F59E0B", Info="#38BDF8" },
        new() { Name = "Midnight",     Bg="#000000", Card="#111111", CardAlt="#1A1A1A", Border="#333333", Accent="#FFFFFF", AccentLight="#CCCCCC", Text="#FFFFFF",  TextMuted="#888888", Danger="#EF4444", Success="#22C55E", Warning="#F59E0B", Info="#60A5FA" },
        new() { Name = "Emerald",      Bg="#031009", Card="#0A1F12", CardAlt="#0D2718", Border="#1A4028", Accent="#34D399", AccentLight="#6EE7B7", Text="#ECFDF5", TextMuted="#6EE7B7", Danger="#F87171", Success="#34D399", Warning="#FBBF24", Info="#38BDF8" },
        new() { Name = "Sunset",       Bg="#0F0507", Card="#1A0A0E", CardAlt="#220D12", Border="#3D1520", Accent="#FB7185", AccentLight="#FCA5A5", Text="#FFF1F2", TextMuted="#F87171", Danger="#EF4444", Success="#22C55E", Warning="#FBBF24", Info="#38BDF8" },
        new() { Name = "Gold",         Bg="#0C0900", Card="#1A1500", CardAlt="#221B00", Border="#3D3000", Accent="#FBBF24", AccentLight="#FDE68A", Text="#FFFBEB", TextMuted="#F59E0B", Danger="#EF4444", Success="#22C55E", Warning="#FBBF24", Info="#38BDF8" },
        new() { Name = "Light",        Bg="#F8FAFC", Card="#FFFFFF",  CardAlt="#F1F5F9", Border="#E2E8F0", Accent="#6366F1", AccentLight="#818CF8", Text="#0F172A", TextMuted="#64748B", Danger="#EF4444", Success="#16A34A", Warning="#D97706", Info="#0284C7" },
    };

    public ObservableCollection<AppTheme> CustomProfiles { get; } = new();

    public IRelayCommand<AppColorSlot> SelectSlotCommand { get; }
    public IRelayCommand ApplyCommand { get; }
    public IRelayCommand<ColorPresetItem> ApplyPresetCommand { get; }
    public IRelayCommand ResetCommand { get; }
    public IRelayCommand<string> SetHexCommand { get; }
    public IRelayCommand SaveProfileCommand { get; }
    public IRelayCommand<AppTheme> ApplyCustomProfileCommand { get; }
    public IRelayCommand<AppTheme> DeleteProfileCommand { get; }

    public ColorPickerViewModel()
    {
        SelectSlotCommand = new RelayCommand<AppColorSlot>(SelectSlot);
        ApplyCommand = new RelayCommand(Apply);
        ApplyPresetCommand = new RelayCommand<ColorPresetItem>(ApplyPreset);
        ResetCommand = new RelayCommand(Reset);
        SetHexCommand = new RelayCommand<string>(SetHex);
        SaveProfileCommand = new RelayCommand(SaveProfile);
        ApplyCustomProfileCommand = new RelayCommand<AppTheme>(ApplyCustomProfile);
        DeleteProfileCommand = new RelayCommand<AppTheme>(DeleteProfile);

        InitSlots();
        LoadSaved();
        LoadCustomProfiles();
        if (ColorSlots.Count > 0) SelectSlot(ColorSlots[0]);
    }

    private void InitSlots()
    {
        ColorSlots.Clear();
        var defaults = new[]
        {
            ("BgColor",          "Background",         "#050508"),
            ("CardColor",        "Card Surface",       "#0C0A15"),
            ("CardAltColor",     "Card Alt",           "#080610"),
            ("BorderColor",      "Border",             "#1A1530"),
            ("AccentColor",      "Accent",             "#C96C9E"),
            ("AccentLightColor", "Accent Light",       "#D48CB5"),
            ("TextColor",        "Text Primary",       "#FAFAFA"),
            ("TextMutedColor",   "Text Muted",         "#71798A"),
            ("DangerColor",      "Danger / Error",     "#FB7185"),
            ("SuccessColor",     "Success",            "#34D399"),
            ("WarningColor",     "Warning",            "#FBBF24"),
            ("InfoColor",        "Info",               "#38BDF8"),
        };

        foreach (var (key, name, hex) in defaults)
            ColorSlots.Add(new AppColorSlot { Key = key, DisplayName = name, HexColor = hex });
    }

    private void LoadSaved()
    {
        var saved = _themeService.Load();
        if (saved == null) return;
        ApplyThemeToSlots(saved);
    }

    private void ApplyThemeToSlots(AppTheme t)
    {
        SetSlotHex("BgColor", t.Bg);
        SetSlotHex("CardColor", t.Card);
        SetSlotHex("CardAltColor", t.CardAlt);
        SetSlotHex("BorderColor", t.Border);
        SetSlotHex("AccentColor", t.Accent);
        SetSlotHex("AccentLightColor", t.AccentLight);
        SetSlotHex("TextColor", t.Text);
        SetSlotHex("TextMutedColor", t.TextMuted);
        SetSlotHex("DangerColor", t.Danger);
        SetSlotHex("SuccessColor", t.Success);
        SetSlotHex("WarningColor", t.Warning);
        SetSlotHex("InfoColor", t.Info);
    }

    private AppTheme BuildThemeFromSlots(string name)
    {
        string Get(string key) => FindSlot(key)?.HexColor ?? "#000000";
        return new AppTheme
        {
            Name = name,
            Bg = Get("BgColor"), Card = Get("CardColor"), CardAlt = Get("CardAltColor"),
            Border = Get("BorderColor"), Accent = Get("AccentColor"), AccentLight = Get("AccentLightColor"),
            Text = Get("TextColor"), TextMuted = Get("TextMutedColor"),
            Danger = Get("DangerColor"), Success = Get("SuccessColor"),
            Warning = Get("WarningColor"), Info = Get("InfoColor")
        };
    }

    private void LoadCustomProfiles()
    {
        CustomProfiles.Clear();
        foreach (var p in _themeService.LoadProfiles())
            CustomProfiles.Add(p);
    }

    private void SaveProfile()
    {
        var name = string.IsNullOrWhiteSpace(NewProfileName) ? $"My Theme {CustomProfiles.Count + 1}" : NewProfileName.Trim();
        var theme = BuildThemeFromSlots(name);
        EnsureReadableText(theme);
        ApplyThemeToSlots(theme);
        _themeService.SaveProfile(theme);
        LoadCustomProfiles();
        NewProfileName = "";
        StatusText = $"Profile '{name}' saved";
        ClearStatus();
    }

    private void ApplyCustomProfile(AppTheme? p)
    {
        if (p == null) return;
        ApplyThemeToSlots(p);
        Apply();
        if (SelectedSlot != null) SelectSlot(SelectedSlot);
        StatusText = $"Profile '{p.Name}' applied";
    }

    private void DeleteProfile(AppTheme? p)
    {
        if (p == null) return;
        _themeService.DeleteProfile(p.Name);
        LoadCustomProfiles();
        StatusText = $"Profile '{p.Name}' deleted";
        ClearStatus();
    }

    private void SetSlotHex(string key, string hex)
    {
        var slot = FindSlot(key);
        if (slot != null) slot.HexColor = hex;
    }

    private AppColorSlot? FindSlot(string key)
    {
        foreach (var s in ColorSlots)
            if (s.Key == key) return s;
        return null;
    }

    private void SelectSlot(AppColorSlot? slot)
    {
        if (slot == null) return;
        foreach (var s in ColorSlots) s.IsSelected = false;
        slot.IsSelected = true;
        SelectedSlot = slot;
        try
        {
            var color = Color.Parse(slot.HexColor);
            RgbToHsv(color.R, color.G, color.B, out var h, out var s2, out var v);
            _updatingFromHsv = true;
            Hue = h; Saturation = s2 * 100; Brightness = v * 100;
            _updatingFromHsv = false;
            SyncCursorFromHsv();
            UpdatePreview();
        }
        catch { }
    }

    private bool _updatingFromHsv;

    partial void OnHueChanged(double value)
    {
        if (_updatingFromHsv) return;
        SvSquareHue = HsvToHex(value, 1, 1);
        OnPropertyChanged(nameof(SvSquareHueColor));
        UpdatePreview();
    }

    partial void OnSaturationChanged(double value)
    {
        if (_updatingFromHsv) return;
        SyncCursorFromHsv();
        UpdatePreview();
    }

    partial void OnBrightnessChanged(double value)
    {
        if (_updatingFromHsv) return;
        SyncCursorFromHsv();
        UpdatePreview();
    }

    private void SyncCursorFromHsv()
    {
        SvCursorX = Saturation / 100.0;
        SvCursorY = 1.0 - Brightness / 100.0;
    }

    private void UpdatePreview()
    {
        var color = HsvToColor(Hue, Saturation / 100.0, Brightness / 100.0);
        PreviewColor = color;
        PreviewHex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        if (SelectedSlot != null)
            SelectedSlot.HexColor = PreviewHex;
    }

    public void OnSvSquareClick(double relativeX, double relativeY)
    {
        SvCursorX = Math.Clamp(relativeX, 0, 1);
        SvCursorY = Math.Clamp(relativeY, 0, 1);
        _updatingFromHsv = true;
        Saturation = SvCursorX * 100;
        Brightness = (1.0 - SvCursorY) * 100;
        _updatingFromHsv = false;
        UpdatePreview();
    }

    private void SetHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return;
        if (!hex.StartsWith('#')) hex = '#' + hex;
        try
        {
            var color = Color.Parse(hex);
            RgbToHsv(color.R, color.G, color.B, out var h, out var s, out var v);
            _updatingFromHsv = true;
            Hue = h; Saturation = s * 100; Brightness = v * 100;
            _updatingFromHsv = false;
            SyncCursorFromHsv();
            UpdatePreview();
        }
        catch { }
    }

    private void Apply()
    {
        var theme = BuildThemeFromSlots("Current");
        // Светлый фон + светлый текст = нечитаемо. Принудительно тёмный текст.
        EnsureReadableText(theme);
        ApplyThemeToSlots(theme);
        _themeService.ApplyTheme(theme);
        StatusText = "Theme applied — app + widgets updated";
        ClearStatus();
    }

    // Светлая карточка/фон со светлым текстом — текст всегда делаем тёмным,
    // иначе на светлых темах его не видно.
    private static void EnsureReadableText(AppTheme t)
    {
        try
        {
            bool surfaceLight = IsLightHex(t.Card) || IsLightHex(t.Bg);
            if (surfaceLight && IsLightHex(t.Text)) t.Text = "#111318";
            if (surfaceLight && IsLightHex(t.TextMuted)) t.TextMuted = "#5B6472";
        }
        catch { }
    }

    private static bool IsLightHex(string hex)
    {
        try
        {
            var c = Color.Parse(hex.StartsWith('#') ? hex : '#' + hex);
            return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0 > 0.6;
        }
        catch { return false; }
    }

    private void ApplyPreset(ColorPresetItem? p)
    {
        if (p == null) return;
        var theme = new AppTheme
        {
            Name = p.Name, Bg = p.Bg, Card = p.Card, CardAlt = p.CardAlt, Border = p.Border,
            Accent = p.Accent, AccentLight = p.AccentLight, Text = p.Text, TextMuted = p.TextMuted,
            Danger = p.Danger, Success = p.Success, Warning = p.Warning, Info = p.Info
        };
        EnsureReadableText(theme);
        ApplyThemeToSlots(theme);
        Apply();
        if (SelectedSlot != null) SelectSlot(SelectedSlot);
        StatusText = $"Preset '{p.Name}' applied";
    }

    private void Reset() => ApplyPreset(Presets[0]);

    private async void ClearStatus()
    {
        await Task.Delay(2500);
        StatusText = "";
    }

    private static Color HsvToColor(double h, double s, double v)
    {
        if (s == 0)
        {
            var g = (byte)(v * 255);
            return Color.FromRgb(g, g, g);
        }
        h /= 60;
        var i = (int)h;
        var f = h - i;
        var p = v * (1 - s);
        var q = v * (1 - s * f);
        var t = v * (1 - s * (1 - f));
        double r, gr, b;
        switch (i % 6)
        {
            case 0: r = v; gr = t; b = p; break;
            case 1: r = q; gr = v; b = p; break;
            case 2: r = p; gr = v; b = t; break;
            case 3: r = p; gr = q; b = v; break;
            case 4: r = t; gr = p; b = v; break;
            default: r = v; gr = p; b = q; break;
        }
        return Color.FromRgb((byte)(r * 255), (byte)(gr * 255), (byte)(b * 255));
    }

    private static string HsvToHex(double h, double s, double v)
    {
        var c = HsvToColor(h, s, v);
        return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    private static void RgbToHsv(byte r, byte g, byte b, out double h, out double s, out double v)
    {
        var rf = r / 255.0; var gf = g / 255.0; var bf = b / 255.0;
        var max = Math.Max(rf, Math.Max(gf, bf));
        var min = Math.Min(rf, Math.Min(gf, bf));
        var delta = max - min;
        v = max;
        s = max == 0 ? 0 : delta / max;
        if (delta == 0) { h = 0; return; }
        if (max == rf) h = 60 * (((gf - bf) / delta) % 6);
        else if (max == gf) h = 60 * ((bf - rf) / delta + 2);
        else h = 60 * ((rf - gf) / delta + 4);
        if (h < 0) h += 360;
    }
}

public class ColorPresetItem
{
    public string Name { get; set; } = "";
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

    public Color AccentAsColor => ParseColor(Accent);
    public Color CardAsColor => ParseColor(Card);
    public Color BgAsColor => ParseColor(Bg);
    public Color TextAsColor => ParseColor(Text);

    private static Color ParseColor(string hex)
    {
        try { return Color.Parse(hex); }
        catch { return Colors.Gray; }
    }
}
