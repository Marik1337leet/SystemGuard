using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using SystemGuard.Desktop.Services;

namespace SystemGuard.Desktop.ViewModels;

public partial class SchedulerViewModel : ViewModelBase
{
    private readonly SchedulerService _scheduler = new();

    [ObservableProperty] private ObservableCollection<ScheduledTask> _tasks = new();
    [ObservableProperty] private string _statusText = "Ready";

    // Форма новой задачи
    [ObservableProperty] private string _newTaskName = "";
    [ObservableProperty] private string _newTaskType = "Shutdown";
    [ObservableProperty] private string _newTaskCustomCommand = "";
    [ObservableProperty] private bool _newTaskRecurring;
    [ObservableProperty] private string _newTaskRecurrence = "Daily";
    [ObservableProperty] private double _newTaskCpuThreshold;

    // Отчёт в Slack webhook
    [ObservableProperty] private string _slackWebhook = "";

    // DatePicker и TimePicker разделены
    [ObservableProperty] private DateTimeOffset? _newTaskDate = DateTimeOffset.Now.AddDays(1);
    [ObservableProperty] private TimeSpan? _newTaskTime = new TimeSpan(12, 0, 0);

    public string[] TaskTypes => new[] { "Shutdown", "Restart", "Sleep", "Cleanup", "Notify", "Custom" };
    public string[] RecurrenceTypes => new[] { "Daily", "Weekly", "Monthly" };

    public bool IsCustomTaskType => NewTaskType == "Custom";

    public int TaskCount => Tasks.Count;
    public int ActiveTaskCount => Tasks.Count(t => t.IsEnabled);

    public IRelayCommand AddTaskCommand { get; }
    public IRelayCommand<string> RemoveTaskCommand { get; }
    public IRelayCommand<string> ToggleTaskCommand { get; }
    public IRelayCommand<string> RunNowCommand { get; }
    public IRelayCommand RefreshCommand { get; }
    public IRelayCommand SendReportCommand { get; }

    public SchedulerViewModel()
    {
        AddTaskCommand = new RelayCommand(AddTask);
        RemoveTaskCommand = new RelayCommand<string>(RemoveTask);
        ToggleTaskCommand = new RelayCommand<string>(ToggleTask);
        RunNowCommand = new RelayCommand<string>(RunNow);
        RefreshCommand = new RelayCommand(LoadTasks);
        SendReportCommand = new AsyncRelayCommand(SendReportAsync);

        _scheduler.OnTaskExecuted += task =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                StatusText = $"Executed: {task.Name}";
                LoadTasks();
            });

        LoadTasks();
    }

    partial void OnNewTaskTypeChanged(string value) =>
        OnPropertyChanged(nameof(IsCustomTaskType));

    private void LoadTasks()
    {
        Tasks = new ObservableCollection<ScheduledTask>(
            _scheduler.GetTasks().OrderBy(t => t.ExecuteAt));
        OnPropertyChanged(nameof(TaskCount));
        OnPropertyChanged(nameof(ActiveTaskCount));
    }

    private void AddTask()
    {
        if (string.IsNullOrWhiteSpace(NewTaskName)) return;

        // Собираем DateTime из DatePicker + TimePicker
        var date = NewTaskDate?.Date ?? DateTime.Today.AddDays(1);
        var time = NewTaskTime ?? new TimeSpan(12, 0, 0);
        var executeAt = date + time;

        if (executeAt <= DateTime.Now)
        {
            StatusText = "Schedule time must be in the future";
            return;
        }

        var task = new ScheduledTask
        {
            Name = NewTaskName.Trim(),
            Type = NewTaskType,
            ExecuteAt = executeAt,
            IsRecurring = NewTaskRecurring,
            Recurrence = NewTaskRecurrence,
            CustomCommand = NewTaskType == "Custom" ? NewTaskCustomCommand.Trim() : "",
            CpuAboveThreshold = NewTaskCpuThreshold
        };

        _scheduler.AddTask(task);

        // Сброс формы
        NewTaskName = "";
        NewTaskCustomCommand = "";
        NewTaskDate = DateTimeOffset.Now.AddDays(1);
        NewTaskTime = new TimeSpan(12, 0, 0);

        LoadTasks();
        StatusText = $"Added: {task.Name}";
    }

    private void RemoveTask(string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        _scheduler.RemoveTask(id);
        LoadTasks();
        StatusText = "Task removed";
    }

    private void ToggleTask(string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        _scheduler.ToggleTask(id);
        LoadTasks();
    }

    private void RunNow(string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        _scheduler.RunNow(id);
        LoadTasks();
        StatusText = "Task started";
    }

    private async Task SendReportAsync()
    {
        if (string.IsNullOrWhiteSpace(SlackWebhook)) { StatusText = "Enter Slack webhook URL"; return; }
        var lines = Tasks.Select(t => $"• {t.Name} [{t.Type}] {(t.IsEnabled ? "on" : "off")} {t.ExecuteAt:dd MMM HH:mm}");
        StatusText = await SlackNotifierService.SendAsync(SlackWebhook.Trim(),
            $"SystemGuard tasks ({TaskCount}):\n" + string.Join("\n", lines));
    }
}