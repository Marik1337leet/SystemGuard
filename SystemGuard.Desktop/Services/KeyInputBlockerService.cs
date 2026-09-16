using System;
using System.Runtime.InteropServices;

namespace SystemGuard.Desktop.Services;

public class KeyInputBlockerService : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_TAB = 0x09;
    private const int VK_F4 = 0x73;
    private const int VK_MENU = 0x12;
    private const int VK_CONTROL = 0x11;
    private const int VK_ESCAPE = 0x1B;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private IntPtr _hookId = IntPtr.Zero;
    private readonly LowLevelKeyboardProc _proc;

    private bool _blockWin;
    private bool _blockAltTab;
    private bool _blockAltF4;

    public KeyInputBlockerService()
    {
        _proc = HookCallback;
    }

    public bool IsActive => _hookId != IntPtr.Zero;

    public void Start(bool blockWin, bool blockAltTab, bool blockAltF4)
    {
        _blockWin = blockWin;
        _blockAltTab = blockAltTab;
        _blockAltF4 = blockAltF4;

        if (!blockWin && !blockAltTab && !blockAltF4) return;
        if (_hookId != IntPtr.Zero) return;

        try
        {
            using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule;
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc,
                GetModuleHandle(curModule?.ModuleName), 0);
        }
        catch { _hookId = IntPtr.Zero; }
    }

    public void Stop()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
            {
                int vkCode = Marshal.ReadInt32(lParam);
                bool altDown = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
                bool ctrlDown = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;

                if (_blockWin && (vkCode == VK_LWIN || vkCode == VK_RWIN))
                    return (IntPtr)1;

                // Ctrl+Esc тоже открывает Start — режем вместе с Win.
                if (_blockWin && vkCode == VK_ESCAPE && ctrlDown)
                    return (IntPtr)1;

                if (_blockAltTab && vkCode == VK_TAB && altDown)
                    return (IntPtr)1;

                if (_blockAltF4 && vkCode == VK_F4 && altDown)
                    return (IntPtr)1;
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose() => Stop();
}