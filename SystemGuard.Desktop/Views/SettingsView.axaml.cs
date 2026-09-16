using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SystemGuard.Desktop.ViewModels;

namespace SystemGuard.Desktop.Views;

public partial class SettingsView : UserControl
{
    // Капля 132px: General / Colors / Telegram / License → 0 / 132 / 264 / 396.
    public SettingsView() => InitializeComponent();

    private void MoveDroplet(int index)
    {
        var indicator = this.FindControl<Border>("TabIndicator");
        if (indicator != null)
            indicator.Margin = new Thickness(index * 132, 0, 0, 0);
    }

    private void SelectTab(string tab, int index)
    {
        MoveDroplet(index);
        if (DataContext is SettingsViewModel vm && vm.SelectTabCommand.CanExecute(tab))
            vm.SelectTabCommand.Execute(tab);
    }

    private void GeneralTab_Click(object? sender, RoutedEventArgs e) => SelectTab("General", 0);
    private void ColorsTab_Click(object? sender, RoutedEventArgs e) => SelectTab("Colors", 1);
    private void TelegramTab_Click(object? sender, RoutedEventArgs e) => SelectTab("Telegram", 2);
    private void LicenseTab_Click(object? sender, RoutedEventArgs e) => SelectTab("License", 3);
}
