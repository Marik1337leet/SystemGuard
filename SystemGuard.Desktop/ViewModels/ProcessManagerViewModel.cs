using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using SystemGuard.Desktop.Models;
using SystemGuard.Desktop.Services;

namespace SystemGuard.Desktop.ViewModels;

public partial class ProcessManagerViewModel : ViewModelBase
{
    private readonly AdvancedProcessService _processService;

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private ObservableCollection<ProcessModel> _processes = new();
    [ObservableProperty] private ProcessModel? _selectedProcess;
    [ObservableProperty] private bool _isTreeView = true;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _processFilter = "All";
    [ObservableProperty] private int _autoRefreshSeconds = 3;
    [ObservableProperty] private bool _autoRefreshEnabled;
    [ObservableProperty] private string _affinityMask = "";
    [ObservableProperty] private string _dllPath = "";

    public string[] ProcessFilters => new[] { "All", "User", "System" };
    public string[] PriorityLevels => new[] { "RealTime", "High", "AboveNormal", "Normal", "BelowNormal", "Idle" };

    private System.Timers.Timer? _autoTimer;
    private int _refreshing; // защита от перекрытия тяжёлых WMI-опросов

    private List<ProcessModel> _allProcesses = new();

    public RelayCommand RefreshCommand { get; }
    public RelayCommand KillCommand { get; }
    public RelayCommand KillForceCommand { get; }
    public RelayCommand KillTreeCommand { get; }
    public RelayCommand PriorityHighCommand { get; }
    public RelayCommand PriorityNormalCommand { get; }
    public IRelayCommand<string> SetPriorityCommand { get; }
    public IRelayCommand ApplyAffinityCommand { get; }
    public IRelayCommand InjectDllCommand { get; }
    public RelayCommand SuspendCommand { get; }
    public RelayCommand ResumeCommand { get; }
    public RelayCommand DumpCommand { get; }
    public RelayCommand OpenLocationCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand OpenRegistryCommand { get; }
    public RelayCommand CheckVirusTotalCommand { get; }

    public ProcessManagerViewModel()
    {
        _processService = new AdvancedProcessService();
        RefreshCommand = new RelayCommand(RefreshProcesses);
        KillCommand = new RelayCommand(() => KillProcess(false));
        KillForceCommand = new RelayCommand(() => KillProcess(true));
        KillTreeCommand = new RelayCommand(KillTree);
        PriorityHighCommand = new RelayCommand(() => SetPriority(ProcessPriorityClass.High));
        PriorityNormalCommand = new RelayCommand(() => SetPriority(ProcessPriorityClass.Normal));
        SetPriorityCommand = new RelayCommand<string>(p => { if (Enum.TryParse<ProcessPriorityClass>(p, out var pr)) SetPriority(pr); });
        ApplyAffinityCommand = new RelayCommand(ApplyAffinity);
        InjectDllCommand = new RelayCommand(InjectDll);
        SuspendCommand = new RelayCommand(SuspendProcess);
        ResumeCommand = new RelayCommand(ResumeProcess);
        DumpCommand = new RelayCommand(CreateDump);
        OpenLocationCommand = new RelayCommand(OpenFileLocation);
        OpenFolderCommand = new RelayCommand(OpenContainingFolder);
        OpenRegistryCommand = new RelayCommand(() => StatusText = AdvancedProcessService.OpenStartupRegistryKey());
        CheckVirusTotalCommand = new RelayCommand(CheckVirusTotal);
        RefreshProcesses();
    }

    public void RefreshProcesses()
    {
        // WMI-опрос ~0.5-2с: не копим параллельные проходы, иначе UI встанет в очередь
        if (System.Threading.Interlocked.Exchange(ref _refreshing, 1) != 0) return;
        IsLoading = true;
        Task.Run(() =>
        {
            List<ProcessModel> processes;
            try { processes = _processService.GetProcessTree(); }
            catch { System.Threading.Interlocked.Exchange(ref _refreshing, 0); return; }
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    _allProcesses = processes;
                    FilterProcesses();
                    StatusText = $"{_allProcesses.Count} processes";
                }
                finally
                {
                    IsLoading = false;
                    System.Threading.Interlocked.Exchange(ref _refreshing, 0);
                }
            });
        });
    }

    public void FilterProcesses()
    {
        IEnumerable<ProcessModel> query = _allProcesses;

        // Фильтр: All / User / System (эвристика по пути и пользователю)
        if (ProcessFilter == "System")
            query = query.Where(p => (p.FullPath?.StartsWith(@"C:\Windows", StringComparison.OrdinalIgnoreCase) ?? false)
                || string.Equals(p.User, "SYSTEM", StringComparison.OrdinalIgnoreCase));
        else if (ProcessFilter == "User")
            query = query.Where(p => !(p.FullPath?.StartsWith(@"C:\Windows", StringComparison.OrdinalIgnoreCase) ?? false));

        // Поиск по имени, PID, пути и компании
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var s = SearchText.ToLower();
            query = query.Where(p => p.Name.ToLower().Contains(s)
                || p.Pid.ToString().Contains(s)
                || (p.FullPath?.ToLower().Contains(s) ?? false)
                || (p.Company?.ToLower().Contains(s) ?? false));
        }

        Processes = new ObservableCollection<ProcessModel>(query);
    }

    partial void OnSearchTextChanged(string value) => FilterProcesses();
    partial void OnProcessFilterChanged(string value) => FilterProcesses();

    partial void OnAutoRefreshEnabledChanged(bool value)
    {
        _autoTimer?.Dispose();
        _autoTimer = null;
        if (!value) return;
        var sec = Math.Clamp(AutoRefreshSeconds, 5, 30);
        _autoTimer = new System.Timers.Timer(sec * 1000);
        _autoTimer.AutoReset = false;
        _autoTimer.Elapsed += (_, _) =>
        {
            try { RefreshProcesses(); }
            finally { try { _autoTimer?.Start(); } catch { } }
        };
        _autoTimer.Start();
        StatusText = $"Auto-refresh every {sec}s";
    }

    partial void OnAutoRefreshSecondsChanged(int value)
    {
        if (AutoRefreshEnabled)
        {
            OnAutoRefreshEnabledChanged(false);
            OnAutoRefreshEnabledChanged(true);
        }
    }

    private void KillProcess(bool force)
    {
        if (SelectedProcess == null) return;
        if (SelfProtection.IsProtectedProcessId(SelectedProcess.Pid))
        { StatusText = "System-protected process — refused"; return; }
        try { Process.GetProcessById(SelectedProcess.Pid).Kill(); }
        catch { if (force) _processService.KillProcessForce(SelectedProcess.Pid); }
        RefreshProcesses();
    }

    // Дерево: сам процесс + все потомки по ParentProcessId (WMI)
    private void KillTree()
    {
        if (SelectedProcess == null) return;
        if (SelfProtection.IsProtectedProcessId(SelectedProcess.Pid))
        { StatusText = "System-protected process — refused"; return; }
        int killed = 0;
        try
        {
            var children = new System.Collections.Generic.List<int>();
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT ProcessId,ParentProcessId FROM Win32_Process");
            var map = new System.Collections.Generic.Dictionary<int, int>();
            foreach (System.Management.ManagementObject o in searcher.Get())
            {
                try
                {
                    map[Convert.ToInt32(o["ProcessId"])] = Convert.ToInt32(o["ParentProcessId"]);
                }
                catch { }
            }
            var roots = new System.Collections.Generic.Queue<int>();
            roots.Enqueue(SelectedProcess.Pid);
            var targets = new System.Collections.Generic.List<int> { SelectedProcess.Pid };
            while (roots.Count > 0)
            {
                var parent = roots.Dequeue();
                foreach (var kv in map)
                    if (kv.Value == parent && !targets.Contains(kv.Key))
                    { targets.Add(kv.Key); roots.Enqueue(kv.Key); }
            }
            foreach (var pid in targets.OrderByDescending(p => p))
            {
                if (SelfProtection.IsProtectedProcessId(pid)) continue;
                try { Process.GetProcessById(pid).Kill(); killed++; }
                catch { }
            }
            StatusText = $"Terminated tree: {killed} processes";
        }
        catch (Exception ex) { StatusText = $"Kill tree failed: {ex.Message}"; }
        RefreshProcesses();
    }

    private void SetPriority(ProcessPriorityClass priority)
    {
        if (SelectedProcess == null) return;
        StatusText = ProcessControlService.SetPriority(SelectedProcess.Pid, priority);
        RefreshProcesses();
    }

    private void ApplyAffinity()
    {
        if (SelectedProcess == null) return;
        try
        {
            var mask = AffinityMask.Trim().StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToInt64(AffinityMask.Trim()[2..], 16)
                : Convert.ToInt64(AffinityMask.Trim());
            StatusText = ProcessControlService.SetAffinity(SelectedProcess.Pid, new IntPtr(mask));
        }
        catch { StatusText = "Affinity: enter mask like 0x3 or 3"; }
    }

    private void InjectDll()
    {
        if (SelectedProcess == null) return;
        if (string.IsNullOrWhiteSpace(DllPath)) { StatusText = "Select DLL path first"; return; }
        StatusText = ProcessControlService.InjectDll(SelectedProcess.Pid, DllPath.Trim().Trim('"'));
    }

    private void SuspendProcess()
    {
        if (SelectedProcess == null) return;
        StatusText = ProcessControlService.Suspend(SelectedProcess.Pid);
    }

    private void ResumeProcess()
    {
        if (SelectedProcess == null) return;
        StatusText = ProcessControlService.Resume(SelectedProcess.Pid);
    }

    private void CreateDump()
    {
        if (SelectedProcess == null) return;
        var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), $"dump_{SelectedProcess.Name}_{SelectedProcess.Pid}.dmp");
        _processService.CreateDump(SelectedProcess.Pid, path);
        StatusText = $"Dump: {path}";
    }

    private void OpenFileLocation()
    {
        if (SelectedProcess == null || string.IsNullOrEmpty(SelectedProcess.FullPath)) return;
        StatusText = AdvancedProcessService.OpenContainingFolder(SelectedProcess.FullPath);
    }

    private void OpenContainingFolder() => OpenFileLocation();

    private void CheckVirusTotal()
    {
        if (SelectedProcess == null || string.IsNullOrEmpty(SelectedProcess.FullPath)) return;
        var sha256 = _processService.CalculateSHA256(SelectedProcess.FullPath);
        var hash = !string.IsNullOrEmpty(sha256) ? sha256 : _processService.CalculateMD5(SelectedProcess.FullPath);
        if (string.IsNullOrEmpty(hash)) { StatusText = "Cannot hash file"; return; }
        StatusText = $"SHA256: {hash[..Math.Min(16, hash.Length)]}… → VirusTotal";
        try { Process.Start(new ProcessStartInfo { FileName = $"https://www.virustotal.com/gui/file/{hash}", UseShellExecute = true }); } catch (Exception ex) { StatusText = ex.Message; }
    }
}