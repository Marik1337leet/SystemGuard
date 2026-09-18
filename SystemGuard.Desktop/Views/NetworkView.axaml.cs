using Avalonia.Controls;
using Avalonia.Interactivity;
using SystemGuard.Desktop.ViewModels;

namespace SystemGuard.Desktop.Views;

public partial class NetworkView : UserControl
{
    // Капля едет сама: Margin TabIndicator привязан в XAML к SelectedTab
    // через SectionToTabMarginConverter (Adapters / Firewall / Tools).
    // Traffic удалён (дубль мониторинга).
    public NetworkView() => InitializeComponent();

    private void SelectTab(string tab)
    {
        if (DataContext is NetworkViewModel vm && vm.SelectTabCommand.CanExecute(tab))
            vm.SelectTabCommand.Execute(tab);
    }

    private void AdaptersTab_Click(object? sender, RoutedEventArgs e) => SelectTab("Adapters");
    private void FirewallTab_Click(object? sender, RoutedEventArgs e) => SelectTab("Firewall");
    private void ToolsTab_Click(object? sender, RoutedEventArgs e) => SelectTab("Tools");
}
