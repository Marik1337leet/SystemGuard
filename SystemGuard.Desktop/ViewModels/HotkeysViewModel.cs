using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using SystemGuard.Desktop.Models;
using SystemGuard.Desktop.Services;

namespace SystemGuard.Desktop.ViewModels;

public partial class HotkeyItem : ObservableObject
{
    [ObservableProperty] private string _actionId = "";
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _combo = "";
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private string _hint = "";
}

public partial class HotkeysViewModel : ViewModelBase
{
    private readonly SettingsService _settings = new();

    [ObservableProperty] private ObservableCollection<HotkeyItem> _items = new();
    [ObservableProperty] private string _status = "Добавьте свои хоткеи ниже — стандартных нет.";
    [ObservableProperty] private bool _hookRunning;
    [ObservableProperty] private string _newActionId = "";
    [ObservableProperty] private string _newCombo = "";

    // Палитра для picker'а: действия, ещё не забинденные.
    public IEnumerable<string> AvailableActionTitles => HotkeyCatalog.Actions
        .Where(a => !Items.Any(i => i.ActionId == a.Id))
        .Select(a => a.Title)
        .ToList();

    public bool HasItems => Items.Count > 0;

    public IRelayCommand SaveCommand { get; }
    public IRelayCommand ResetCommand { get; }
    public IRelayCommand<string> RunActionCommand { get; }
    public IRelayCommand AddHotkeyCommand { get; }
    public IRelayCommand<string> RemoveHotkeyCommand { get; }

    public HotkeysViewModel()
    {
        SaveCommand = new RelayCommand(Save);
        ResetCommand = new RelayCommand(Reset);
        RunActionCommand = new AsyncRelayCommand<string>(RunActionAsync);
        AddHotkeyCommand = new RelayCommand(AddHotkey);
        RemoveHotkeyCommand = new RelayCommand<string>(RemoveHotkey);
        Items.CollectionChanged += (_, __) =>
        {
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(AvailableActionTitles));
        };
        Load();
        RefreshHookState();
        GlobalHotkeyService.Instance.OnHotkey += _ => RefreshHookState();
    }

    private void Load()
    {
        Items.Clear();
        // Только сохранённые пользователем. Пусто = пусто, дефолтов больше нет.
        var saved = _settings.Load().Hotkeys ?? new List<HotkeyBinding>();
        foreach (var b in saved)
        {
            var def = HotkeyCatalog.Actions.FirstOrDefault(a => a.Id == b.ActionId);
            if (def == null) continue;
            Items.Add(new HotkeyItem
            {
                ActionId = def.Id,
                Title = def.Title,
                Combo = HotkeyCatalog.Normalize(b.Combo),
                Enabled = b.Enabled,
                Hint = def.Hint
            });
        }
        NewActionId = AvailableActionTitles.FirstOrDefault() ?? "";
        NewCombo = "";
        OnPropertyChanged(nameof(AvailableActionTitles));
    }

    private void Save()
    {
        var settings = _settings.Load();
        settings.Hotkeys = Items.Select(i => new HotkeyBinding
        {
            ActionId = i.ActionId,
            Combo = HotkeyCatalog.Normalize(i.Combo),
            Enabled = i.Enabled
        }).ToList();
        _settings.Save(settings);
        GlobalHotkeyService.Instance.Rebuild(settings.Hotkeys);
        Status = "Хоткеи сохранены и применены глобально.";
        RefreshHookState();
        ClearStatusAfterDelay();
    }

    private void Reset()
    {
        var settings = _settings.Load();
        settings.Hotkeys = new List<HotkeyBinding>();
        _settings.Save(settings);
        GlobalHotkeyService.Instance.Rebuild(settings.Hotkeys);
        Load();
        Status = "Хоткеи очищены — добавьте свои.";
        RefreshHookState();
        ClearStatusAfterDelay();
    }

    private void AddHotkey()
    {
        var def = HotkeyCatalog.Actions.FirstOrDefault(a => a.Title == NewActionId)
            ?? HotkeyCatalog.Actions.FirstOrDefault(a => a.Id == NewActionId);
        if (def == null) { Status = "Выберите действие."; ClearStatusAfterDelay(); return; }
        if (Items.Any(i => i.ActionId == def.Id)) { Status = "Это действие уже забиндено."; ClearStatusAfterDelay(); return; }
        var combo = HotkeyCatalog.Normalize(NewCombo);
        if (string.IsNullOrWhiteSpace(combo)) { Status = "Введите комбо, например Ctrl+Alt+O."; ClearStatusAfterDelay(); return; }
        if (Items.Any(i => string.Equals(i.Combo, combo, StringComparison.OrdinalIgnoreCase)))
        { Status = $"Комбо {combo} уже занято."; ClearStatusAfterDelay(); return; }
        Items.Add(new HotkeyItem
        {
            ActionId = def.Id, Title = def.Title, Combo = combo, Enabled = true, Hint = def.Hint
        });
        NewActionId = AvailableActionTitles.FirstOrDefault() ?? "";
        NewCombo = "";
        OnPropertyChanged(nameof(AvailableActionTitles));
        Save();
    }

    private void RemoveHotkey(string? actionId)
    {
        if (string.IsNullOrWhiteSpace(actionId)) return;
        var item = Items.FirstOrDefault(i => i.ActionId == actionId);
        if (item == null) return;
        Items.Remove(item);
        OnPropertyChanged(nameof(AvailableActionTitles));
        Save();
    }

    private async Task RunActionAsync(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        Status = "Выполняю: " + HotkeyCatalog.TitleOf(id) + "…";
        string r = await HotkeyActionRunner.RunAsync(id).ConfigureAwait(false);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Status = r;
            ClearStatusAfterDelay();
        });
    }

    private void RefreshHookState()
    {
        try { HookRunning = GlobalHotkeyService.Instance.IsRunning; } catch { }
    }

    private async void ClearStatusAfterDelay()
    {
        await Task.Delay(3000);
        Status = Items.Count == 0
            ? "Добавьте свои хоткеи ниже — стандартных нет."
            : "Глобальные хоткеи работают даже когда окно свёрнуто.";
    }

    public override void OnActivated()
    {
        base.OnActivated();
        Load();
        RefreshHookState();
    }
}
