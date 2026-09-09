using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SystemGuard.Desktop.ViewModels;

namespace SystemGuard.Desktop.Views;

public partial class NetworkView : UserControl
{
    // Капля 132px: Adapters / Traffic / Firewall / Tools → 0 / 132 / 264 / 396.
    public NetworkView() => InitializeComponent();

    private void MoveDroplet(int index)
    {
        var indicator = this.FindControl<Border>("TabIndicator");
        if (indicator != null)
            indicator.Margin = new Thickness(index * 132, 0, 0, 0);
    }

    private void SelectTab(string tab, int index)
    {
        MoveDroplet(index);
        if (DataContext is NetworkViewModel vm && vm.SelectTabCommand.CanExecute(tab))
            vm.SelectTabCommand.Execute(tab);
    }

    private void AdaptersTab_Click(object? sender, RoutedEventArgs e) => SelectTab("Adapters", 0);
    private void TrafficTab_Click(object? sender, RoutedEventArgs e) => SelectTab("Traffic", 1);
    private void FirewallTab_Click(object? sender, RoutedEventArgs e) => SelectTab("Firewall", 2);
    private void ToolsTab_Click(object? sender, RoutedEventArgs e) => SelectTab("Tools", 3);
}
