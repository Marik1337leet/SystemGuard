using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace SystemGuard.Desktop.Services;

public class PortScanResult
{
    public int Port { get; set; }
    public bool IsOpen { get; set; }
}

// Реальный сканер открытых портов TCP.
public static class PortScannerService
{
    public static async Task<List<PortScanResult>> ScanAsync(string host, int fromPort, int toPort, int timeoutMs = 800)
    {
        fromPort = Math.Clamp(fromPort, 1, 65535);
        toPort = Math.Clamp(toPort, 1, 65535);
        if (fromPort > toPort) (fromPort, toPort) = (toPort, fromPort);
        if (toPort - fromPort > 200) toPort = fromPort + 200; // защита от зависших сканов

        var tasks = Enumerable.Range(fromPort, toPort - fromPort + 1)
            .Select(async port => new PortScanResult { Port = port, IsOpen = await IsOpenAsync(host, port, timeoutMs) });
        var results = await Task.WhenAll(tasks);
        return results.Where(r => r.IsOpen).OrderBy(r => r.Port).ToList();
    }

    private static async Task<bool> IsOpenAsync(string host, int port, int timeoutMs)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(host, port);
            var winner = await Task.WhenAny(task, Task.Delay(timeoutMs));
            return winner == task && client.Connected;
        }
        catch { return false; }
    }
}
