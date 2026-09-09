using System;
using SystemGuard.Desktop.Views;

namespace SystemGuard.Desktop.Services;

// Единая точка управления оверлеями (Dock + виджеты): показать/скрыть,
// восстановить при старте, корректно закрыть при выходе.
public static class OverlayManager
{
    private static DockWindow? _dock;
    private static WidgetsWindow? _widgets;

    public static bool IsDockOpen => _dock != null;
    public static bool IsWidgetsOpen => _widgets != null;

    public static void ShowDock()
    {
        try
        {
            if (_dock != null) { _dock.Activate(); return; }
            _dock = new DockWindow();
            _dock.Closed += (_, __) => _dock = null;
            _dock.Show();
        }
        catch { }
    }

    public static void HideDock()
    {
        try { _dock?.Close(); } catch { }
        _dock = null;
    }

    public static void RefreshDock()
    {
        try { _dock?.Rebuild(); } catch { }
    }

    public static void ShowWidgets()
    {
        try
        {
            if (_widgets != null) { _widgets.Activate(); return; }
            _widgets = new WidgetsWindow();
            _widgets.Closed += (_, __) => _widgets = null;
            _widgets.Show();
        }
        catch { }
    }

    public static void HideWidgets()
    {
        try { _widgets?.Close(); } catch { }
        _widgets = null;
    }

    public static void RefreshWidgets()
    {
        try { _widgets?.Rebuild(); } catch { }
    }

    public static void RestoreAtStartup()
    {
        try
        {
            if (new DockService().LoadSettings().Enabled) ShowDock();
            if (new WidgetsService().Load().Enabled) ShowWidgets();
        }
        catch { }
    }

    public static void Shutdown()
    {
        HideDock();
        HideWidgets();
    }
}
