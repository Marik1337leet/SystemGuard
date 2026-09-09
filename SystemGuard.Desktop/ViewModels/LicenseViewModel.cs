using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using SystemGuard.Desktop.Services;

namespace SystemGuard.Desktop.ViewModels;

// Строка таблицы фич — вынесена из LicenseView.axaml
public class FeatureRow
{
    public string Name { get; set; } = "";
    public string FreeText { get; set; } = "No";
    public string ProText { get; set; } = "Yes";
    public string EntText { get; set; } = "Yes";
    public string FreeColor { get; set; } = "#71798A";
    public string ProColor { get; set; } = "#C96C9E";
    public string EntColor { get; set; } = "#D48CB5";
}

public partial class LicenseViewModel : ViewModelBase
{
    private readonly LicenseService _licenseService = new();

    [ObservableProperty] private string _activationKey = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _statusColor = "#71798A";
    [ObservableProperty] private string _activationMessage = "";
    [ObservableProperty] private string _activationMessageColor = "#C96C9E";
    [ObservableProperty] private bool _isActivating;
    [ObservableProperty] private int _daysLeft;
    [ObservableProperty] private string _tierBadge = "FREE";
    [ObservableProperty] private bool _isLicenseActive;
    [ObservableProperty] private bool _isExpired;
    [ObservableProperty] private bool _canActivateTrial;

    public string MachineId => _licenseService.MachineId; // HWID этого ПК — для привязки ключа
    public string PlanText => _licenseService.CurrentLicense is { IsValid: true } l && !string.IsNullOrEmpty(l.Plan)
        ? l.Plan : "—";

    public ObservableCollection<FeatureRow> FeatureRows { get; } = new()
    {
        new() { Name = "Dashboard & Monitoring",  FreeText = "Yes", FreeColor = "#C96C9E" },
        new() { Name = "Process Manager",          FreeText = "Yes", FreeColor = "#C96C9E" },
        new() { Name = "Basic Cleanup",            FreeText = "Yes", FreeColor = "#C96C9E" },
        new() { Name = "Power Controls",           FreeText = "Yes", FreeColor = "#C96C9E" },
        new() { Name = "Advanced Disk Cleanup" },
        new() { Name = "Game Mode" },
        new() { Name = "Telegram Bot Control" },
        new() { Name = "Task Scheduler" },
        new() { Name = "Benchmarks" },
        new() { Name = "Network Monitor" },
        new() { Name = "Priority Support",
                EntText = "Yes", ProText = "No", ProColor = "#71798A" },
        new() { Name = "Multi-PC Management",
                EntText = "Yes", ProText = "No", ProColor = "#71798A" },
    };

    public IRelayCommand ActivateTrialCommand { get; }
    public IRelayCommand ActivateProCommand { get; }
    public IRelayCommand DeactivateCommand { get; }
    public IRelayCommand OpenTelegramBotCommand { get; }

    public LicenseViewModel()
    {
        ActivateTrialCommand = new RelayCommand(ActivateTrial);
        ActivateProCommand = new AsyncRelayCommand(ActivateProAsync);
        DeactivateCommand = new RelayCommand(Deactivate);
        OpenTelegramBotCommand = new RelayCommand(OpenTelegramBot);
        Refresh();
    }

    private void Refresh()
    {
        var license = _licenseService.CurrentLicense;

        DaysLeft = license.DaysLeft;
        TierBadge = license.Tier.ToUpper();
        IsLicenseActive = license.IsValid && !license.IsExpired;
        IsExpired = license.IsExpired && license.Tier != "Free";
        CanActivateTrial = !license.IsValid || (license.Tier == "Free" && license.IsExpired);

        StatusText = license.IsValid && !license.IsExpired
            ? $"{license.Tier} — Active"
            : license.IsExpired ? "Expired" : "No active license";

        StatusColor = license.IsValid && !license.IsExpired
            ? "#D48CB5"
            : "#FB7185";
    }

    private void ActivateTrial()
    {
        var success = _licenseService.ActivateTrial();
        Refresh();

        if (success)
        {
            ActivationMessage = "Trial activated — 14 days of Pro access";
            ActivationMessageColor = "#34D399";
        }
        else
        {
            ActivationMessage = "Trial already used on this machine";
            ActivationMessageColor = "#FB7185";
        }
        ClearActivationMessageAfterDelay();
    }

    private async Task ActivateProAsync()
    {
        if (string.IsNullOrWhiteSpace(ActivationKey)) return;

        IsActivating = true;
        ActivationMessage = "Validating key...";
        ActivationMessageColor = "#71798A";

        // Небольшая задержка для UX — имитирует проверку
        await Task.Delay(300);

        var (success, message) = _licenseService.ActivatePro(ActivationKey);

        ActivationMessage = success ? message : $"Error: {message}";
        ActivationMessageColor = success ? "#34D399" : "#FB7185";

        if (success) ActivationKey = "";

        IsActivating = false;
        Refresh();
        ClearActivationMessageAfterDelay();
    }

    private void Deactivate()
    {
        _licenseService.Deactivate();
        Refresh();
        ActivationMessage = "License deactivated";
        ActivationMessageColor = "#71798A";
        ClearActivationMessageAfterDelay();
    }

    private void OpenTelegramBot()
    {
        // Открывает Telegram бот в браузере
        // Замени username на реальный username бота
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://t.me/YourSystemGuardBot",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private async void ClearActivationMessageAfterDelay()
    {
        await Task.Delay(5000);
        ActivationMessage = "";
    }
}