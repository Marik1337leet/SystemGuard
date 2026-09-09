using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;

namespace SystemGuard.Desktop.ViewModels;

public partial class MenuItemViewModel : ObservableObject
{
    [ObservableProperty] private string _icon = "";
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _viewType = "";
    [ObservableProperty] private bool _isSelected;

    public Action<string>? NavigationAction { get; set; }

    [RelayCommand]
    private void Navigate() => NavigationAction?.Invoke(ViewType);
}