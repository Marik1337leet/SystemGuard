using CommunityToolkit.Mvvm.ComponentModel;

namespace SystemGuard.Desktop.Models;

public partial class StorageDriveViewModel : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private double _temperature;
    [ObservableProperty] private double _usedSpace;
    [ObservableProperty] private double _totalSpace;
    [ObservableProperty] private double _readSpeed;
    [ObservableProperty] private double _writeSpeed;
    [ObservableProperty] private double _usagePercent;
}