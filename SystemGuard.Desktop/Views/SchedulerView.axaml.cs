using Avalonia.Controls;
using Avalonia.Interactivity;
using SystemGuard.Desktop.ViewModels;

namespace SystemGuard.Desktop.Views;

public partial class SchedulerView : UserControl
{
    public SchedulerView() => InitializeComponent();

    private void ToggleTask_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id && DataContext is SchedulerViewModel vm)
            vm.ToggleTaskCommand.Execute(id);
    }

    private void RemoveTask_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id && DataContext is SchedulerViewModel vm)
            vm.RemoveTaskCommand.Execute(id);
    }
}