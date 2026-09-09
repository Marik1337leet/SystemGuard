using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SystemGuard.Desktop.ViewModels;

namespace SystemGuard.Desktop.Views;

public partial class StartupView : UserControl
{
    // Капля 132px: Startup / Services → 0 / 132.
    public StartupView() => InitializeComponent();

    private void MoveDroplet(int index)
    {
        var indicator = this.FindControl<Border>("TabIndicator");
        if (indicator != null)
            indicator.Margin = new Thickness(index * 132, 0, 0, 0);
    }

    private void SelectTab(string tab, int index)
    {
        MoveDroplet(index);
        if (DataContext is StartupViewModel vm && vm.SelectTabCommand.CanExecute(tab))
            vm.SelectTabCommand.Execute(tab);
    }

    private void StartupTab_Click(object? sender, RoutedEventArgs e) => SelectTab("Startup", 0);
    private void ServicesTab_Click(object? sender, RoutedEventArgs e) => SelectTab("Services", 1);
}
