using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace SystemGuard.Desktop.Services;

// Общий помощник освобождения RAM (EmptyWorkingSet по всем доступным
// процессам + GC). Используют GameMode Boost и Performance "Free Memory".
// Безопасен: системные и собственные процессы отфильтрованы SelfProtection,
// данные не трогает — только выгружает простаивающие страницы в standby.
public static class MemoryTrimmer
{
    [System.Runtime.InteropServices.DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    public static async Task<int> TrimAllAsync()
    {
        return await Task.Run(() =>
        {
            int trimmed = 0;
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (proc.Id == Environment.ProcessId) continue;
                    if (SelfProtection.IsProtectedProcess(proc.ProcessName)) continue;
                    if (EmptyWorkingSet(proc.Handle)) trimmed++;
                }
                catch { }
                finally { try { proc.Dispose(); } catch { } }
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return trimmed;
        });
    }
}
