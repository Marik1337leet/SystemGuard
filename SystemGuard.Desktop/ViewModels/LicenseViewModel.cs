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
    [ObservableProperty] private bool _isDeactivateConfirming;

    public string DeactivateButtonText => IsDeactivateConfirming ? "Confirm — annul license?" : "Deactivate";

    /// <summary>
    /// Поле ключа спрятано: обычный пользователь покупает кнопкой «Купить Pro»
    /// (ключ прилетает из шоп-бота и автовставляется), а ручной ввод нужен
    /// только для подарочных ключей с розыгрышей.
    /// </summary>
    [ObservableProperty] private bool _isGiftKeyVisible;

    /// <summary>
    /// Username шоп-бота (БЕЗ токена!). Токен живёт только на сервере/твоём ПК
    /// в shopbot.json или переменной SHOP_BOT_TOKEN — в код его вшивать нельзя.
    /// </summary>
    public string ShopBotUsername { get; set; } = "SystemGuardPayBot";

    /// <summary>Аккаунт поддержки (кнопка на экране License).</summary>
    public string SupportUrl { get; set; } = "https://t.me/mattrix_solution";

    /// <summary>Красивая справка: что даёт Pro — для экрана License.</summary>
    public string ProHelpText =>
        "Pro открывает: глубокое удаление программ, игровой режим, " +
        "бенчмарки и стресс-тесты, расширенную сеть (DNS, hosts, сканер портов, WoL), " +
        "шифрование файлов и менеджер паролей, расширенный Telegram-пульт (скриншоты, файлы, " +
        "команды, стрим, камера, live-ссылка) и расширенное управление процессами. " +
        "Free навсегда остаётся: мониторинг, базовые процессы, автозагрузка, базовая очистка " +
        "и базовый Defender-скан.";

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
    public IRelayCommand OpenShopBotCommand { get; }
    public IRelayCommand OpenSupportCommand { get; }
    public IRelayCommand ToggleGiftKeyCommand { get; }
    public IRelayCommand CheckPurchaseCommand { get; }

    public LicenseViewModel()
    {
        ActivateTrialCommand = new RelayCommand(ActivateTrial);
        ActivateProCommand = new AsyncRelayCommand(ActivateProAsync);
        DeactivateCommand = new RelayCommand(Deactivate);
        OpenTelegramBotCommand = new RelayCommand(OpenTelegramBot);
        OpenShopBotCommand = new RelayCommand(OpenShopBot);
        OpenSupportCommand = new RelayCommand(OpenSupport);
        ToggleGiftKeyCommand = new RelayCommand(() => IsGiftKeyVisible = !IsGiftKeyVisible);
        CheckPurchaseCommand = new AsyncRelayCommand(CheckPurchaseAsync);
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

        // Мгновенно перекрасить все гейты (бейдж, Telegram, Scheduler...).
        Services.LicenseGate.NotifyChanged();
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
        if (!IsDeactivateConfirming)
        {
            IsDeactivateConfirming = true;
            ActivationMessage = "Внимание: лицензия будет аннулирована полностью без возможности возврата. Нажмите ещё раз для подтверждения.";
            ActivationMessageColor = "#FB7185";
            _ = ResetDeactivateConfirmAsync();
            return;
        }
        IsDeactivateConfirming = false;
        _licenseService.Deactivate();
        Refresh();
        ActivationMessage = "License deactivated";
        ActivationMessageColor = "#71798A";
        ClearActivationMessageAfterDelay();
    }

    private async Task ResetDeactivateConfirmAsync()
    {
        await Task.Delay(6000);
        if (IsDeactivateConfirming)
        {
            IsDeactivateConfirming = false;
            if (ActivationMessage.StartsWith("Внимание"))
                ActivationMessage = "";
        }
    }

    /// <summary>
    /// «Я оплатил, проверить»: забирает ключ по HWID из Supabase и активирует
    /// БЕЗ показа ключа пользователю. Работает только когда в
    /// %LocalAppData%\SystemGuard\supabase.json вписаны supabaseUrl + anonKey
    /// (публичный anon, НЕ service key). Без Supabase — просит вставить ключ вручную.
    /// </summary>
    private async Task CheckPurchaseAsync()
    {
        IsActivating = true;
        ActivationMessage = "Checking purchase…";
        ActivationMessageColor = "#71798A";
        try
        {
            var key = await Services.SupabaseLicenseClient.TryFetchKeyByHwidAsync(MachineId);
            if (string.IsNullOrWhiteSpace(key))
            {
                ActivationMessage = "Оплата не найдена. Если только что оплатил — подожди минуту и нажми ещё раз, либо вставь ключ из бота ниже.";
                ActivationMessageColor = "#FB7185";
            }
            else
            {
                var (success, message) = _licenseService.ActivatePro(key.Trim());
                ActivationMessage = success ? $"Pro активирована! {message}" : $"Error: {message}";
                ActivationMessageColor = success ? "#34D399" : "#FB7185";
                Refresh();
            }
        }
        catch (Exception ex)
        {
            ActivationMessage = $"Check failed: {ex.Message}";
            ActivationMessageColor = "#FB7185";
        }
        finally
        {
            IsActivating = false;
            ClearActivationMessageAfterDelay();
        }
    }
    private void OpenTelegramBot()
    {
        // Ключи выдаёт шоп-бот @SystemGuardPayBot — ведём туда же, что и кнопка покупки.
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://t.me/SystemGuardPayBot",
                UseShellExecute = true
            });
        }
        catch { }
    }

    /// <summary>Поддержка: открывает чат mattrix_solution.</summary>
    private void OpenSupport()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = string.IsNullOrWhiteSpace(SupportUrl) ? "https://t.me/mattrix_solution" : SupportUrl,
                UseShellExecute = true
            });
        }
        catch { }
    }

    /// <summary>
    /// Купить Pro: открывает шоп-бота с HWID в start-параметре
    /// (бот привяжет выданный ключ к этому ПК — вставлять руками не придётся,
    /// ключ из чата подхватится кнопкой «Вставить из буфера» / автовставкой).
    /// </summary>
    private void OpenShopBot()
    {
        try
        {
            var hwid = MachineId ?? "";
            var url = string.IsNullOrWhiteSpace(ShopBotUsername)
                ? "https://t.me/SystemGuardPayBot"
                : $"https://t.me/{ShopBotUsername}?start=buy_{hwid}";
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch { }
    }

    partial void OnIsDeactivateConfirmingChanged(bool value) => OnPropertyChanged(nameof(DeactivateButtonText));

    private async void ClearActivationMessageAfterDelay()
    {
        await Task.Delay(5000);
        ActivationMessage = "";
    }
}