using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using SystemGuard.Desktop.Services;

namespace SystemGuard.Desktop.ViewModels;

public partial class FanRow : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _chip = "";
    [ObservableProperty] private string _rpmText = "—";
}

public partial class FansViewModel : ViewModelBase
{
    private readonly SettingsService _settings = new();

    [ObservableProperty] private ObservableCollection<FanRow> _fans = new();
    [ObservableProperty] private ObservableCollection<string> _modes = new() { "Auto", "Silent", "Balanced", "Performance", "Manual" };
    [ObservableProperty] private string _selectedMode = "Auto";
    [ObservableProperty] private int _manualPercent = 50;
    [ObservableProperty] private string _status = "Читаем обороты с чипа платы…";

    public IRelayCommand RefreshCommand { get; }
    public IRelayCommand ApplyCommand { get; }

    public FansViewModel()
    {
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync());
        ApplyCommand = new RelayCommand(() => _ = ApplyAsync());
        try
        {
            var s = _settings.Load();
            if (Modes.Contains(s.FanMode)) SelectedMode = s.FanMode;
            ManualPercent = Math.Clamp(s.FanManualPercent, 0, 100);
        }
        catch { }
    }

    public override void OnActivated()
    {
        base.OnActivated();
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        Status = "Опрос вентиляторов…";
        var list = await Task.Run(() => FanControlService.GetFans()).ConfigureAwait(false);
        var text = await Task.Run(() => FanControlService.StatusText()).ConfigureAwait(false);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Fans.Clear();
            foreach (var f in list)
                Fans.Add(new FanRow
                {
                    Name = f.Name,
                    Chip = f.Chip,
                    RpmText = f.Rpm.HasValue ? $"{f.Rpm:F0} RPM" + (f.ControlPercent.HasValue ? $" · {f.ControlPercent:F0}%" : "") : "—"
                });
            Status = list.Count == 0 ? text : $"{text.Split('\n').FirstOrDefault()} · обновлено {DateTime.Now:HH:mm:ss}";
        });
    }

    private async Task ApplyAsync()
    {
        var mode = SelectedMode;
        var pct = ManualPercent;
        Status = "Применяю: " + mode + "…";
        string r = await Task.Run(() => FanControlService.ApplyMode(mode, pct)).ConfigureAwait(false);
        try
        {
            var s = _settings.Load();
            s.FanMode = mode;
            s.FanManualPercent = pct;
            _settings.Save(s);
        }
        catch { }
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Status = r);
        await RefreshAsync().ConfigureAwait(false);
    }
}
