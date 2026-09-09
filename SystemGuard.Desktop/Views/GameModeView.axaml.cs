using Avalonia.Controls;
using Avalonia.Interactivity;
using SystemGuard.Desktop.ViewModels;

namespace SystemGuard.Desktop.Views;

public partial class GameModeView : UserControl
{
    public GameModeView()
    {
        InitializeComponent();
    }

    private void AddKeepProcess_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GameModeViewModel vm && vm.SelectedProfile != null && !string.IsNullOrWhiteSpace(vm.NewProcessToKeep))
        {
            if (vm.AddProcessToKeepCommand.CanExecute(null))
                vm.AddProcessToKeepCommand.Execute(null);
        }
    }

    private void RemoveKeepProcess_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string process && DataContext is GameModeViewModel vm && vm.SelectedProfile != null)
        {
            if (vm.RemoveProcessToKeepCommand.CanExecute(process))
                vm.RemoveProcessToKeepCommand.Execute(process);
        }
    }

    private void AddKillProcess_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GameModeViewModel vm && vm.SelectedProfile != null && !string.IsNullOrWhiteSpace(vm.NewProcessToKill))
        {
            if (vm.AddProcessToKillCommand.CanExecute(null))
                vm.AddProcessToKillCommand.Execute(null);
        }
    }

    private void RemoveKillProcess_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string process && DataContext is GameModeViewModel vm && vm.SelectedProfile != null)
        {
            if (vm.RemoveProcessToKillCommand.CanExecute(process))
                vm.RemoveProcessToKillCommand.Execute(process);
        }
    }
}