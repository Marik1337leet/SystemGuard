using Avalonia.Controls;
using Avalonia.Interactivity;
using SystemGuard.Desktop.ViewModels;

namespace SystemGuard.Desktop.Views;

public partial class DiskCleanupView : UserControl
{
    // Капля едет сама: Margin TabIndicator привязан в XAML к SelectedSection
    // через SectionToTabMarginConverter (Cleanup / Tools / Privacy / Uninstall).
    // Плавное перетекание — ThicknessTransition на Margin в XAML.
    public DiskCleanupView() => InitializeComponent();

    private void SelectSection(string section)
    {
        if (DataContext is DiskCleanupViewModel vm && vm.SelectTabCommand.CanExecute(section))
            vm.SelectTabCommand.Execute(section);
    }

    private void CleanupTab_Click(object? sender, RoutedEventArgs e) => SelectSection("Cleanup");
    private void ToolsTab_Click(object? sender, RoutedEventArgs e) => SelectSection("Tools");
    private void PrivacyTab_Click(object? sender, RoutedEventArgs e) => SelectSection("Privacy");
    private void UninstallTab_Click(object? sender, RoutedEventArgs e) => SelectSection("Uninstall");
}
