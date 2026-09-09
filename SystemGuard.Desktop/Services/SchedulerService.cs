using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace SystemGuard.Desktop.Services;

public class ScheduledTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString()[..8];
    public string Name { get; set; } = "";
    public string Type { get; set; } = "Shutdown";
    public DateTime ExecuteAt { get; set; } = DateTime.Now.AddHours(1);
    public bool IsEnabled { get; set; } = true;
    public bool IsRecurring { get; set; }
    public string Recurrence { get; set; } = "Daily";
    public int Interval { get; set; } = 1;
    public string CustomCommand { get; set; } = "";
    public DateTime? LastExecuted { get; set; }
    // Условие: выполнять только если загрузка CPU выше порога (0 = без условия)
    public double CpuAboveThreshold { get; set; }
    public bool IsExpired => DateTime.Now > ExecuteAt && !IsRecurring;
}

public class SchedulerService
{
    private readonly string _tasksPath;
    private List<ScheduledTask> _tasks = new();
    private System.Timers.Timer? _checkTimer;

    public event Action<ScheduledTask>? OnTaskExecuted;

    public SchedulerService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var folder = Path.Combine(appData, "SystemGuard");
        Directory.CreateDirectory(folder);
        _tasksPath = Path.Combine(folder, "scheduled_tasks.json");
        LoadTasks();
        StartChecking();
    }

    private void StartChecking()
    {
        _checkTimer = new System.Timers.Timer(15000);
        _checkTimer.Elapsed += (_, _) => CheckTasks();
        _checkTimer.Start();
    }

    private void CheckTasks()
    {
        var now = DateTime.Now;
        foreach (var task in _tasks.ToArray())
        {
            if (!task.IsEnabled) continue;
            if (task.IsExpired && !task.IsRecurring) { _tasks.Remove(task); continue; }
            if (task.ExecuteAt > now) continue;
            // Условие CPU: если задано и нагрузка ниже — пропускаем цикл
            if (task.CpuAboveThreshold > 0 && ReadCpuLoad() < task.CpuAboveThreshold)
                continue;
            ExecuteTask(task);
        }
        SaveTasks();
    }

    private static System.Diagnostics.PerformanceCounter? _cpuCounter;
    private static double ReadCpuLoad()
    {
        try
        {
            _cpuCounter ??= new System.Diagnostics.PerformanceCounter("Processor", "% Processor Time", "_Total");
            _ = _cpuCounter.NextValue();
            System.Threading.Thread.Sleep(200);
            return Math.Round(_cpuCounter.NextValue(), 1);
        }
        catch { return 0; }
    }

    private async void ExecuteTask(ScheduledTask task)
    {
        task.LastExecuted = DateTime.Now;
        await Task.Run(() =>
        {
            switch (task.Type)
            {
                case "Shutdown": Process.Start("shutdown", "/s /t 60"); break;
                case "Restart": Process.Start("shutdown", "/r /t 60"); break;
                case "Sleep": Process.Start("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0"); break;
                case "Cleanup": CleanTempFiles(); break;
                case "Notify": break; // напоминание: срабатывает через OnTaskExecuted → StatusText/Telegram
                case "Custom":
                    if (!string.IsNullOrEmpty(task.CustomCommand))
                        Process.Start("cmd.exe", $"/c {task.CustomCommand}");
                    break;
            }
        });
        if (task.IsRecurring) task.ExecuteAt = GetNextExecution(task);
        OnTaskExecuted?.Invoke(task);
        SaveTasks();
    }

    private void CleanTempFiles()
    {
        try
        {
            foreach (var file in Directory.GetFiles(Path.GetTempPath()))
            {
                try
                {
                    // Свежие файлы (< 2 мин) не трогаем — там могут быть
                    // in-flight скриншоты/стримы самого SystemGuard
                    if (DateTime.Now - File.GetCreationTime(file) < TimeSpan.FromMinutes(2)) continue;
                    if (SelfProtection.IsProtectedPath(file)) continue;
                    File.Delete(file);
                }
                catch { }
            }
        }
        catch { }
    }

    private DateTime GetNextExecution(ScheduledTask task) => task.Recurrence switch
    {
        "Daily" => task.ExecuteAt.AddDays(task.Interval),
        "Weekly" => task.ExecuteAt.AddDays(7 * task.Interval),
        "Monthly" => task.ExecuteAt.AddMonths(task.Interval),
        _ => task.ExecuteAt.AddDays(1)
    };

    public List<ScheduledTask> GetTasks() => _tasks;

    public void AddTask(ScheduledTask task) { _tasks.Add(task); SaveTasks(); }
    public void RemoveTask(string id) { _tasks.RemoveAll(t => t.Id == id); SaveTasks(); }

    // Макрос = цепочка действий: выполняем задачи по id строго по порядку.
    // Возвращает отчёт по каждому шагу (для показа в UI / отправки в Telegram/email).
    public async Task<string> RunMacroAsync(IReadOnlyList<string> taskIds)
    {
        var lines = new List<string> { $"Macro started {DateTime.Now:G} ({taskIds.Count} steps)" };
        foreach (var id in taskIds)
        {
            var task = _tasks.Find(t => t.Id == id);
            if (task == null) { lines.Add($"- {id}: not found, skipped"); continue; }
            try
            {
                ExecuteTask(task);
                lines.Add($"- {task.Name} [{task.Type}]: OK");
            }
            catch (Exception ex) { lines.Add($"- {task.Name}: FAILED {ex.Message}"); }
            await Task.Delay(500);
        }
        lines.Add($"Macro finished {DateTime.Now:G}");
        return string.Join("\n", lines);
    }

    public async Task<string> RunMacroAndReportAsync(IReadOnlyList<string> taskIds, Func<string, Task<string>>? reportTo = null)
    {
        var report = await RunMacroAsync(taskIds);
        if (reportTo != null)
        {
            try { await reportTo(report); } catch { }
        }
        return report;
    }

    // Запуск задачи прямо сейчас (без ожидания расписания)
    public void RunNow(string id)
    {
        var task = _tasks.Find(t => t.Id == id);
        if (task != null) ExecuteTask(task);
    }
    public void ToggleTask(string id)
    {
        var task = _tasks.Find(t => t.Id == id);
        if (task != null) { task.IsEnabled = !task.IsEnabled; SaveTasks(); }
    }

    private void LoadTasks()
    {
        try { if (File.Exists(_tasksPath)) _tasks = JsonSerializer.Deserialize<List<ScheduledTask>>(File.ReadAllText(_tasksPath)) ?? new(); }
        catch { _tasks = new(); }
    }

    private void SaveTasks()
    {
        try { File.WriteAllText(_tasksPath, JsonSerializer.Serialize(_tasks, new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }
}