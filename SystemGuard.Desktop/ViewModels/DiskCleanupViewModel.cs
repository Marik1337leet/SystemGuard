using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SystemGuard.Desktop.Services;

namespace SystemGuard.Desktop.ViewModels;

public partial class DiskCleanupViewModel : ViewModelBase
{
    private readonly DiskService _diskService = new();
    private readonly AdvancedCleanupService _cleanupService = new();

    [ObservableProperty] private ObservableCollection<DiskInfoModel> _disks = new();
    [ObservableProperty] private ObservableCollection<CleanupItem> _cleanupItems = new();
    [ObservableProperty] private ObservableCollection<FolderSizeInfo> _largeFolders = new();
    [ObservableProperty] private ObservableCollection<LargeFileInfo> _largeFiles = new();
    [ObservableProperty] private ObservableCollection<DuplicateFileGroup> _duplicates = new();
    [ObservableProperty] private ObservableCollection<LargeFileInfo> _oldFiles = new();
    [ObservableProperty] private int _oldFileDays = 90;
    [ObservableProperty] private ObservableCollection<BackupEntry> _backups = new();
    [ObservableProperty] private string _savesDir = "";
    [ObservableProperty] private string _savesBackupRoot = "";
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _isCleaning;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private double _progress;

    // ── Droplet tabs: Cleanup / Tools / Privacy / Uninstall ────────────────
    // Капля-индикатор двигается в code-behind (Margin-анимация, как в референсе),
    // здесь только состояние секции. Все табы фиксированные 132px.
    [ObservableProperty] private string _selectedSection = "Cleanup";
    [ObservableProperty] private bool _isCleanupTab = true;
    [ObservableProperty] private bool _isToolsTab;
    [ObservableProperty] private bool _isPrivacyTab;
    [ObservableProperty] private bool _isUninstallTab;

    public bool HasCleanupItems => CleanupItems.Count > 0;
    public bool HasLargeFolders => LargeFolders.Count > 0;
    public bool HasLargeFiles => LargeFiles.Count > 0;
    public bool HasDuplicates => Duplicates.Count > 0;
    public bool HasOldFiles => OldFiles.Count > 0;
    public long TotalCleanupSize => CleanupItems.Where(c => c.IsSelected).Sum(c => c.Size);
    public long TotalDuplicateWaste => Duplicates.Sum(d => d.WastedSpace);

    // ── Privacy: подмножество CleanupItems (браузер/куки/история) ───────────
    public System.Collections.Generic.IEnumerable<CleanupItem> PrivacyItems => CleanupItems.Where(c =>
        c.Category.Contains("Browser", System.StringComparison.OrdinalIgnoreCase) ||
        c.Category.Contains("Privacy", System.StringComparison.OrdinalIgnoreCase) ||
        c.Category.Contains("Cookie", System.StringComparison.OrdinalIgnoreCase) ||
        c.Category.Contains("History", System.StringComparison.OrdinalIgnoreCase));
    public bool HasPrivacyItems => PrivacyItems.Any();
    public long TotalPrivacySize => PrivacyItems.Where(c => c.IsSelected).Sum(c => c.Size);

    public IRelayCommand ScanCleanupCommand { get; }
    public IRelayCommand CleanCommand { get; }
    public IRelayCommand AnalyzeFolderCommand { get; }
    public IRelayCommand FindLargeFilesCommand { get; }
    public IRelayCommand FindDuplicatesCommand { get; }
    public IRelayCommand FindOldFilesCommand { get; }
    public IRelayCommand CreateBackupCommand { get; }
    public IRelayCommand RefreshBackupsCommand { get; }
    public IRelayCommand<BackupEntry> RestoreBackupCommand { get; }
    public IRelayCommand CleanShadersCommand { get; }
    public IRelayCommand BackupSavesCommand { get; }
    public IRelayCommand SelectAllCommand { get; }
    public IRelayCommand DeselectAllCommand { get; }
    public IRelayCommand EmptyRecycleBinCommand { get; }
    public IRelayCommand<string> SelectTabCommand { get; }
    public IRelayCommand SelectAllPrivacyCommand { get; }
    public IRelayCommand DeselectAllPrivacyCommand { get; }

    public DiskCleanupViewModel()
    {
        ScanCleanupCommand = new AsyncRelayCommand(ScanCleanup);
        CleanCommand = new AsyncRelayCommand(Clean);
        AnalyzeFolderCommand = new AsyncRelayCommand(AnalyzeFolders);
        FindLargeFilesCommand = new AsyncRelayCommand(ScanLargeFiles);
        FindDuplicatesCommand = new AsyncRelayCommand(ScanDuplicates);
        FindOldFilesCommand = new AsyncRelayCommand(ScanOldFiles);
        CreateBackupCommand = new RelayCommand(() => { new BackupService().CreateBackup(auto: false); RefreshBackups(); StatusText = "Manual backup created"; });
        RefreshBackupsCommand = new RelayCommand(RefreshBackups);
        RestoreBackupCommand = new RelayCommand<BackupEntry>(b => { if (b != null) { new BackupService().Restore(b); StatusText = $"Restored: {b.Name}"; } });
        CleanShadersCommand = new RelayCommand(() => StatusText = GameMaintenanceService.CleanShaderCaches());
        BackupSavesCommand = new RelayCommand(() =>
        {
            if (string.IsNullOrWhiteSpace(SavesDir)) { StatusText = "Enter saves folder"; return; }
            var root = string.IsNullOrWhiteSpace(SavesBackupRoot)
                ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SystemGuard_Saves")
                : SavesBackupRoot.Trim();
            StatusText = GameMaintenanceService.BackupSaves(SavesDir.Trim().Trim('"'), root);
        });
        SelectAllCommand = new RelayCommand(() => SetAllSelected(true));
        DeselectAllCommand = new RelayCommand(() => SetAllSelected(false));
        EmptyRecycleBinCommand = new RelayCommand(() =>
            StatusText = AdvancedCleanupService.EmptyRecycleBin());
        SelectTabCommand = new RelayCommand<string>(SelectTab);
        SelectAllPrivacyCommand = new RelayCommand(() => SetPrivacySelected(true));
        DeselectAllPrivacyCommand = new RelayCommand(() => SetPrivacySelected(false));
        Disks = new ObservableCollection<DiskInfoModel>(_diskService.GetDrives());
        RefreshBackups();
    }

    public override void OnActivated()
    {
        base.OnActivated();
        RefreshBackups();
    }

    private void RefreshBackups() =>
        Backups = new ObservableCollection<BackupEntry>(new BackupService().ListBackups());

    private void SelectTab(string? tab)
    {
        if (string.IsNullOrEmpty(tab)) return;
        SelectedSection = tab;
        IsCleanupTab = tab == "Cleanup";
        IsToolsTab = tab == "Tools";
        IsPrivacyTab = tab == "Privacy";
        IsUninstallTab = tab == "Uninstall";
    }

    partial void OnCleanupItemsChanged(ObservableCollection<CleanupItem> value)
    {
        SubscribeCleanupItems();
        OnPropertyChanged(nameof(HasCleanupItems));
        OnPropertyChanged(nameof(TotalCleanupSize));
        OnPropertyChanged(nameof(PrivacyItems));
        OnPropertyChanged(nameof(HasPrivacyItems));
        OnPropertyChanged(nameof(TotalPrivacySize));
    }

    partial void OnLargeFoldersChanged(ObservableCollection<FolderSizeInfo> value) => OnPropertyChanged(nameof(HasLargeFolders));
    partial void OnLargeFilesChanged(ObservableCollection<LargeFileInfo> value) => OnPropertyChanged(nameof(HasLargeFiles));

    partial void OnDuplicatesChanged(ObservableCollection<DuplicateFileGroup> value)
    {
        OnPropertyChanged(nameof(HasDuplicates));
        OnPropertyChanged(nameof(TotalDuplicateWaste));
    }

    private async Task ScanCleanup()
    {
        if (IsScanning) return;
        IsScanning = true; StatusText = "Scanning..."; Progress = 0;
        try
        {
            var items = await Task.Run(() => _cleanupService.GetCleanupItems());
            CleanupItems = new ObservableCollection<CleanupItem>(items);
            Progress = 100;
            StatusText = items.Count > 0 ? $"Found {items.Count} items" : "Nothing to clean";
        }
        catch (Exception ex) { StatusText = $"Scan failed: {ex.Message}"; }
        finally { IsScanning = false; }
    }

    private async Task Clean()
    {
        if (!CleanupItems.Any(i => i.IsSelected)) { StatusText = "Nothing selected"; return; }
        IsCleaning = true; StatusText = "Cleaning..."; Progress = 0;
        await Task.Run(() =>
        {
            foreach (var item in CleanupItems.Where(i => i.IsSelected))
            {
                try
                {
                    if (SelfProtection.IsProtectedPath(item.Path))
                    {
                        StatusText = $"Skipped protected path: {item.Name}";
                        continue;
                    }
                    if (System.IO.Directory.Exists(item.Path))
                        SelfProtection.SafeDeleteDirectory(item.Path, true);
                    else if (System.IO.File.Exists(item.Path))
                        SelfProtection.SafeDeleteFile(item.Path);
                }
                catch { }
            }
        });
        Progress = 100; StatusText = "Cleaned"; IsCleaning = false;
        await ScanCleanup();
    }

    private async Task AnalyzeFolders()
    {
        if (IsScanning) return;
        IsScanning = true;
        StatusText = "Analyzing folders…";
        try
        {
            LargeFolders = new ObservableCollection<FolderSizeInfo>(await _diskService.AnalyzeFolderSizes("C:\\"));
            StatusText = $"{LargeFolders.Count} folders";
        }
        catch (Exception ex) { StatusText = $"Analyze failed: {ex.Message}"; }
        finally { IsScanning = false; }
    }

    private async Task ScanLargeFiles()
    {
        if (IsScanning) return;
        IsScanning = true;
        StatusText = "Searching large files on C:\\ …";
        try
        {
            LargeFiles = new ObservableCollection<LargeFileInfo>(await _diskService.FindLargeFiles("C:\\"));
            StatusText = LargeFiles.Count > 0 ? $"{LargeFiles.Count} large files" : "No large files found on C:\\";
        }
        catch (Exception ex) { StatusText = $"Search failed: {ex.Message}"; }
        finally { IsScanning = false; }
    }

    private async Task ScanDuplicates()
    {
        if (IsScanning) return;
        IsScanning = true;
        StatusText = "Searching duplicates on C:\\ (may take a while)…";
        try
        {
            Duplicates = new ObservableCollection<DuplicateFileGroup>(await _diskService.FindDuplicates("C:\\"));
            StatusText = Duplicates.Count > 0
                ? $"{Duplicates.Count} duplicate groups, waste {TotalDuplicateWaste / 1048576} MB"
                : "No duplicates found on C:\\";
        }
        catch (Exception ex) { StatusText = $"Search failed: {ex.Message}"; }
        finally { IsScanning = false; }
    }

    // Старые файлы: не использовались N дней (реальный поиск по LastAccessTime)
    private async Task ScanOldFiles()
    {
        if (IsScanning) return;
        IsScanning = true;
        StatusText = $"Searching files older than {OldFileDays} days…";
        var days = Math.Clamp(OldFileDays, 1, 3650);
        var cutoff = DateTime.Now.AddDays(-days);
        try
        {
            var found = await Task.Run(() =>
            {
                var list = new List<LargeFileInfo>();
                try
                {
                    var root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Take(20000))
                    {
                        try
                        {
                            var fi = new FileInfo(file);
                            if (fi.LastAccessTime < cutoff && fi.Length > 1024 * 1024)
                                list.Add(new LargeFileInfo { Name = fi.Name, Path = fi.FullName, Size = fi.Length });
                            if (list.Count >= 200) break;
                        }
                        catch { }
                    }
                }
                catch { }
                return list.OrderByDescending(f => f.Size).ToList();
            });
            OldFiles = new ObservableCollection<LargeFileInfo>(found);
            OnPropertyChanged(nameof(HasOldFiles));
            StatusText = $"{found.Count} old files (>{days}d, capped at 200)";
        }
        catch (Exception ex) { StatusText = $"Search failed: {ex.Message}"; }
        finally { IsScanning = false; }
    }

    private void SetAllSelected(bool s)
    {
        foreach (var i in CleanupItems) i.IsSelected = s;
        OnPropertyChanged(nameof(TotalCleanupSize));
        OnPropertyChanged(nameof(TotalPrivacySize));
    }

    private void SetPrivacySelected(bool s)
    {
        foreach (var i in PrivacyItems) i.IsSelected = s;
        OnPropertyChanged(nameof(TotalCleanupSize));
        OnPropertyChanged(nameof(TotalPrivacySize));
    }

    private void SubscribeCleanupItems()
    {
        foreach (var i in CleanupItems)
            i.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CleanupItem.IsSelected))
                {
                    OnPropertyChanged(nameof(TotalCleanupSize));
                    OnPropertyChanged(nameof(TotalPrivacySize));
                }
            };
    }
}