using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SystemGuard.Desktop.ViewModels;

namespace SystemGuard.Desktop.Views;

public partial class ColorPickerView : UserControl
{
    private bool _isDragging;

    public ColorPickerView()
    {
        InitializeComponent();
    }

    // ── SV Square interaction ─────────────────────────────────────────────────

    private void SvSquare_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border square) return;
        _isDragging = true;
        e.Pointer.Capture(square);
        HandleSvClick(square, e.GetPosition(square));
    }

    private void SvSquare_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging || sender is not Border square) return;
        if (!e.GetCurrentPoint(square).Properties.IsLeftButtonPressed)
        {
            _isDragging = false;
            return;
        }
        HandleSvClick(square, e.GetPosition(square));
    }

    private void HandleSvClick(Border square, Avalonia.Point pos)
    {
        var bounds = square.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var rx = pos.X / bounds.Width;
        var ry = pos.Y / bounds.Height;

        if (DataContext is ColorPickerViewModel vm)
        {
            vm.OnSvSquareClick(rx, ry);
            UpdateCursorPosition(square, rx, ry);
        }
    }

    private void UpdateCursorPosition(Border square, double rx, double ry)
    {
        if (SvCursor == null) return;

        var bounds = square.Bounds;
        var x = rx * bounds.Width - SvCursor.Width / 2;
        var y = ry * bounds.Height - SvCursor.Height / 2;

        Canvas.SetLeft(SvCursor, x);
        Canvas.SetTop(SvCursor, y);
    }

    // ── HEX input ─────────────────────────────────────────────────────────────

    private void HexInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            ApplyHexFromInput();
    }

    private void HexSet_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ApplyHexFromInput();

    private void ApplyHexFromInput()
    {
        if (HexInput == null || DataContext is not ColorPickerViewModel vm) return;
        vm.SetHexCommand.Execute(HexInput.Text);
    }
}
