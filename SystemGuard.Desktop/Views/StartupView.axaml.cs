using Avalonia.Controls;
using Avalonia.Interactivity;
using SystemGuard.Desktop.ViewModels;

namespace SystemGuard.Desktop.Views;

public partial class StartupView : UserControl
{
    // Капля едет сама: Margin TabIndicator привязан в XAML к SelectedTab
    // через SectionToTabMarginConverter (Startup / Services).
    public StartupView() => InitializeComponent();

    private void SelectTab(string tab)
    {
        if (DataContext is StartupViewModel vm && vm.SelectTabCommand.CanExecute(tab))
            vm.SelectTabCommand.Execute(tab);
    }

    private void StartupTab_Click(object? sender, RoutedEventArgs e) => SelectTab("Startup");
    private void ServicesTab_Click(object? sender, RoutedEventArgs e) => SelectTab("Services");
}
