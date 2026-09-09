using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.ObjectModel;

namespace SystemGuard.Desktop.Models;

public partial class ProcessModel : ObservableObject
{
    [ObservableProperty] private int _pid;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _fullPath = "";
    [ObservableProperty] private double _cpuUsage;
    [ObservableProperty] private double _memoryMB;
    [ObservableProperty] private int _threadCount;
    [ObservableProperty] private int _handleCount;
    [ObservableProperty] private string _commandLine = "";
    [ObservableProperty] private string _user = "";
    [ObservableProperty] private DateTime _startTime;
    [ObservableProperty] private bool _isResponding;
    [ObservableProperty] private bool _isSuspended;
    [ObservableProperty] private string _priority = "Normal";
    [ObservableProperty] private string _company = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private int _parentPid;
    [ObservableProperty] private ObservableCollection<ProcessModel> _children = new();
    [ObservableProperty] private int _indentLevel;
}