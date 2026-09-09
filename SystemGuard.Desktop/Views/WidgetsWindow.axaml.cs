using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using SystemGuard.Desktop.Services;
using System.IO;

namespace SystemGuard.Desktop.Views;

// Нативные виджеты: часы / система / погода. Перетаскиваются за любую карточку.
public partial class WidgetsWindow : Window
{
    private readonly WidgetsService _service = new();
    private WidgetsSettings _settings = new();
    private readonly DispatcherTimer _fastTimer;
    private readonly DispatcherTimer _weatherTimer;

    private TextBlock? _clockTime;
    private TextBlock? _clockDate;
    private TextBlock? _cpuText;
    private TextBlock? _ramText;
    private TextBlock? _sysExtraText;
    private ProgressBar? _cpuBar;
    private ProgressBar? _ramBar;
    private TextBlock? _weatherTemp;
    private TextBlock? _weatherDesc;
    private TextBlock? _weatherExtra;

    public WidgetsWindow()
    {
        InitializeComponent();
        _settings = _service.Load();
        ColorThemeService.ThemeChanged += _ => Rebuild();
        Rebuild();

        _fastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _fastTimer.Tick += (_, __) => TickFast();
        _fastTimer.Start();

        _weatherTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Clamp(_settings.RefreshSeconds, 60, 3600)) };
        _weatherTimer.Tick += (_, __) => _ = TickWeatherAsync();
        _weatherTimer.Start();
        _ = TickWeatherAsync();

        PositionTopRight();
    }

    public void Rebuild()
    {
        _settings = _service.Load();
        var host = this.FindControl<StackPanel>("Cards");
        if (host == null) return;
        host.Children.Clear();
        _clockTime = _clockDate = _cpuText = _ramText = _weatherTemp = _weatherDesc = null;
        _cpuBar = _ramBar = null;

        if (_settings.ShowClock) host.Children.Add(BuildClockCard());
        if (_settings.ShowSystem) host.Children.Add(BuildSystemCard());
        if (_settings.ShowWeather) host.Children.Add(BuildWeatherCard());
        TickFast();
        _ = TickWeatherAsync();
    }

    private static string UptimeShort()
    {
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        return up.TotalDays >= 1 ? $"{(int)up.TotalDays}d {up.Hours}h"
            : up.TotalHours >= 1 ? $"{(int)up.TotalHours}h {up.Minutes}m"
            : $"{up.Minutes}m";
    }

    private static string SystemDriveFree()
    {
        try
        {
            var sys = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            foreach (var d in DriveInfo.GetDrives())
            {
                if (!d.IsReady) continue;
                if (string.Equals(d.Name.TrimEnd('\\'), sys.TrimEnd('\\'),
                        StringComparison.OrdinalIgnoreCase))
                    return $"{d.AvailableFreeSpace / 1073741824.0:F0} GB free";
            }
        }
        catch { }
        return "";
    }

    private Border GlassCard(Control content)
    {
        var card = ColorThemeService.GetColor("CardColor", "#0C0A15");
        var inner = new Border
        {
            CornerRadius = new CornerRadius(20),
            Background = new SolidColorBrush(card, 0.85),
            BorderBrush = new SolidColorBrush(ColorThemeService.GetColor("BorderColor", "#1A1530")),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18, 14),
            Child = content
        };
        inner.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(inner).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };
        var wrap = new Border
        {
            CornerRadius = new CornerRadius(20),
            IsHitTestVisible = false,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 0.55, RelativeUnit.Relative),
                GradientStops = new GradientStops
                {
                    new GradientStop(Color.Parse("#1FFFFFFF"), 0),
                    new GradientStop(Color.Parse("#00FFFFFF"), 1)
                }
            }
        };
        var panel = new Panel();
        panel.Children.Add(inner);
        panel.Children.Add(wrap);
        var outer = new Border { CornerRadius = new CornerRadius(20), Child = panel };
        return outer;
    }

    private Control BuildClockCard()
    {
        var text = ColorThemeService.GetColor("TextColor", "#FAFAFA");
        var muted = ColorThemeService.GetColor("TextMutedColor", "#71798A");
        var stack = new StackPanel { Spacing = 2 };
        _clockTime = new TextBlock
        {
            FontSize = 30, FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(text),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        _clockDate = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(muted)
        };
        stack.Children.Add(_clockTime);
        stack.Children.Add(_clockDate);
        return GlassCard(stack);
    }

    private Control BuildSystemCard()
    {
        var muted = ColorThemeService.GetColor("TextMutedColor", "#71798A");
        var stack = new StackPanel { Spacing = 8 };
        var title = new TextBlock
        {
            Text = "SYSTEM", FontSize = 9,
            Foreground = new SolidColorBrush(muted)
        };
        stack.Children.Add(title);
        _cpuText = AddMeter(stack, "CPU");
        _cpuBar = AddBar(stack);
        _ramText = AddMeter(stack, "RAM");
        _ramBar = AddBar(stack);
        _sysExtraText = new TextBlock
        {
            FontSize = 9,
            Foreground = new SolidColorBrush(muted)
        };
        stack.Children.Add(_sysExtraText);
        return GlassCard(stack);
    }

    private TextBlock AddMeter(StackPanel parent, string label)
    {
        var muted = ColorThemeService.GetColor("TextMutedColor", "#71798A");
        var accent = ColorThemeService.GetColor("AccentColor", "#C96C9E");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var l = new TextBlock { Text = label, FontSize = 10, Foreground = new SolidColorBrush(muted) };
        var v = new TextBlock { FontSize = 13, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(accent) };
        grid.Children.Add(l);
        Grid.SetColumn(v, 1);
        grid.Children.Add(v);
        parent.Children.Add(grid);
        return v;
    }

    private ProgressBar AddBar(StackPanel parent)
    {
        var accent = ColorThemeService.GetColor("AccentColor", "#C96C9E");
        var bar = new ProgressBar
        {
            Minimum = 0, Maximum = 100, Height = 4, CornerRadius = new CornerRadius(2),
            Foreground = new SolidColorBrush(accent),
            Background = new SolidColorBrush(Color.Parse("#14FFFFFF"))
        };
        parent.Children.Add(bar);
        return bar;
    }

    private Control BuildWeatherCard()
    {
        var muted = ColorThemeService.GetColor("TextMutedColor", "#71798A");
        var text = ColorThemeService.GetColor("TextColor", "#FAFAFA");
        var accentLight = ColorThemeService.GetColor("AccentLightColor", "#D48CB5");
        var stack = new StackPanel { Spacing = 2 };
        var city = new TextBlock
        {
            Text = _settings.City.ToUpper(), FontSize = 9,
            Foreground = new SolidColorBrush(muted)
        };
        _weatherTemp = new TextBlock
        {
            FontSize = 30, FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(accentLight)
        };
        _weatherDesc = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(text),
            TextWrapping = TextWrapping.Wrap
        };
        _weatherExtra = new TextBlock
        {
            FontSize = 9,
            Foreground = new SolidColorBrush(muted)
        };
        stack.Children.Add(city);
        stack.Children.Add(_weatherTemp);
        stack.Children.Add(_weatherDesc);
        stack.Children.Add(_weatherExtra);
        var card = GlassCard(stack);
        card.PointerPressed += (_, __) => WidgetsService.OpenWeatherPage(_settings.City);
        return card;
    }

    private void TickFast()
    {
        var now = DateTime.Now;
        if (_clockTime != null) _clockTime.Text = now.ToString("HH:mm:ss");
        if (_clockDate != null) _clockDate.Text = now.ToString("dddd, d MMMM");
        if (_cpuText != null || _ramText != null)
        {
            var (cpu, usedGb, totalGb) = WidgetsService.GetSystemStats();
            if (_cpuText != null) _cpuText.Text = $"{cpu:F0}%";
            if (_cpuBar != null) _cpuBar.Value = cpu;
            if (_ramText != null) _ramText.Text = $"{usedGb:F1} / {totalGb:F1} GB";
            if (_ramBar != null) _ramBar.Value = totalGb > 0 ? usedGb / totalGb * 100 : 0;
            if (_sysExtraText != null) _sysExtraText.Text = $"Up {UptimeShort()}  •  C: {SystemDriveFree()}";
        }
    }

    private async System.Threading.Tasks.Task TickWeatherAsync()
    {
        if (_weatherTemp == null || _weatherDesc == null || !_settings.ShowWeather) return;
        var w = await WidgetsService.GetWeatherAsync(_settings.City);
        _weatherTemp.Text = $"{w.TempC}°C";
        _weatherDesc.Text = w.Desc;
        if (_weatherExtra != null)
        {
            var parts = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrEmpty(w.WindKph)) parts.Add($"Wind {w.WindKph} km/h");
            if (!string.IsNullOrEmpty(w.Humidity)) parts.Add($"Hum {w.Humidity}%");
            _weatherExtra.Text = string.Join("  •  ", parts);
        }
    }

    private void PositionTopRight()
    {
        try
        {
            var screen = Screens.Primary;
            if (screen == null) return;
            var area = screen.WorkingArea;
            Opened += (_, __) =>
            {
                Position = new PixelPoint(
                    area.X + area.Width - (int)Bounds.Width - 10,
                    area.Y + 10);
            };
        }
        catch { }
    }

    protected override void OnClosed(EventArgs e)
    {
        _fastTimer.Stop();
        _weatherTimer.Stop();
        base.OnClosed(e);
    }
}
