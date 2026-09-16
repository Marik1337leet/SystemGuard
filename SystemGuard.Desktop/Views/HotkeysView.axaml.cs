using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SystemGuard.Desktop.Views;

public partial class HotkeysView : UserControl
{
    public HotkeysView()
    {
        InitializeComponent();
        // Захват комбо нажатием клавиш: поля с Tag="capture" сами вписывают
        // «Ctrl+Alt+O» при нажатии — руками печатать ничего не нужно.
        // Tunnel перехватывает до TextBox, чтобы символы не печатались в поле.
        AddHandler(KeyDownEvent, OnCaptureKeyDown, RoutingStrategies.Tunnel);
    }

    private static void OnCaptureKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is not TextBox box || box.Tag?.ToString() != "capture")
            return;

        // Backspace без модификаторов — очистить поле.
        if (e.Key is Key.Back or Key.Delete && e.KeyModifiers == KeyModifiers.None)
        {
            box.Text = "";
            e.Handled = true;
            return;
        }

        string? key = MapKey(e.Key);
        var mods = e.KeyModifiers;

        // Нажат только модификатор — показываем частичное комбо как подсказку.
        if (key == null)
        {
            var partial = BuildCombo(mods, null);
            if (!string.IsNullOrEmpty(partial))
            {
                box.Text = partial + "+…";
                e.Handled = true;
            }
            return;
        }

        // Глобальный хук требует хотя бы один модификатор — голые клавиши игнорим.
        if (mods == KeyModifiers.None)
        {
            e.Handled = true;
            return;
        }

        box.Text = BuildCombo(mods, key);
        e.Handled = true;
    }

    private static string BuildCombo(KeyModifiers mods, string? key)
    {
        var parts = new System.Collections.Generic.List<string>();
        if (mods.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (mods.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (mods.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
        if (!string.IsNullOrEmpty(key)) parts.Add(key);
        return string.Join("+", parts);
    }

    // Имена совпадают с GlobalHotkeyService.BuildCombo, иначе бинд не сработает.
    private static string? MapKey(Key key)
    {
        if (key >= Key.A && key <= Key.Z)
            return ((char)('A' + (key - Key.A))).ToString();
        if (key >= Key.D0 && key <= Key.D9)
            return ((char)('0' + (key - Key.D0))).ToString();
        if (key >= Key.NumPad0 && key <= Key.NumPad9)
            return ((char)('0' + (key - Key.NumPad0))).ToString();
        if (key >= Key.F1 && key <= Key.F12)
            return "F" + (key - Key.F1 + 1);
        return key switch
        {
            Key.Space => "Space",
            Key.Enter => "Enter",
            Key.Escape => "Esc",
            Key.Tab => "Tab",
            Key.Delete => "Delete",
            Key.Insert => "Insert",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PgUp",
            Key.PageDown => "PgDn",
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Left => "Left",
            Key.Right => "Right",
            // Только модификатор — не финальный ключ (см. подсказку выше).
            Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin => null,
            _ => null
        };
    }
}
