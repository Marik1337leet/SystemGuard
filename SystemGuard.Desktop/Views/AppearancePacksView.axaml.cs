using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SystemGuard.Desktop.ViewModels;

namespace SystemGuard.Desktop.Views;

public partial class AppearancePacksView : UserControl
{
    // Капля 132px: Style / Taskbar / Dock / Widgets → 0 / 132 / 264 / 396.
    public AppearancePacksView() => InitializeComponent();

    private void MoveDroplet(int index)
    {
        var indicator = this.FindControl<Border>("TabIndicator");
        if (indicator != null)
            indicator.Margin = new Thickness(index * 132, 0, 0, 0);
    }

    private void SelectSection(string section, int index)
    {
        MoveDroplet(index);
        if (DataContext is AppearancePacksViewModel vm && vm.SelectTabCommand.CanExecute(section))
            vm.SelectTabCommand.Execute(section);
    }

    private void StyleTab_Click(object? sender, RoutedEventArgs e) => SelectSection("Style", 0);
    private void TaskbarTab_Click(object? sender, RoutedEventArgs e) => SelectSection("Taskbar", 1);
    private void DockTab_Click(object? sender, RoutedEventArgs e) => SelectSection("Dock", 2);
    private void WidgetsTab_Click(object? sender, RoutedEventArgs e) => SelectSection("Widgets", 3);
}
