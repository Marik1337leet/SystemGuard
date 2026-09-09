using Avalonia.Controls;
using Avalonia.VisualTree;
using SystemGuard.Desktop.ViewModels;
using System;

namespace SystemGuard.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += (_, _) => (DataContext as MainWindowViewModel)?.Shutdown();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is not MainWindowViewModel vm) return;

        // Подписываемся на смену выбранного пункта меню
        // чтобы добавлять/убирать CSS-класс "active" на кнопку
        foreach (var item in vm.MenuItems)
        {
            item.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MenuItemViewModel.IsSelected))
                    SyncActiveClass();
            };
        }

        SyncActiveClass();
    }

    private void SyncActiveClass()
    {
        // Находим ItemsControl по имени и перебираем все кнопки внутри
        var nav = this.FindControl<ItemsControl>("NavItems");
        if (nav == null) return;

        foreach (var desc in nav.GetVisualDescendants())
        {
            if (desc is not Button btn) continue;

            // DataContext кнопки — MenuItemViewModel (через DataTemplate)
            if (btn.DataContext is not MenuItemViewModel mi) continue;

            if (mi.IsSelected)
                btn.Classes.Add("active");
            else
                btn.Classes.Remove("active");
        }
    }
}
