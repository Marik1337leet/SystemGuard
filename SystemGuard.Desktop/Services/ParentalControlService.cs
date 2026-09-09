using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SystemGuard.Desktop.Services;

// Родительский контроль: блокировка сайтов через hosts с маркерами SG_BLOCK.
// hostsPath инжектится для тестов; по умолчанию реальный hosts.
public class ParentalControlService
{
    private const string Begin = "# BEGIN SystemGuard parental block";
    private const string End = "# END SystemGuard parental block";
    private readonly string _hostsPath;

    public ParentalControlService(string? hostsPath = null)
    {
        _hostsPath = hostsPath ?? HostsEditorService.HostsPath;
    }

    public List<string> ListBlocked()
    {
        try
        {
            if (!File.Exists(_hostsPath)) return new List<string>();
            var text = File.ReadAllText(_hostsPath);
            var i0 = text.IndexOf(Begin, StringComparison.Ordinal);
            var i1 = text.IndexOf(End, StringComparison.Ordinal);
            if (i0 < 0 || i1 < 0 || i1 <= i0) return new List<string>();
            var block = text.Substring(i0, i1 - i0);
            return block.Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.StartsWith("0.0.0.0 "))
                .Select(l => l["0.0.0.0 ".Length..].Trim())
                .Where(s => s.Length > 0).ToList();
        }
        catch { return new List<string>(); }
    }

    public string Block(string site)
    {
        try
        {
            site = site.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(site)) return "Empty site";
            var text = File.Exists(_hostsPath) ? File.ReadAllText(_hostsPath) : "";
            var current = ListBlocked();
            if (current.Contains(site)) return $"{site} already blocked";
            current.Add(site);
            File.WriteAllText(_hostsPath, RenderBlock(text, current));
            try { System.Diagnostics.Process.Start("ipconfig", "/flushdns"); } catch { }
            return $"{site} blocked";
        }
        catch (Exception ex) { return $"Block failed: {ex.Message} (run as admin)"; }
    }

    public string Unblock(string site)
    {
        try
        {
            var current = ListBlocked();
            current.RemoveAll(s => s.Equals(site.Trim().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase));
            var text = File.Exists(_hostsPath) ? File.ReadAllText(_hostsPath) : "";
            File.WriteAllText(_hostsPath, RenderBlock(text, current));
            return $"{site} unblocked";
        }
        catch (Exception ex) { return $"Unblock failed: {ex.Message}"; }
    }

    internal static string RenderBlock(string hostsText, List<string> sites)
    {
        var i0 = hostsText.IndexOf(Begin, StringComparison.Ordinal);
        var i1 = hostsText.IndexOf(End, StringComparison.Ordinal);
        if (i0 >= 0 && i1 > i0)
            hostsText = hostsText.Remove(i0, i1 + End.Length - i0);
        if (sites.Count == 0) return hostsText.TrimEnd() + Environment.NewLine;
        var block = Begin + Environment.NewLine +
            string.Join(Environment.NewLine, sites.Select(s => $"0.0.0.0 {s}")) +
            Environment.NewLine + End + Environment.NewLine;
        return hostsText.TrimEnd() + Environment.NewLine + block;
    }
}
