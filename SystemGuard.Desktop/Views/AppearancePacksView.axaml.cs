using Avalonia.Controls;
using Avalonia.Interactivity;
using SystemGuard.Desktop.ViewModels;

namespace SystemGuard.Desktop.Views;

public partial class AppearancePacksView : UserControl
{
    // Капля едет сама: Margin TabIndicator привязан в XAML к SelectedSection
    // через SectionToTabMarginConverter (Style / Taskbar / Dock / Widgets).
    public AppearancePacksView() => InitializeComponent();

    private void SelectSection(string section)
    {
        if (DataContext is AppearancePacksViewModel vm && vm.SelectTabCommand.CanExecute(section))
            vm.SelectTabCommand.Execute(section);
    }

    private void StyleTab_Click(object? sender, RoutedEventArgs e) => SelectSection("Style");
    private void TaskbarTab_Click(object? sender, RoutedEventArgs e) => SelectSection("Taskbar");
    private void DockTab_Click(object? sender, RoutedEventArgs e) => SelectSection("Dock");
    private void WidgetsTab_Click(object? sender, RoutedEventArgs e) => SelectSection("Widgets");
}
