using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using SystemGuard.Desktop.Models;
using SystemGuard.Desktop.Services;

namespace SystemGuard.Desktop.ViewModels;

// ВАЖНО: этот файл полностью ЗАМЕНЯЕТ старый SettingsViewModel.cs.
public partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService = new();
    private readonly LocalizationService _localizationService = LocalizationService.Instance;
    private readonly AuthService _authService = new();

    [ObservableProperty] private AppSettings _settings = new();
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private ObservableCollection<string> _languages = new() { "English", "Русский", "Deutsch", "Français", "Español", "Português", "Українська", "Қазақша", "中文", "日本語", "한국어" };
    [ObservableProperty] private string _selectedLanguage = "English";
    [ObservableProperty] private bool _isCheckingUpdate;
    [ObservableProperty] private string _latestVersion = "";
    [ObservableProperty] private bool _isUpdateAvailable;
    [ObservableProperty] private string _releaseUrl = "";
    [ObservableProperty] private bool _isDownloadingUpdate;
    [ObservableProperty] private double _updateProgress;
    private string _setupAssetUrl = "";
    private string _setupAssetName = "";
    [ObservableProperty] private string _accountEmail = "";
    [ObservableProperty] private string _accountProvider = "";
    [ObservableProperty] private bool _isSignedIn;
    [ObservableProperty] private string _accountStatus = "";
    [ObservableProperty] private bool _isSigningIn;
    // About + политики (видны в Settings → About, дублируют /privacy /terms /safety в боте)
    public string AboutTitle => "SystemGuard";
    public string AboutSubtitle => "Монитор, твикер и удалённый пульт для Windows";
    public string PrivacyText => PoliciesService.Privacy;
    public string TermsText => PoliciesService.Terms;
    public string SafetyText => PoliciesService.Safety;
    public string StarsOwnerText => $"Stars → владелец {PoliciesService.StarsOwnerId} (вывод через Fragment)";
    public string RemoteHelpText => PoliciesService.FeatureNotes;

    // Подвкладки настроек: General / Colors / Telegram / License.
    // Appearance удалена (виджеты переехали в General), Scheduler удалён полностью.
    // Hotkeys живут в Gaming, Fans — в Performance.
    [ObservableProperty] private LicenseViewModel _license = new();
    [ObservableProperty] private ColorPickerViewModel _colors = new();
    [ObservableProperty] private AppearancePacksViewModel _appearance = new();
    [ObservableProperty] private TelegramViewModel _telegram = new();
    [ObservableProperty] private string _selectedSection = "General";
    [ObservableProperty] private bool _isGeneralTab = true;
    [ObservableProperty] private bool _isColorsTab;
    [ObservableProperty] private bool _isTelegramTab;
    [ObservableProperty] private bool _isLicenseTab;

    public IRelayCommand<string> SelectTabCommand { get; }

    public Window? OwnerWindow { get; set; }

    public IRelayCommand SaveCommand { get; }
    public IRelayCommand ResetCommand { get; }
    public IRelayCommand ExportCommand { get; }
    public IRelayCommand ImportCommand { get; }
    public IRelayCommand CheckUpdateCommand { get; }
    public IRelayCommand OpenReleasePageCommand { get; }
    public IRelayCommand InstallUpdateCommand { get; }
    public IRelayCommand SignInGoogleCommand { get; }
    public IRelayCommand SignInAppleCommand { get; }
    public IRelayCommand SignOutCommand { get; }

    public SettingsViewModel()
    {
        SelectTabCommand = new RelayCommand<string>(SelectTab);
        SaveCommand = new RelayCommand(Save);
        ResetCommand = new RelayCommand(Reset);
        ExportCommand = new AsyncRelayCommand(ExportAsync);
        ImportCommand = new AsyncRelayCommand(ImportAsync);
        CheckUpdateCommand = new AsyncRelayCommand(() => CheckUpdateAsync(silent: false));
        OpenReleasePageCommand = new RelayCommand(OpenReleasePage);
        InstallUpdateCommand = new AsyncRelayCommand(InstallUpdateAsync);
        SignInGoogleCommand = new AsyncRelayCommand(SignInGoogleAsync);
        SignInAppleCommand = new AsyncRelayCommand(SignInAppleAsync);
        SignOutCommand = new RelayCommand(SignOut);

        Load();
        RefreshAccount();
        _authService.OnAccountChanged += RefreshAccount;
    }

    private void SelectTab(string? tab)
    {
        if (string.IsNullOrEmpty(tab)) return;
        // Удаленные табы маппим на живые: Appearance → General (там виджеты), Scheduler → License.
        if (tab == "Appearance") tab = "General";
        if (tab == "Scheduler") tab = "License";
        SelectedSection = tab;
        IsGeneralTab = tab == "General";
        IsColorsTab = tab == "Colors";
        IsTelegramTab = tab == "Telegram";
        IsLicenseTab = tab == "License";
    }

    /// <summary>Открыть таб покупки/управления лицензией (для бейджа слева снизу).</summary>
    public void SelectLicenseTab() => SelectTab("License");

    public override void OnActivated()
    {
        base.OnActivated();
        // Буфер обмена живёт на TopLevel: даём LicenseVM reader для кнопки «Я оплатил».
        Services.SupabaseLicenseClient.ClipboardReader = async () =>
        {
            try
            {
                if (OwnerWindow?.Clipboard == null) return null;
                return await OwnerWindow.Clipboard.GetTextAsync();
            }
            catch { return null; }
        };
        License.OnActivated();
        Colors.OnActivated();
        Appearance.OnActivated();
        Telegram.OnActivated();
        // Тихая проверка при входе в настройки, не чаще раза в сутки.
        try
        {
            if (Settings.AutoUpdate && IsUpdateCheckStale())
                _ = CheckUpdateAsync(silent: true);
        }
        catch { }
    }

    private bool IsUpdateCheckStale()
    {
        try
        {
            var last = Settings.LastCheckUpdate;
            if (string.IsNullOrWhiteSpace(last) || last == "Never") return true;
            if (DateTime.TryParse(last, out var dt))
                return (DateTime.Now - dt).TotalHours >= 24;
            return true;
        }
        catch { return true; }
    }

    public override void OnDeactivated()
    {
        base.OnDeactivated();
        License.OnDeactivated();
        Colors.OnDeactivated();
        Appearance.OnDeactivated();
        Telegram.OnDeactivated();
    }

    // Каскад Pro-флага во вложенные VM: без него Telegram/Scheduler/License
    // навсегда оставались IsProLicense=false и показывали замки при активной Pro.
    public override void RefreshLicenseGate()
    {
        base.RefreshLicenseGate();
        try
        {
            License.RefreshLicenseGate();
            Colors.RefreshLicenseGate();
            Appearance.RefreshLicenseGate();
            Telegram.RefreshLicenseGate();
        }
        catch { }
    }

    // ── Account (Google / Apple) ────────────────────────────────────────────

    private void RefreshAccount()
    {
        var s = _authService.CurrentSession;
        IsSignedIn = s != null;
        AccountEmail = s?.Email ?? "";
        AccountProvider = s?.Provider ?? "";
        if (s != null)
            AccountStatus = $"Signed in with {s.Provider} as {s.Email}";
    }

    private async Task SignInGoogleAsync()
    {
        if (IsSigningIn) return;
        IsSigningIn = true;
        AccountStatus = "Opening browser for Google sign-in…";
        try
        {
            var s = await _authService.SignInWithGoogleAsync();
            AccountStatus = $"Signed in with Google as {s.Email}";
        }
        catch (Exception ex) { AccountStatus = $"Sign-in error: {ex.Message}"; }
        finally { IsSigningIn = false; RefreshAccount(); }
    }

    private async Task SignInAppleAsync()
    {
        if (IsSigningIn) return;
        IsSigningIn = true;
        AccountStatus = "Opening browser for Apple sign-in…";
        try
        {
            var s = await _authService.SignInWithAppleAsync();
            AccountStatus = $"Signed in with Apple as {s.Email}";
        }
        catch (Exception ex) { AccountStatus = $"Sign-in error: {ex.Message}"; }
        finally { IsSigningIn = false; RefreshAccount(); }
    }

    private void SignOut()
    {
        _authService.SignOut();
        AccountStatus = "Signed out";
        RefreshAccount();
    }

    private void Load()
    {
        Settings = _settingsService.Load();
        SelectedLanguage = _localizationService.GetDisplayName(Settings.Language);
        StatusText = "Settings loaded";
    }

    private void Save()
    {
        // Сохраняем язык как КОД (en/ru), а не отображаемое имя —
        // согласовано с LocalizationService.SetLanguage
        Settings.Language = _localizationService.GetLanguageCode(SelectedLanguage);
        _settingsService.Save(Settings);

        // Реально применяем язык — раньше это никак не было связано
        _localizationService.SetLanguage(Settings.Language);

        StatusText = $"Settings saved. Language: {SelectedLanguage}";

        // Тему применяем через диспетчер: Save могут дёрнуть и с фона,
        // а RequestedThemeVariant требует UI-поток ("Call from invalid thread").
        if (App.Current != null)
        {
            var variant = Settings.DarkTheme
                ? Avalonia.Styling.ThemeVariant.Dark
                : Avalonia.Styling.ThemeVariant.Light;
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                App.Current.RequestedThemeVariant = variant;
            else
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (App.Current != null) App.Current.RequestedThemeVariant = variant;
                });
        }

        ClearStatusAfterDelay();
    }

    private void Reset()
    {
        _settingsService.Reset();
        Load();
        StatusText = "Settings reset to default";
        ClearStatusAfterDelay();
    }

    // ── Export / Import через реальный диалог сохранения ────────────────────

    private async Task ExportAsync()
    {
        var topLevel = OwnerWindow != null ? TopLevel.GetTopLevel(OwnerWindow) : null;
        if (topLevel?.StorageProvider == null)
        {
            StatusText = "Cannot open save dialog";
            return;
        }

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Settings",
            SuggestedFileName = "SystemGuard_Settings.json",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("JSON File") { Patterns = new[] { "*.json" } }
            }
        });

        if (file != null)
        {
            _settingsService.Export(file.Path.LocalPath);
            StatusText = $"Exported to {file.Name}";
            ClearStatusAfterDelay();
        }
    }

    private async Task ImportAsync()
    {
        var topLevel = OwnerWindow != null ? TopLevel.GetTopLevel(OwnerWindow) : null;
        if (topLevel?.StorageProvider == null)
        {
            StatusText = "Cannot open file dialog";
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Settings",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("JSON File") { Patterns = new[] { "*.json" } }
            }
        });

        if (files.Count > 0)
        {
            _settingsService.Import(files[0].Path.LocalPath);
            Load();
            StatusText = "Settings imported";
            ClearStatusAfterDelay();
        }
    }

    private async Task CheckUpdateAsync(bool silent)
    {
        if (IsCheckingUpdate || IsDownloadingUpdate) return;
        IsCheckingUpdate = true;
        IsUpdateAvailable = false;
        if (!silent) StatusText = "Checking for updates on GitHub…";
        try
        {
            var svc = new UpdateService();
            var res = await svc.CheckAsync();
            Settings.LastCheckUpdate = DateTime.Now.ToString("g");
            _settingsService.Save(Settings);
            _setupAssetUrl = res.SetupAssetUrl;
            _setupAssetName = res.SetupAssetName;
            if (!res.Success)
            {
                if (!silent) StatusText = $"Update check failed: {res.Error} (offline?)";
            }
            else
            {
                LatestVersion = res.Latest;
                ReleaseUrl = res.ReleaseUrl;
                if (res.HasUpdate)
                {
                    IsUpdateAvailable = true;
                    if (!silent) StatusText = $"Update available: v{res.Latest} (current v{res.Current})";
                }
                else if (!silent)
                {
                    StatusText = $"You are running the latest version (v{res.Current})";
                }
            }
        }
        catch (Exception ex) { if (!silent) StatusText = $"Update check failed: {ex.Message}"; }
        finally { IsCheckingUpdate = false; if (!silent) ClearStatusAfterDelay(); }
    }

    // Тихая установка: качает SystemGuard-Setup-*.exe из релиза и запускает /SILENT.
    // Без ассета в релизе — открывает страницу релиза как раньше.
    private async Task InstallUpdateAsync()
    {
        if (IsDownloadingUpdate || IsCheckingUpdate) return;
        if (string.IsNullOrWhiteSpace(_setupAssetUrl))
        {
            OpenReleasePage();
            return;
        }
        IsDownloadingUpdate = true;
        UpdateProgress = 0;
        StatusText = $"Downloading {_setupAssetName}…";
        try
        {
            var svc = new UpdateService();
            var progress = new Progress<double>(p =>
            {
                UpdateProgress = p;
                StatusText = $"Downloading update… {p:F0}%";
            });
            var (ok, path, error) = await svc.DownloadSetupAsync(_setupAssetUrl, _setupAssetName, progress);
            if (!ok) { StatusText = $"Download failed: {error}"; return; }
            StatusText = UpdateService.LaunchSetupAndExit(path);
        }
        catch (Exception ex) { StatusText = $"Update failed: {ex.Message}"; }
        finally { IsDownloadingUpdate = false; }
    }

    private void OpenReleasePage()
    {
        try
        {
            var url = string.IsNullOrWhiteSpace(ReleaseUrl) ? "https://github.com/marik1337leet/SystemGuard/releases/latest" : ReleaseUrl;
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }

    private async void ClearStatusAfterDelay()
    {
        await Task.Delay(3000);
        StatusText = "";
    }
}
