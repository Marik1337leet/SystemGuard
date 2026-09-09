using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using SystemGuard.Desktop.Services;

namespace SystemGuard.Desktop.ViewModels;

public partial class SecurityViewModel : ViewModelBase
{
    private readonly PasswordVaultService _vault = new();
    private readonly ParentalControlService _parental = new();
    private readonly AuditLogService _audit = new();

    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private string _defenderStatus = "";
    [ObservableProperty] private string _filePath = "";
    [ObservableProperty] private string _filePassword = "";
    [ObservableProperty] private ObservableCollection<VaultEntry> _vaultEntries = new();
    [ObservableProperty] private string _newTitle = "";
    [ObservableProperty] private string _newLogin = "";
    [ObservableProperty] private string _newPassword = "";
    [ObservableProperty] private ObservableCollection<string> _blockedSites = new();
    [ObservableProperty] private string _newSite = "";

    public IRelayCommand DefenderScanCommand { get; }
    public IRelayCommand DefenderStatusCommand { get; }
    public IRelayCommand EncryptCommand { get; }
    public IRelayCommand DecryptCommand { get; }
    public IRelayCommand AddVaultCommand { get; }
    public IRelayCommand<string> DeleteVaultCommand { get; }
    public IRelayCommand BlockSiteCommand { get; }
    public IRelayCommand<string> UnblockSiteCommand { get; }

    public SecurityViewModel()
    {
        DefenderScanCommand = new RelayCommand(() => { StatusText = SecurityService.DefenderQuickScan(); _audit.Log("Security", "DefenderScan", StatusText); });
        DefenderStatusCommand = new RelayCommand(() => DefenderStatus = SecurityService.DefenderStatus());
        EncryptCommand = new AsyncRelayCommand(EncryptAsync);
        DecryptCommand = new AsyncRelayCommand(DecryptAsync);
        AddVaultCommand = new RelayCommand(AddVault);
        DeleteVaultCommand = new RelayCommand<string>(DeleteVault);
        BlockSiteCommand = new RelayCommand(() => { if (!string.IsNullOrWhiteSpace(NewSite)) { StatusText = _parental.Block(NewSite); NewSite = ""; RefreshBlocked(); _audit.Log("Security", "Block", StatusText); } });
        UnblockSiteCommand = new RelayCommand<string>(s => { if (!string.IsNullOrEmpty(s)) { StatusText = _parental.Unblock(s); RefreshBlocked(); } });
        RefreshVault();
        RefreshBlocked();
    }

    private void RefreshVault() =>
        VaultEntries = new ObservableCollection<VaultEntry>(_vault.List());

    private void RefreshBlocked() =>
        BlockedSites = new ObservableCollection<string>(_parental.ListBlocked());

    private void AddVault()
    {
        if (string.IsNullOrWhiteSpace(NewTitle)) { StatusText = "Enter title"; return; }
        _vault.Add(new VaultEntry { Title = NewTitle.Trim(), Login = NewLogin.Trim(), Password = NewPassword, Url = "" });
        NewTitle = NewLogin = NewPassword = "";
        RefreshVault();
        StatusText = "Saved to vault";
        _audit.Log("Security", "VaultAdd", "");
    }

    private void DeleteVault(string? title)
    {
        if (string.IsNullOrEmpty(title)) return;
        _vault.Delete(title);
        RefreshVault();
    }

    private async Task EncryptAsync()
    {
        if (string.IsNullOrWhiteSpace(FilePath) || string.IsNullOrEmpty(FilePassword)) { StatusText = "Select file and password"; return; }
        try
        {
            var src = FilePath.Trim().Trim('"');
            await SecurityService.EncryptFileAsync(src, src + ".sgenc", FilePassword);
            StatusText = $"Encrypted: {src}.sgenc";
            FilePath = src + ".sgenc"; // следующий шаг пользователя — расшифровка
            _audit.Log("Security", "Encrypt", src);
        }
        catch (Exception ex) { StatusText = ex.Message; }
    }

    private async Task DecryptAsync()
    {
        if (string.IsNullOrWhiteSpace(FilePath) || string.IsNullOrEmpty(FilePassword)) { StatusText = "Select file and password"; return; }
        try
        {
            var src = FilePath.Trim().Trim('"');
            var dest = src.EndsWith(".sgenc") ? src[..^6] + ".dec" : src + ".dec";
            await SecurityService.DecryptFileAsync(src, dest, FilePassword);
            StatusText = $"Decrypted: {dest}";
        }
        catch (Exception ex) { StatusText = ex.Message; }
    }
}
