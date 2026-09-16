using System;
using Avalonia.Threading;
using SystemGuard.Desktop.Views;

namespace SystemGuard.Desktop.Services;

// Единая точка управления оверлеями (Dock + виджеты): показать/скрыть,
// восстановить при старте, корректно закрыть при выходе.
//
// ВАЖНО: окна Avalonia можно создавать/показывать ТОЛЬКО на UI-потоке.
// Вызовы прилетают и с фона (глобальный хоткей, RemoteActions, бот) —
// раньше это молча падало в пустом catch и «виджет не запускался».
// Теперь всё маршалится на Dispatcher.UIThread.
public static class OverlayManager
{
    private static DockWindow? _dock;
    private static WidgetsWindow? _widgets;

    public static bool IsDockOpen => _dock != null;
    public static bool IsWidgetsOpen => _widgets != null;

    private static void OnUi(Action action)
    {
        try
        {
            if (Dispatcher.UIThread.CheckAccess()) action();
            else Dispatcher.UIThread.Post(action);
        }
        catch { }
    }

    public static void ShowDock()
    {
        OnUi(() =>
        {
            try
            {
                if (_dock != null) { _dock.Activate(); return; }
                _dock = new DockWindow();
                _dock.Closed += (_, __) => _dock = null;
                _dock.Show();
            }
            catch { }
        });
    }

    public static void HideDock()
    {
        OnUi(() =>
        {
            try { _dock?.Close(); } catch { }
            _dock = null;
        });
    }

    public static void RefreshDock()
    {
        OnUi(() =>
        {
            try { _dock?.Rebuild(); } catch { }
        });
    }

    public static void ShowWidgets()
    {
        OnUi(() =>
        {
            try
            {
                if (_widgets != null) { _widgets.Activate(); return; }
                _widgets = new WidgetsWindow();
                _widgets.Closed += (_, __) => _widgets = null;
                _widgets.Show();
            }
            catch { }
        });
    }

    public static void HideWidgets()
    {
        OnUi(() =>
        {
            try { _widgets?.Close(); } catch { }
            _widgets = null;
        });
    }

    public static void RefreshWidgets()
    {
        OnUi(() =>
        {
            try { _widgets?.Rebuild(); } catch { }
        });
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
