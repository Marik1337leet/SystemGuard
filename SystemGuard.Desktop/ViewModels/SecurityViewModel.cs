using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SystemGuard.Desktop.Services;

namespace SystemGuard.Desktop.ViewModels;

public partial class VaultRowViewModel : ObservableObject
{
    public VaultEntry Entry { get; }
    [ObservableProperty] private bool _isRevealed;

    public string PasswordDisplay => IsRevealed
        ? Entry.Password
        : new string('•', Math.Clamp(Entry.Password?.Length ?? 0, 4, 16));

    public VaultRowViewModel(VaultEntry entry) => Entry = entry;

    partial void OnIsRevealedChanged(bool value) => OnPropertyChanged(nameof(PasswordDisplay));
}

public partial class SecurityViewModel : ViewModelBase
{
    private readonly PasswordVaultService _vault = new();
    private readonly ParentalControlService _parental = new();
    private readonly AuditLogService _audit = new();

    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private string _defenderStatus = "";
    [ObservableProperty] private string _filePath = "";
    [ObservableProperty] private string _filePassword = "";
    [ObservableProperty] private ObservableCollection<VaultRowViewModel> _vaultEntries = new();
    [ObservableProperty] private string _newTitle = "";
    [ObservableProperty] private string _newLogin = "";
    [ObservableProperty] private string _newPassword = "";
    [ObservableProperty] private ObservableCollection<string> _blockedSites = new();
    [ObservableProperty] private string _newSite = "";

    public IRelayCommand DefenderScanCommand { get; }
    public IRelayCommand DefenderFullScanCommand { get; }
    public IRelayCommand DefenderStatusCommand { get; }
    public IRelayCommand DefenderUpdateCommand { get; }
    public IRelayCommand DefenderThreatsCommand { get; }
    public IRelayCommand EncryptCommand { get; }
    public IRelayCommand DecryptCommand { get; }
    public IRelayCommand AddVaultCommand { get; }
    public IRelayCommand<string> DeleteVaultCommand { get; }
    public IRelayCommand<string> RevealVaultCommand { get; }
    public IRelayCommand<string> CopyVaultPasswordCommand { get; }
    public IRelayCommand BlockSiteCommand { get; }
    public IRelayCommand<string> UnblockSiteCommand { get; }

    public SecurityViewModel()
    {
        DefenderScanCommand = new RelayCommand(() => { StatusText = SecurityService.DefenderQuickScan(); _audit.Log("Security", "DefenderScan", StatusText); });
        DefenderFullScanCommand = new RelayCommand(() => { StatusText = SecurityService.DefenderFullScan(); _audit.Log("Security", "DefenderFullScan", StatusText); });
        DefenderStatusCommand = new RelayCommand(() => DefenderStatus = SecurityService.DefenderStatus());
        DefenderUpdateCommand = new RelayCommand(() => { StatusText = "Updating signatures…"; StatusText = SecurityService.DefenderUpdateSignatures(); _audit.Log("Security", "DefenderUpdate", StatusText); });
        DefenderThreatsCommand = new RelayCommand(() => DefenderStatus = SecurityService.DefenderThreats());
        EncryptCommand = new AsyncRelayCommand(EncryptAsync);
        DecryptCommand = new AsyncRelayCommand(DecryptAsync);
        AddVaultCommand = new RelayCommand(AddVault);
        DeleteVaultCommand = new RelayCommand<string>(DeleteVault);
        RevealVaultCommand = new RelayCommand<string>(ToggleReveal);
        CopyVaultPasswordCommand = new AsyncRelayCommand<string>(CopyVaultPasswordAsync);
        BlockSiteCommand = new RelayCommand(() => { if (!string.IsNullOrWhiteSpace(NewSite)) { StatusText = _parental.Block(NewSite); NewSite = ""; RefreshBlocked(); _audit.Log("Security", "Block", StatusText); } });
        UnblockSiteCommand = new RelayCommand<string>(s => { if (!string.IsNullOrEmpty(s)) { StatusText = _parental.Unblock(s); RefreshBlocked(); } });
        RefreshVault();
        RefreshBlocked();
    }

    private void RefreshVault() =>
        VaultEntries = new ObservableCollection<VaultRowViewModel>(
            _vault.List().Select(e => new VaultRowViewModel(e)));

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

    private void ToggleReveal(string? title)
    {
        if (string.IsNullOrEmpty(title)) return;
        foreach (var row in VaultEntries)
            row.IsRevealed = string.Equals(row.Entry.Title, title, StringComparison.OrdinalIgnoreCase)
                ? !row.IsRevealed
                : false;
    }

    private async Task CopyVaultPasswordAsync(string? title)
    {
        if (string.IsNullOrEmpty(title)) return;
        var row = VaultEntries.FirstOrDefault(r =>
            string.Equals(r.Entry.Title, title, StringComparison.OrdinalIgnoreCase));
        if (row == null) return;
        try
        {
            var top = AppClipboard.TopLevel;
            if (top?.Clipboard == null) { StatusText = "Clipboard unavailable"; return; }
            await top.Clipboard.SetTextAsync(row.Entry.Password ?? "");
            StatusText = $"Password for \"{row.Entry.Title}\" copied to clipboard";
        }
        catch (Exception ex) { StatusText = $"Copy failed: {ex.Message}"; }
    }

    private async Task DecryptAsync()
    {
        if (string.IsNullOrWhiteSpace(FilePath) || string.IsNullOrEmpty(FilePassword)) { StatusText = "Select file and password"; return; }
        var src = FilePath.Trim().Trim('"');
        var dest = src.EndsWith(".sgenc") ? src[..^6] + ".dec" : src + ".dec";
        try
        {
            if (!File.Exists(src)) { StatusText = "File not found"; return; }
            if (new FileInfo(src).Length < 32) { StatusText = "Not a SystemGuard encrypted file (too small)"; return; }
            await SecurityService.DecryptFileAsync(src, dest, FilePassword);
            // Пустой результат при непустом исходнике = неверный пароль/битый файл.
            // Раньше пустой .dec молча оставался на диске — отсюда «расшифровывает в пустоту».
            if (new FileInfo(dest).Length == 0 && new FileInfo(src).Length > 32)
            {
                try { File.Delete(dest); } catch { }
                StatusText = "Wrong password or corrupted file — nothing decrypted";
                return;
            }
            StatusText = $"Decrypted: {dest}";
            _audit.Log("Security", "Decrypt", src);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            try { if (File.Exists(dest) && new FileInfo(dest).Length == 0) File.Delete(dest); } catch { }
            StatusText = "Wrong password or corrupted file — nothing decrypted";
        }
        catch (Exception ex)
        {
            try { if (File.Exists(dest) && new FileInfo(dest).Length == 0) File.Delete(dest); } catch { }
            StatusText = ex.Message;
        }
    }
}
