using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SystemGuard.Desktop.Services;

// Громкость БЕЗ спавна powershell на каждое нажатие:
// точное значение — через winmm waveOut (мгновенно),
// относительные шаги и мут — через системные медиа-клавиши.
public static class VolumeService
{
    [DllImport("winmm.dll")] private static extern int waveOutSetVolume(IntPtr hwo, uint dwVolume);
    [DllImport("winmm.dll")] private static extern int waveOutGetVolume(IntPtr hwo, out uint dwVolume);
    [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const byte VK_VOLUME_MUTE = 0xAD;
    private const byte VK_VOLUME_DOWN = 0xAE;
    private const byte VK_VOLUME_UP = 0xAF;

    public static int GetPercent()
    {
        try
        {
            if (waveOutGetVolume(IntPtr.Zero, out uint v) == 0)
            {
                int left = (int)(v & 0xFFFF);
                return (int)Math.Round(left / 65535.0 * 100);
            }
        }
        catch { }
        return -1;
    }

    // Точная установка 0-100. Возвращает итоговый уровень или -1 при неудаче.
    public static int SetPercent(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        try
        {
            uint ch = (uint)Math.Round(percent / 100.0 * 65535);
            uint stereo = ch | (ch << 16);
            if (waveOutSetVolume(IntPtr.Zero, stereo) == 0)
                return GetPercent();
        }
        catch { }
        return -1;
    }

    public static void StepUp(int steps = 2) => Press(VK_VOLUME_UP, steps);
    public static void StepDown(int steps = 2) => Press(VK_VOLUME_DOWN, steps);
    public static void MuteToggle() => Press(VK_VOLUME_MUTE, 1);

    private static void Press(byte vk, int times)
    {
        for (int i = 0; i < Math.Clamp(times, 1, 10); i++)
        {
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            Thread.Sleep(45);
            keybd_event(vk, 0, 2, UIntPtr.Zero);
            if (i + 1 < times) Thread.Sleep(45);
        }
    }

    public static Task PressAsync(byte vk, int times = 1) =>
        Task.Run(() => Press(vk, times));

    // Медиа-клавиши треков
    public const byte VK_MEDIA_NEXT = 0xB0;
    public const byte VK_MEDIA_PREV = 0xB1;
    public const byte VK_MEDIA_PLAY_PAUSE = 0xB3;

    public static Task MediaAsync(byte vk) => Task.Run(() =>
    {
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        Thread.Sleep(70);
        keybd_event(vk, 0, 2, UIntPtr.Zero);
    });
}
