using CommunityToolkit.Mvvm.ComponentModel;

namespace SystemGuard.Desktop.ViewModels;

public partial class ViewModelBase : ObservableObject
{
    [ObservableProperty] private bool _isActive;

    /// <summary>
    /// Pro-гейт для XAML: true = полный доступ (Pro/Enterprise/активный Trial).
    /// Обновляется при каждой навигации из MainWindowViewModel.NavigateTo
    /// и мгновенно по событию LicenseGate.Changed (активация/истечение).
    /// Использование: IsEnabled="{Binding IsProLicense}" + LockBadge IsVisible="{Binding !IsProLicense}".
    /// </summary>
    [ObservableProperty] private bool _isProLicense;

    public virtual void RefreshLicenseGate()
    {
        try { IsProLicense = Services.LicenseGate.IsPro(); }
        catch { IsProLicense = false; }
    }

    public virtual void OnActivated() { }
    public virtual void OnDeactivated() { }
}