using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SystemGuard.Desktop.Services;
using SystemGuard.Desktop.ViewModels;
using SystemGuard.Desktop.Views;
using System;

namespace SystemGuard.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Применяем сохранённую тему до создания окон — виджеты и док подхватят её сразу
        try { new ColorThemeService().ApplySavedOrDefault(); } catch { }
        // Автобэкап при каждом запуске (храним последние 3, ручные не трогаем)
        try { new BackupService().AutoBackupOnStartup(); } catch { }
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                var vm = new MainWindowViewModel();
                var window = new MainWindow { DataContext = vm };
                vm.SetOwnerWindow(window);
                desktop.MainWindow = window;
                desktop.ShutdownRequested += (_, _) => vm.Shutdown();
            }
            catch (Exception ex)
            {
                // Показываем ошибку вместо пустого окна
                desktop.MainWindow = new Avalonia.Controls.Window
                {
                    Title = "Startup Error",
                    Width = 800,
                    Height = 500,
                    Content = new Avalonia.Controls.ScrollViewer
                    {
                        Content = new Avalonia.Controls.TextBlock
                        {
                            Text = ex.ToString(),
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                            Margin = new Thickness(20),
                            FontFamily = "Consolas"
                        }
                    }
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
