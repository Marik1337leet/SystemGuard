using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;

namespace SystemGuard.Desktop.Services;

public class WindowsServiceInfo
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public ServiceStartMode StartMode { get; set; }
    public ServiceControllerStatus Status { get; set; }
    public string StatusText => Status.ToString();
    public bool IsRunning => Status == ServiceControllerStatus.Running;
    public string StartModeText => StartMode.ToString();
    public bool IsSystemService { get; set; }
}

public class WindowsServiceManager
{
    public List<WindowsServiceInfo> GetServices()
    {
        var services = new List<WindowsServiceInfo>();
        try
        {
            foreach (var service in ServiceController.GetServices())
            {
                try
                {
                    services.Add(new WindowsServiceInfo
                    {
                        Name = service.ServiceName,
                        DisplayName = service.DisplayName,
                        Status = service.Status,
                        StartMode = service.StartType,
                        IsSystemService = IsSystemService(service.ServiceName)
                    });
                }
                catch { }
            }
        }
        catch { }
        return services.OrderBy(s => s.DisplayName).ToList();
    }

    public void StartService(string serviceName)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            if (sc.Status != ServiceControllerStatus.Running)
            {
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
            }
        }
        catch { }
    }

    public void StopService(string serviceName)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
            }
        }
        catch { }
    }

    public void RestartService(string serviceName)
    {
        StopService(serviceName);
        System.Threading.Thread.Sleep(1000);
        StartService(serviceName);
    }

    public void SetStartMode(string serviceName, ServiceStartMode mode)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc",
                Arguments = $"config \"{serviceName}\" start= {mode.ToString().ToLower()}",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process.Start(psi)?.WaitForExit(3000);
        }
        catch { }
    }

    private bool IsSystemService(string serviceName)
    {
        var systemServices = new[]
        {
            "wuauserv", "WSearch", "Spooler", "BITS", "SysMain", "FontCache",
            "DiagTrack", "dmwappushservice", "MapsBroker", "lfsvc"
        };
        return systemServices.Contains(serviceName);
    }
}