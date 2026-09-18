using Avalonia.Controls;
using Avalonia.Interactivity;
using SystemGuard.Desktop.ViewModels;

namespace SystemGuard.Desktop.Views;

public partial class SettingsView : UserControl
{
    // Капля 132px едет сама: Margin TabIndicator привязан в XAML к
    // SelectedSection через SectionToTabMarginConverter (General / Colors / Telegram / License).
    // Так капля всегда на запомненном табе: View пересоздаётся при навигации,
    // а VM переиспользуется. Клик только переключает таб в VM.
    public SettingsView() => InitializeComponent();

    private void SelectTab(string tab)
    {
        if (DataContext is SettingsViewModel vm && vm.SelectTabCommand.CanExecute(tab))
            vm.SelectTabCommand.Execute(tab);
    }

    private void GeneralTab_Click(object? sender, RoutedEventArgs e) => SelectTab("General");
    private void ColorsTab_Click(object? sender, RoutedEventArgs e) => SelectTab("Colors");
    private void TelegramTab_Click(object? sender, RoutedEventArgs e) => SelectTab("Telegram");
    private void LicenseTab_Click(object? sender, RoutedEventArgs e) => SelectTab("License");
}
