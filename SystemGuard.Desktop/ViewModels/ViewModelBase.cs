using CommunityToolkit.Mvvm.ComponentModel;

namespace SystemGuard.Desktop.ViewModels;

public partial class ViewModelBase : ObservableObject
{
    [ObservableProperty] private bool _isActive;

    public virtual void OnActivated() { }
    public virtual void OnDeactivated() { }
}