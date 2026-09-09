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
    [ObservableProperty] private string _accountEmail = "";
    [ObservableProperty] private string _accountProvider = "";
    [ObservableProperty] private bool _isSignedIn;
    [ObservableProperty] private string _accountStatus = "";
    [ObservableProperty] private bool _isSigningIn;

    // Подвкладки настроек: General / Appearance / Colors / Telegram / Scheduler / License.
    [ObservableProperty] private LicenseViewModel _license = new();
    [ObservableProperty] private ColorPickerViewModel _colors = new();
    [ObservableProperty] private AppearancePacksViewModel _appearance = new();
    [ObservableProperty] private TelegramViewModel _telegram = new();
    [ObservableProperty] private SchedulerViewModel _scheduler = new();
    [ObservableProperty] private string _selectedSection = "General";
    [ObservableProperty] private bool _isGeneralTab = true;
    [ObservableProperty] private bool _isAppearanceTab;
    [ObservableProperty] private bool _isColorsTab;
    [ObservableProperty] private bool _isTelegramTab;
    [ObservableProperty] private bool _isSchedulerTab;
    [ObservableProperty] private bool _isLicenseTab;

    public IRelayCommand<string> SelectTabCommand { get; }

    public Window? OwnerWindow { get; set; }

    public IRelayCommand SaveCommand { get; }
    public IRelayCommand ResetCommand { get; }
    public IRelayCommand ExportCommand { get; }
    public IRelayCommand ImportCommand { get; }
    public IRelayCommand CheckUpdateCommand { get; }
    public IRelayCommand OpenReleasePageCommand { get; }
    public IRelayCommand OpenAppDataCommand { get; }
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
        CheckUpdateCommand = new AsyncRelayCommand(CheckUpdate);
        OpenReleasePageCommand = new RelayCommand(OpenReleasePage);
        OpenAppDataCommand = new RelayCommand(OpenAppData);
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
        SelectedSection = tab;
        IsGeneralTab = tab == "General";
        IsAppearanceTab = tab == "Appearance";
        IsColorsTab = tab == "Colors";
        IsTelegramTab = tab == "Telegram";
        IsSchedulerTab = tab == "Scheduler";
        IsLicenseTab = tab == "License";
    }

    public override void OnActivated()
    {
        base.OnActivated();
        License.OnActivated();
        Colors.OnActivated();
        Appearance.OnActivated();
        Telegram.OnActivated();
        Scheduler.OnActivated();
    }

    public override void OnDeactivated()
    {
        base.OnDeactivated();
        License.OnDeactivated();
        Colors.OnDeactivated();
        Appearance.OnDeactivated();
        Telegram.OnDeactivated();
        Scheduler.OnDeactivated();
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

    private async Task CheckUpdate()
    {
        IsCheckingUpdate = true;
        IsUpdateAvailable = false;
        StatusText = "Checking for updates on GitHub…";
        try
        {
            var svc = new UpdateService();
            var res = await svc.CheckAsync();
            Settings.LastCheckUpdate = DateTime.Now.ToString("g");
            _settingsService.Save(Settings);
            if (!res.Success)
            {
                StatusText = $"Update check failed: {res.Error} (offline?)";
            }
            else
            {
                LatestVersion = res.Latest;
                ReleaseUrl = res.ReleaseUrl;
                if (res.HasUpdate)
                {
                    IsUpdateAvailable = true;
                    StatusText = $"Update available: v{res.Latest} (current v{res.Current})";
                }
                else
                {
                    StatusText = $"You are running the latest version (v{res.Current})";
                }
            }
        }
        catch (Exception ex) { StatusText = $"Update check failed: {ex.Message}"; }
        finally { IsCheckingUpdate = false; ClearStatusAfterDelay(); }
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

    private void OpenAppData()
    {
        var path = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SystemGuard");
        try { Process.Start("explorer.exe", path); }
        catch { }
    }

    private async void ClearStatusAfterDelay()
    {
        await Task.Delay(3000);
        StatusText = "";
    }
}
