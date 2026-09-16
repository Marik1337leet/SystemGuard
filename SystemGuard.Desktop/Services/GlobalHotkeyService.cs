using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using SystemGuard.Desktop.Models;

namespace SystemGuard.Desktop.Services;

// Глобальные хоткеи через low-level keyboard hook (WH_KEYBOARD_LL):
// работают даже когда окно свёрнуто. Без хука на окно — не нужен HWND.
public sealed class GlobalHotkeyService : IDisposable
{
    private static GlobalHotkeyService? _instance;
    public static GlobalHotkeyService Instance => _instance ??= new GlobalHotkeyService();

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private IntPtr _hook = IntPtr.Zero;
    private HookProc? _proc;
    private readonly object _lock = new();
    private Dictionary<string, string> _comboToAction = new(StringComparer.OrdinalIgnoreCase);

    public event Action<string>? OnHotkey;

    public bool IsRunning => _hook != IntPtr.Zero;

    public void Start(IEnumerable<HotkeyBinding> bindings)
    {
        Rebuild(bindings);
        lock (_lock)
        {
            if (_hook != IntPtr.Zero) return;
            try
            {
                _proc = HookCallback;
                using var cur = Process.GetCurrentProcess();
                using var mod = cur.MainModule;
                IntPtr hMod = GetModuleHandle(mod?.ModuleName);
                _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, hMod, 0);
            }
            catch { _hook = IntPtr.Zero; }
        }
    }

    public void Rebuild(IEnumerable<HotkeyBinding> bindings)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in bindings ?? Enumerable.Empty<HotkeyBinding>())
        {
            if (b == null || !b.Enabled) continue;
            string combo = HotkeyCatalog.Normalize(b.Combo);
            if (string.IsNullOrWhiteSpace(combo) || string.Equals(combo, b.ActionId, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!map.ContainsKey(combo)) map[combo] = b.ActionId;
        }
        lock (_lock) _comboToAction = map;
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_hook != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(_hook); } catch { }
                _hook = IntPtr.Zero;
            }
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
            {
                var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                string? combo = BuildCombo((int)kb.vkCode);
                if (combo != null)
                {
                    string? action = null;
                    lock (_lock) _comboToAction.TryGetValue(combo, out action);
                    if (action != null)
                    {
                        try { OnHotkey?.Invoke(action); } catch { }
                        _ = System.Threading.Tasks.Task.Run(async () =>
                        {
                            try { await HotkeyActionRunner.RunAsync(action).ConfigureAwait(false); }
                            catch { }
                        });
                        return (IntPtr)1; // съедаем, чтобы не уходило дальше
                    }
                }
            }
        }
        catch { }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static string? BuildCombo(int vk)
    {
        // Модификаторы
        bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;
        bool alt = (GetAsyncKeyState(0x12) & 0x8000) != 0;
        bool shift = (GetAsyncKeyState(0x10) & 0x8000) != 0;
        bool win = (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0;
        // Сам модификатор без ключа — не хоткей
        if (vk is 0x11 or 0x12 or 0x10 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5)
            return null;
        if (!ctrl && !alt && !shift && !win) return null;
        string key = vk switch
        {
            >= 0x30 and <= 0x39 => ((char)vk).ToString(),
            >= 0x41 and <= 0x5A => ((char)vk).ToString(),
            >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
            0x20 => "Space",
            0x0D => "Enter",
            0x1B => "Esc",
            0x09 => "Tab",
            0x2E => "Delete",
            0x2D => "Insert",
            0x24 => "Home",
            0x23 => "End",
            0x21 => "PgUp",
            0x22 => "PgDn",
            0x26 => "Up",
            0x28 => "Down",
            0x25 => "Left",
            0x27 => "Right",
            _ => null
        };
        if (key == null) return null;
        var parts = new List<string>();
        if (ctrl) parts.Add("Ctrl");
        if (alt) parts.Add("Alt");
        if (shift) parts.Add("Shift");
        if (win) parts.Add("Win");
        parts.Add(key);
        return string.Join("+", parts);
    }

    public void Dispose() => Stop();
}
