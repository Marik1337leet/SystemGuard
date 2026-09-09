using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Security.Cryptography;
using System.IO;
using SystemGuard.Desktop.Models;

namespace SystemGuard.Desktop.Services;

public class AdvancedProcessService
{
    public List<ProcessModel> GetProcessTree()
    {
        var processes = new List<ProcessModel>();
        var processMap = new Dictionary<int, ProcessModel>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, Name, ExecutablePath, CommandLine, ThreadCount, HandleCount, " +
                "CreationDate, ParentProcessId FROM Win32_Process");

            foreach (ManagementObject obj in searcher.Get().Cast<ManagementObject>())
            {
                try
                {
                    var pid = Convert.ToInt32(obj["ProcessId"]);
                    var model = new ProcessModel
                    {
                        Pid = pid,
                        Name = obj["Name"]?.ToString() ?? "Unknown",
                        FullPath = obj["ExecutablePath"]?.ToString() ?? "",
                        CommandLine = obj["CommandLine"]?.ToString() ?? "",
                        ThreadCount = Convert.ToInt32(obj["ThreadCount"]),
                        HandleCount = Convert.ToInt32(obj["HandleCount"]),
                        ParentPid = Convert.ToInt32(obj["ParentProcessId"])
                    };

                    try
                    {
                        var proc = Process.GetProcessById(pid);
                        model.MemoryMB = Math.Round(proc.WorkingSet64 / 1048576.0, 2);
                        model.Priority = proc.PriorityClass.ToString();
                        model.IsResponding = proc.Responding;
                        try { model.StartTime = proc.StartTime; } catch { }
                    }
                    catch { }

                    if (!string.IsNullOrEmpty(model.FullPath))
                    {
                        try
                        {
                            var vi = FileVersionInfo.GetVersionInfo(model.FullPath);
                            model.Company = vi.CompanyName ?? "";
                            model.Description = vi.FileDescription ?? "";
                        }
                        catch { }
                    }

                    processMap[pid] = model;
                    processes.Add(model);
                }
                catch { }
            }
        }
        catch { }

        foreach (var proc in processes)
        {
            if (processMap.TryGetValue(proc.ParentPid, out var parent))
            {
                parent.Children.Add(proc);
                proc.IndentLevel = parent.IndentLevel + 1;
            }
        }

        return processes.OrderBy(p => p.IndentLevel).ThenBy(p => p.Name).ToList();
    }

    public void KillProcessForce(int pid)
    {
        if (SelfProtection.IsProtectedProcessId(pid)) return;
        try { Process.GetProcessById(pid).Kill(); } catch { }
        try { Process.Start("taskkill", $"/F /T /PID {pid}")?.WaitForExit(5000); } catch { }
    }

    public void CreateDump(int pid, string outputPath)
    {
        try
        {
            Process.Start("procdump", $"-accepteula {pid} \"{outputPath}\"");
        }
        catch { }
    }

    public string CalculateMD5(string filePath)
    {
        try
        {
            using var md5 = MD5.Create();
            using var stream = File.OpenRead(filePath);
            var hash = md5.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
        catch { return ""; }
    }

    public string CalculateSHA256(string filePath)
    {
        try
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }
        catch { return ""; }
    }

    public static string GetCompanyName(string filePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return "";
            var info = FileVersionInfo.GetVersionInfo(filePath);
            return info.CompanyName ?? "";
        }
        catch { return ""; }
    }

    public static string OpenContainingFolder(string filePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return "File not found";
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true });
            return "Opened";
        }
        catch (Exception ex) { return ex.Message; }
    }

    public static string OpenStartupRegistryKey()
    {
        try
        {
            Process.Start(new ProcessStartInfo("regedit.exe") { UseShellExecute = true });
            return "Registry editor opened (HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run)";
        }
        catch (Exception ex) { return ex.Message; }
    }
}