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

    public static string NormalizeHost(string? site)
    {
        if (string.IsNullOrWhiteSpace(site)) return "";
        var s = site.Trim().ToLowerInvariant();
        foreach (var scheme in new[] { "https://", "http://" })
            if (s.StartsWith(scheme)) { s = s[scheme.Length..]; break; }
        foreach (var sep in new[] { '/', '?', '#' })
        {
            var i = s.IndexOf(sep);
            if (i >= 0) s = s[..i];
        }
        s = s.Trim().TrimEnd('.');
        if (s.Contains(' ') || s.Contains(':') || !s.Contains('.')) return "";
        return s;
    }

    public string Block(string site)
    {
        try
        {
            // Блокируем и голый домен, и www-поддомен: раньше резался только
            // точный ввод, и сайт спокойно открывался через www.example.com.
            var host = NormalizeHost(site);
            if (string.IsNullOrWhiteSpace(host)) return "Enter a valid domain, e.g. example.com";
            var text = File.Exists(_hostsPath) ? File.ReadAllText(_hostsPath) : "";
            var current = ListBlocked();
            var targets = host.StartsWith("www.")
                ? new[] { host }
                : new[] { host, "www." + host };
            bool added = false;
            foreach (var t in targets)
                if (!current.Contains(t)) { current.Add(t); added = true; }
            if (!added) return $"{host} already blocked";
            File.WriteAllText(_hostsPath, RenderBlock(text, current));
            try { System.Diagnostics.Process.Start("ipconfig", "/flushdns"); } catch { }
            return $"{host} blocked";
        }
        catch (Exception ex) { return $"Block failed: {ex.Message} (run as admin)"; }
    }

    public string Unblock(string site)
    {
        try
        {
            var host = NormalizeHost(site);
            if (string.IsNullOrWhiteSpace(host)) host = site.Trim().ToLowerInvariant();
            var buddies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { host, "www." + host.TrimStart() };
            if (host.StartsWith("www.")) buddies.Add(host[4..]);
            var current = ListBlocked();
            current.RemoveAll(s => buddies.Contains(s));
            var text = File.Exists(_hostsPath) ? File.ReadAllText(_hostsPath) : "";
            File.WriteAllText(_hostsPath, RenderBlock(text, current));
            return $"{host} unblocked";
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
