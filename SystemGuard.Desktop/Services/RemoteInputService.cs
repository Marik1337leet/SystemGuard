using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SystemGuard.Desktop.Services;

// Ввод пароля на экране блокировки + пробуждение экрана.
// SendInput инжектит нажатия на низком уровне — они доходят и до
// Winlogon-экрана (lock screen), в отличие от SendKeys.
// ВНИМАНИЕ: пароль идёт по сети — только через свой HTTPS-туннель + токен.
// После использования при утере токена смените пароль Windows.
public static class RemoteInputService
{
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy, mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);
    private const uint ES_DISPLAY_REQUIRED = 0x00000002;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;

    public static void WakeDisplay()
    {
        try
        {
            SetThreadExecutionState(ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED);
            // Лёгкое движение мыши будит монитор из сна
            mouse_event(MOUSEEVENTF_MOVE, 1, 0, 0, UIntPtr.Zero);
            Thread.Sleep(120);
            mouse_event(MOUSEEVENTF_MOVE, 0, 1, 0, UIntPtr.Zero);
            try { SetCursorPos(960, 540); } catch { }
        }
        catch { }
    }

    private static void TypeUnicode(char c)
    {
        var down = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE }
            }
        };
        var up = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP }
            }
        };
        SendInput(1, new[] { down }, Marshal.SizeOf<INPUT>());
        Thread.Sleep(8);
        SendInput(1, new[] { up }, Marshal.SizeOf<INPUT>());
        Thread.Sleep(8);
    }

    private static void PressVk(ushort vk)
    {
        var down = new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk } } };
        var up = new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } } };
        SendInput(1, new[] { down }, Marshal.SizeOf<INPUT>());
        Thread.Sleep(30);
        SendInput(1, new[] { up }, Marshal.SizeOf<INPUT>());
        Thread.Sleep(30);
    }

    // ── Удалённое управление (WebApp → туннель → сюда, всё под live-токеном) ──

    /// <summary>
    /// Свич управления мышью из WebApp (вкладка Пульт).
    /// Выключен — все mouse_*/scroll команды отклоняются (и из бота тоже нет,
    /// бот мышь не дёргает; клавиатура и остальные действия работают).
    /// По умолчанию включён (прежнее поведение).
    /// </summary>
    public static bool MouseEnabled { get; set; } = true;

    public const string MouseOffMessage = "Mouse control is off — включите свич «Мышь» в WebApp";

    private static string? DenyMouseIfOff() => MouseEnabled ? null : MouseOffMessage;

    /// <summary>
    /// Свич управления клавиатурой из WebApp (вкладка Пульт, мастер-свич ввода).
    /// Выключен — key/type отклоняются. Ввод пароля (unlock) НЕ гейтится:
    /// это отдельный сценарий с подтверждением, а не свободная печать.
    /// По умолчанию включён (прежнее поведение).
    /// </summary>
    public static bool KeyboardEnabled { get; set; } = true;

    public const string KbdOffMessage = "Keyboard control is off — включите ввод в WebApp";

    private static string? DenyKbdIfOff() => KeyboardEnabled ? null : KbdOffMessage;

    // Относительное движение (тачпад в WebApp): dx,dy в пикселях.
    public static string MoveRelative(int dx, int dy)
    {
        var deny = DenyMouseIfOff();
        if (deny != null) return deny;
        try
        {
            dx = Math.Clamp(dx, -2000, 2000);
            dy = Math.Clamp(dy, -2000, 2000);
            if (dx == 0 && dy == 0) return "OK";
            mouse_event(MOUSEEVENTF_MOVE, (uint)dx, (uint)dy, 0, UIntPtr.Zero);
            return "OK";
        }
        catch (Exception ex) { return "Mouse failed: " + ex.Message; }
    }

    // Абсолютная позиция долями виртуального экрана 0..1 (тап по скриншоту).
    // Мультимониторность учитывается: координаты от левого верхнего угла всех экранов.
    public static string MoveToFraction(double fx, double fy)
    {
        var deny = DenyMouseIfOff();
        if (deny != null) return deny;
        try
        {
            fx = Math.Clamp(fx, 0, 1);
            fy = Math.Clamp(fy, 0, 1);
            int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            if (vw <= 0 || vh <= 0) return "No screen metrics";
            SetCursorPos(vx + (int)(fx * vw), vy + (int)(fy * vh));
            return "OK";
        }
        catch (Exception ex) { return "Mouse failed: " + ex.Message; }
    }

    // Клик: left | right | middle | double (по умолчанию left).
    public static string Click(string button)
    {
        var deny = DenyMouseIfOff();
        if (deny != null) return deny;
        try
        {
            var b = (button ?? "left").Trim().ToLowerInvariant();
            if (b is "double" or "2x" or "doubleclick")
            {
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(60);
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                return "Double-click";
            }
            uint down = b switch
            {
                "right" or "r" => MOUSEEVENTF_RIGHTDOWN,
                "middle" or "m" => MOUSEEVENTF_MIDDLEDOWN,
                _ => MOUSEEVENTF_LEFTDOWN
            };
            uint up = b switch
            {
                "right" or "r" => MOUSEEVENTF_RIGHTUP,
                "middle" or "m" => MOUSEEVENTF_MIDDLEUP,
                _ => MOUSEEVENTF_LEFTUP
            };
            mouse_event(down, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(40);
            mouse_event(up, 0, 0, 0, UIntPtr.Zero);
            return b switch
            {
                "right" or "r" => "Right-click",
                "middle" or "m" => "Middle-click",
                _ => "Click"
            };
        }
        catch (Exception ex) { return "Click failed: " + ex.Message; }
    }

    // Колесо: положительное — вверх. 1 клик = 120 единиц.
    public static string Scroll(int clicks)
    {
        var deny = DenyMouseIfOff();
        if (deny != null) return deny;
        try
        {
            clicks = Math.Clamp(clicks, -20, 20);
            if (clicks == 0) return "OK";
            mouse_event(MOUSEEVENTF_WHEEL, 0, 0, (uint)(clicks * 120), UIntPtr.Zero);
            return "OK";
        }
        catch (Exception ex) { return "Scroll failed: " + ex.Message; }
    }

    // Именованные клавиши и комбо: enter, esc, tab, backspace, delete, space,
    // up/down/left/right, home, end, pgup, pgdn, f1..f12, win, alttab, altf4,
    // ctrl+c/v/x/z/a/s, win+d, win+l, win+r, printscreen.
    public static string PressKey(string name)
    {
        var deny = DenyKbdIfOff();
        if (deny != null) return deny;
        try
        {
            var k = (name ?? "").Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "");
            if (k.Length == 0) return "Empty key";
            if (SingleKeys.TryGetValue(k, out var vk))
            {
                PressVk(vk);
                return "Key: " + k;
            }
            if (ComboKeys.TryGetValue(k, out var combo))
            {
                PressCombo(combo);
                return "Combo: " + k;
            }
            return "Unknown key: " + name + " (try: enter, esc, tab, arrows, alt+tab, ctrl+c)";
        }
        catch (Exception ex) { return "Key failed: " + ex.Message; }
    }

    private static readonly System.Collections.Generic.Dictionary<string, ushort> SingleKeys = new()
    {
        ["enter"] = 0x0D, ["return"] = 0x0D, ["esc"] = 0x1B, ["escape"] = 0x1B,
        ["tab"] = 0x09, ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E,
        ["space"] = 0x20, ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27,
        ["home"] = 0x24, ["end"] = 0x23, ["pgup"] = 0x21, ["pgdn"] = 0x22, ["pageup"] = 0x21,
        ["pagedown"] = 0x22, ["insert"] = 0x2D, ["printscreen"] = 0x2C,
        ["f1"] = 0x70, ["f2"] = 0x71, ["f3"] = 0x72, ["f4"] = 0x73, ["f5"] = 0x74,
        ["f6"] = 0x75, ["f7"] = 0x76, ["f8"] = 0x77, ["f9"] = 0x78, ["f10"] = 0x79,
        ["f11"] = 0x7A, ["f12"] = 0x7B, ["win"] = 0x5B, ["menu"] = 0x5D,
    };

    // Модификаторы держать, пока жмётся основная: (моды[], основная).
    private static readonly System.Collections.Generic.Dictionary<string, (ushort[] Mods, ushort Key)> ComboKeys = new()
    {
        ["alt+tab"] = (new ushort[] { 0x12 }, 0x09),
        ["alt+f4"] = (new ushort[] { 0x12 }, 0x73),
        ["ctrl+c"] = (new ushort[] { 0x11 }, 0x43),
        ["ctrl+v"] = (new ushort[] { 0x11 }, 0x56),
        ["ctrl+x"] = (new ushort[] { 0x11 }, 0x58),
        ["ctrl+z"] = (new ushort[] { 0x11 }, 0x5A),
        ["ctrl+a"] = (new ushort[] { 0x11 }, 0x41),
        ["ctrl+s"] = (new ushort[] { 0x11 }, 0x53),
        ["ctrl+t"] = (new ushort[] { 0x11 }, 0x54),
        ["ctrl+w"] = (new ushort[] { 0x11 }, 0x57),
        ["win+d"] = (new ushort[] { 0x5B }, 0x44),
        ["win+l"] = (new ushort[] { 0x5B }, 0x4C),
        ["win+r"] = (new ushort[] { 0x5B }, 0x52),
        ["win+e"] = (new ushort[] { 0x5B }, 0x45),
        ["shift+tab"] = (new ushort[] { 0x10 }, 0x09),
    };

    private static void KeyDown(ushort vk)
    {
        var d = new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk } } };
        SendInput(1, new[] { d }, Marshal.SizeOf<INPUT>());
    }

    private static void KeyUp(ushort vk)
    {
        var u = new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } } };
        SendInput(1, new[] { u }, Marshal.SizeOf<INPUT>());
    }

    private static void PressCombo((ushort[] Mods, ushort Key) combo)
    {
        foreach (var m in combo.Mods) { KeyDown(m); Thread.Sleep(30); }
        Thread.Sleep(40);
        KeyDown(combo.Key); Thread.Sleep(60); KeyUp(combo.Key);
        Thread.Sleep(40);
        for (int i = combo.Mods.Length - 1; i >= 0; i--) { KeyUp(combo.Mods[i]); Thread.Sleep(30); }
    }

    // Печать текста юникодом (кириллица — можно). Лимит 500 символов за раз.
    public static async Task<string> TypeTextAsync(string text)
    {
        var deny = DenyKbdIfOff();
        if (deny != null) return deny;
        if (string.IsNullOrEmpty(text)) return "Empty text";
        if (text.Length > 500) text = text[..500];
        return await Task.Run(() =>
        {
            try
            {
                foreach (var c in text)
                {
                    if (c == '\r') continue;
                    if (c == '\n') { PressVk(0x0D); continue; }
                    TypeUnicode(c);
                }
                return $"Typed {text.Length} chars";
            }
            catch (Exception ex) { return "Type failed: " + ex.Message; }
        }).ConfigureAwait(false);
    }
    public static string WakeOnly()
    {
        try
        {
            WakeDisplay();
            Thread.Sleep(400);
            PressVk(0x1B); // Esc — показать поле пароля
            return "Display woken. If locked, use Unlock with password.";
        }
        catch (Exception ex) { return "Wake failed: " + ex.Message; }
    }

    // Ввести пароль и нажать Enter. Возвращает статус (сам пароль НЕ логируем).
    public static async Task<string> UnlockAsync(string password)
    {
        if (string.IsNullOrEmpty(password)) return "Empty password";
        if (password.Length > 128) return "Password too long";
        return await Task.Run(() =>
        {
            try
            {
                WakeDisplay();
                Thread.Sleep(600);
                PressVk(0x1B); // Esc
                Thread.Sleep(400);
                // На всякий случай — фокус на поле: Enter, потом ввод
                foreach (var c in password)
                {
                    if (c == '\n' || c == '\r') continue;
                    TypeUnicode(c);
                }
                Thread.Sleep(200);
                PressVk(0x0D); // Enter
                return "Password sent (check screen stream).";
            }
            catch (Exception ex) { return "Unlock failed: " + ex.Message; }
        }).ConfigureAwait(false);
    }
}
