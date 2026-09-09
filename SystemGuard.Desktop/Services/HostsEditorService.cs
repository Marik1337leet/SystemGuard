using System;
using System.IO;

namespace SystemGuard.Desktop.Services;

// Редактор hosts с автобэкапом. Реально читает/пишет %SystemRoot%\System32\drivers\etc\hosts.
public class HostsEditorService
{
    public static string HostsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers/etc/hosts");

    public static string Read()
    {
        try { return File.ReadAllText(HostsPath); }
        catch (Exception ex) { return $"# Cannot read hosts: {ex.Message}"; }
    }

    public static string Save(string content)
    {
        try
        {
            var backup = HostsPath + $".sg_backup_{DateTime.Now:yyyyMMdd_HHmmss}";
            try { File.Copy(HostsPath, backup, false); } catch { }
            File.WriteAllText(HostsPath, content);
            try { System.Diagnostics.Process.Start("ipconfig", "/flushdns"); } catch { }
            return "hosts saved (backup created, DNS flushed)";
        }
        catch (Exception ex) { return $"hosts save failed: {ex.Message} (run as admin)"; }
    }

    public static string AddAdblockRules(string content)
    {
        var rules = new[]
        {
            "0.0.0.0 doubleclick.net",
            "0.0.0.0 googlesyndication.com",
            "0.0.0.0 googleadservices.com",
            "0.0.0.0 facebook.net",
        };
        foreach (var r in rules)
            if (!content.Contains(r)) content += Environment.NewLine + r;
        return content;
    }
}
