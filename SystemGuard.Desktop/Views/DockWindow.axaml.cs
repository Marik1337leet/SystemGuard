using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.IO;
using SystemGuard.Desktop.Services;

namespace SystemGuard.Desktop.Views;

// Нативный Dock: строится кодом из DockService, иконки из exe,
// точки запущенных, увеличение при наведении, авто-скрытие-затемнение.
public partial class DockWindow : Window
{
    private readonly DockService _service = new();
    private readonly DockSettings _settings;
    private readonly Dictionary<string, Bitmap> _iconCache = new();
    private readonly Dictionary<string, Border> _dots = new();
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<Button, double> _baseScale = new();

    public DockWindow()
    {
        InitializeComponent();
        _settings = _service.LoadSettings();
        ColorThemeService.ThemeChanged += _ => Rebuild();
        BuildDock();
        PositionBottomCenter();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, __) => RefreshDots();
        _timer.Start();
        RefreshDots();
    }

    public void Rebuild()
    {
        BuildDock();
        PositionBottomCenter();
        RefreshDots();
    }

    private void BuildDock()
    {
        var host = this.FindControl<StackPanel>("DockItems");
        if (host == null) return;
        host.Children.Clear();
        _dots.Clear();
        _baseScale.Clear();

        foreach (var app in _service.LoadApps())
        {
            var size = Math.Clamp(_settings.IconSize, 32, 72);
            var btn = new Button
            {
                Width = size + 16,
                Height = size + 22,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(0),
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
                RenderTransformOrigin = new RelativePoint(0.5, 1.0, RelativeUnit.Relative),
                RenderTransform = new ScaleTransform(1, 1),
                Transitions = new Transitions
                {
                    new TransformOperationsTransition
                    {
                        Property = Visual.RenderTransformProperty,
                        Duration = TimeSpan.FromMilliseconds(160),
                        Easing = new Avalonia.Animation.Easings.CubicEaseOut()
                    }
                }
            };
            ToolTip.SetTip(btn, $"{app.Name}\n{app.ExePath}");

            var stack = new StackPanel { Spacing = 2, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
            var img = new Image
            {
                Width = size, Height = size,
                Source = GetIcon(app.ExePath, size),
                Stretch = Stretch.UniformToFill
            };
            var dot = new Border
            {
                Width = 4, Height = 4, CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(Color.Parse("#71798A")),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
            };
            stack.Children.Add(img);
            stack.Children.Add(dot);
            btn.Content = stack;

            var path = app.ExePath;
            btn.Click += (_, __) => DockService.Launch(path);
            if (_settings.Magnification)
            {
                btn.PointerEntered += (_, __) => btn.RenderTransform = new ScaleTransform(1.25, 1.25);
                btn.PointerExited += (_, __) => btn.RenderTransform = new ScaleTransform(1, 1);
            }
            host.Children.Add(btn);
            _dots[path] = dot;
        }

        // Затемнение при простое (безопасный autohide: док не пропадает полностью)
        var bar = this.FindControl<Border>("DockBar");
        if (bar != null)
        {
            bar.Transitions ??= new Transitions
            {
                new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(250) }
            };
            if (_settings.AutoHide)
            {
                bar.Opacity = 0.35;
                bar.PointerEntered += (_, __) => bar.Opacity = 1;
                bar.PointerExited += (_, __) => bar.Opacity = 0.35;
            }
            else bar.Opacity = 1;
        }
    }

    private void RefreshDots()
    {
        var accent = ColorThemeService.GetColor("AccentColor", "#C96C9E");
        var muted = ColorThemeService.GetColor("TextMutedColor", "#71798A");
        foreach (var (path, dot) in _dots)
        {
            bool running = DockService.IsRunning(path);
            dot.Background = running
                ? new SolidColorBrush(accent)
                : new SolidColorBrush(muted);
        }
    }

    private Bitmap? GetIcon(string exePath, int size)
    {
        var key = exePath.ToLowerInvariant() + "|" + size;
        if (_iconCache.TryGetValue(key, out var bmp)) return bmp;
        try
        {
            var png = DockService.ExtractIconPng(exePath, size);
            if (png == null) return null;
            using var ms = new MemoryStream(png);
            bmp = new Bitmap(ms);
            _iconCache[key] = bmp;
            return bmp;
        }
        catch { return null; }
    }

    private void PositionBottomCenter()
    {
        try
        {
            var screen = Screens.Primary;
            if (screen == null) return;
            var area = screen.WorkingArea;
            // Окно SizeToContent: ставим после layout
            Opened += (_, __) =>
            {
                Position = new PixelPoint(
                    area.X + (area.Width - (int)(Bounds.Width)) / 2,
                    area.Y + area.Height - (int)(Bounds.Height) - 4);
            };
        }
        catch { }
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
