using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SystemGuard.Desktop.ViewModels;

namespace SystemGuard.Desktop.Views;

public partial class DiskCleanupView : UserControl
{
    // Капля фиксированной ширины 132px: позиции 0 / 132 / 264 / 396.
    // Плавное перетекание — ThicknessTransition на Margin в XAML.
    public DiskCleanupView() => InitializeComponent();

    private void MoveDroplet(int index)
    {
        var indicator = this.FindControl<Border>("TabIndicator");
        if (indicator != null)
            indicator.Margin = new Thickness(index * 132, 0, 0, 0);
    }

    private void SelectSection(string section, int index)
    {
        MoveDroplet(index);
        if (DataContext is DiskCleanupViewModel vm && vm.SelectTabCommand.CanExecute(section))
            vm.SelectTabCommand.Execute(section);
    }

    private void CleanupTab_Click(object? sender, RoutedEventArgs e) => SelectSection("Cleanup", 0);
    private void ToolsTab_Click(object? sender, RoutedEventArgs e) => SelectSection("Tools", 1);
    private void PrivacyTab_Click(object? sender, RoutedEventArgs e) => SelectSection("Privacy", 2);
    private void UninstallTab_Click(object? sender, RoutedEventArgs e) => SelectSection("Uninstall", 3);
}
