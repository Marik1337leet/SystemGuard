using Avalonia.Data;
using Avalonia.Markup.Xaml;
using System;
using SystemGuard.Desktop.Services;

namespace SystemGuard.Desktop.Markup;

// Использование в XAML:
//   xmlns:loc="using:SystemGuard.Desktop.Markup"
//   Text="{loc:Loc Dashboard}"
// При смене языка (Settings → Language) все тексты обновляются сами,
// т.к. биндинг слушает LocalizationService.Instance[Key] (INotifyPropertyChanged).
public class LocExtension : MarkupExtension
{
    public string Key { get; set; } = "";

    public LocExtension() { }
    public LocExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]")
        {
            Source = LocalizationService.Instance,
            Mode = BindingMode.OneWay
        };
}
