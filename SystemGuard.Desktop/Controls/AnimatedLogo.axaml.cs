using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using System;

namespace SystemGuard.Desktop.Controls;

public partial class AnimatedLogo : UserControl
{
    private readonly DispatcherTimer _timer;
    private double _angle;

    public AnimatedLogo()
    {
        InitializeComponent();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _angle += 0.5;
        if (_angle >= 360) _angle = 0;

        if (OuterRing?.RenderTransform is RotateTransform outerTransform)
            outerTransform.Angle = _angle;

        if (InnerRing?.RenderTransform is RotateTransform innerTransform)
            innerTransform.Angle = -_angle * 1.5;
    }
}