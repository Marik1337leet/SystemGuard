using Avalonia;
using System;

namespace SystemGuard.Desktop;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Самотест без GUI: SystemGuard.exe --selftest (и dotnet exec).
        // Нужен, т.к. внешний xUnit-раннер душит Smart App Control.
        foreach (var a in args ?? Array.Empty<string>())
        {
            if (a.Equals("--selftest", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("selftest", StringComparison.OrdinalIgnoreCase))
            {
                int code;
                try { code = Services.SelfTestService.RunAsync().GetAwaiter().GetResult(); }
                catch (Exception ex)
                {
                    try { Console.WriteLine("[FAIL] selftest crashed :: " + ex.Message); } catch { }
                    code = 2;
                }
                Environment.Exit(code);
                return;
            }
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}