using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace SystemGuard.Desktop.Services;

// Буфер обмена главного окна без проброса OwnerWindow по всем VM.
public static class AppClipboard
{
    public static TopLevel? TopLevel
    {
        get
        {
            try
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime life
                    && life.MainWindow != null)
                    return TopLevel.GetTopLevel(life.MainWindow);
            }
            catch { }
            return null;
        }
    }
}
