using System;
using System.Diagnostics;
using System.Security.Principal;
using SystemGuard.Core.Interfaces;

namespace SystemGuard.Desktop.Services;

public class ElevationService : IElevationService
{
    public bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    public void RestartAsAdministrator()
    {
        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath)) return;

            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                Verb = "runas"
            };
            Process.Start(psi);
            Environment.Exit(0);
        }
        catch { }
    }
}