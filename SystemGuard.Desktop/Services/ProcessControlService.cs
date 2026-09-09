using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SystemGuard.Desktop.Services;

// Реальное управление процессами: 6 приоритетов, affinity, suspend/resume, DLL-инжект (для разработчиков).
public static class ProcessControlService
{
    public static string SetPriority(int pid, ProcessPriorityClass priority)
    {
        try
        {
            if (SelfProtection.IsProtectedProcessId(pid)) return "System-protected process — refused";
            using var p = Process.GetProcessById(pid);
            p.PriorityClass = priority;
            return $"Priority set to {priority}";
        }
        catch (Exception ex) { return $"Priority failed: {ex.Message}"; }
    }

    public static string SetAffinity(int pid, IntPtr mask)
    {
        try
        {
            if (SelfProtection.IsProtectedProcessId(pid)) return "System-protected process — refused";
            using var p = Process.GetProcessById(pid);
            p.ProcessorAffinity = mask;
            return $"Affinity set to 0x{mask.ToInt64():X}";
        }
        catch (Exception ex) { return $"Affinity failed: {ex.Message}"; }
    }

    public static string Suspend(int pid)
    {
        try
        {
            if (SelfProtection.IsProtectedProcessId(pid)) return "System-protected process — refused";
            using var p = Process.GetProcessById(pid);
            NtSuspendProcess(p.Handle);
            return "Suspended";
        }
        catch (Exception ex) { return $"Suspend failed: {ex.Message}"; }
    }

    public static string Resume(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            NtResumeProcess(p.Handle);
            return "Resumed";
        }
        catch (Exception ex) { return $"Resume failed: {ex.Message}"; }
    }

    // DLL-инжект через CreateRemoteThread + LoadLibraryW. Только для разработчиков,
    // требует архитектурного совпадения и прав администратора.
    public static string InjectDll(int pid, string dllPath)
    {
        try
        {
            if (!System.IO.File.Exists(dllPath)) return "DLL not found";
            if (SelfProtection.IsProtectedProcessId(pid)) return "System-protected process — refused";
            using var proc = Process.GetProcessById(pid);
            var hProcess = OpenProcess(0x1F0FFF, false, pid);
            if (hProcess == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var loadAddr = GetProcAddress(GetModuleHandle("kernel32.dll"), "LoadLibraryW");
                if (loadAddr == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                var bytes = System.Text.Encoding.Unicode.GetBytes(dllPath + "\0");
                var alloc = VirtualAllocEx(hProcess, IntPtr.Zero, (uint)bytes.Length, 0x3000, 0x04);
                if (alloc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    if (!WriteProcessMemory(hProcess, alloc, bytes, (uint)bytes.Length, out _))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    var thread = CreateRemoteThread(hProcess, IntPtr.Zero, 0, loadAddr, alloc, 0, IntPtr.Zero);
                    if (thread == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                    CloseHandle(thread);
                    return $"Injected into PID {pid}";
                }
                finally { VirtualFreeEx(hProcess, alloc, 0, 0x8000); }
            }
            finally { CloseHandle(hProcess); }
        }
        catch (Exception ex) { return $"Inject failed: {ex.Message}"; }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr processHandle);
    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr processHandle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr h, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr addr, uint size, uint type, uint prot);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr h, IntPtr addr, int size, uint type);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, uint size, out UIntPtr written);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(IntPtr h, IntPtr attr, uint stack, IntPtr addr, IntPtr param, uint flags, IntPtr tid);
}
