using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;

namespace SystemGuard.Desktop.Services;

// Громкость через CoreAudio (NAudio.Wasapi) — настоящий мастер-канал
// устройства вывода, а не legacy waveOut, который на половине систем
// двигает "не тот" ползунок и звук не меняется.
// (Свой COM-interop убран: QI на IMMDeviceEnumerator сыпался E_NOINTERFACE,
//  а NAudio на той же машине работает — проверено тестами.)
// Fallback-цепочка: CoreAudio → winmm waveOut → системные клавиши.
public static class VolumeService
{
    public static string LastDiag { get; private set; } = "";

    [DllImport("winmm.dll")] private static extern int waveOutSetVolume(IntPtr hwo, uint dwVolume);
    [DllImport("winmm.dll")] private static extern int waveOutGetVolume(IntPtr hwo, out uint dwVolume);
    [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const byte VK_VOLUME_MUTE = 0xAD;
    private const byte VK_VOLUME_DOWN = 0xAE;
    private const byte VK_VOLUME_UP = 0xAF;

    private static MMDevice? DefaultRender()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            return en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch (Exception ex)
        {
            LastDiag = "endpoint: " + ex.GetType().Name + " " + Trim(ex.Message, 100);
            return null;
        }
    }

    public static int GetPercent()
    {
        try
        {
            using var dev = DefaultRender();
            if (dev != null)
            {
                var v = dev.AudioEndpointVolume;
                if (v.Mute) return 0;
                LastDiag = "get: ok";
                return (int)Math.Round(Math.Clamp(v.MasterVolumeLevelScalar, 0, 1) * 100);
            }
        }
        catch (Exception ex) { LastDiag = "get: " + ex.GetType().Name; }
        try
        {
            if (waveOutGetVolume(IntPtr.Zero, out uint w) == 0)
                return (int)Math.Round((w & 0xFFFF) / 65535.0 * 100);
        }
        catch { }
        return -1;
    }

    public static bool IsMuted()
    {
        try
        {
            using var dev = DefaultRender();
            if (dev == null) return false;
            return dev.AudioEndpointVolume.Mute;
        }
        catch { return false; }
    }

    // Точная установка 0-100. Возвращает итоговый уровень или -1.
    public static int SetPercent(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        try
        {
            using var dev = DefaultRender();
            if (dev != null)
            {
                var v = dev.AudioEndpointVolume;
                v.Mute = false;
                v.MasterVolumeLevelScalar = percent / 100f;
                int back = GetPercent();
                LastDiag = "set: ok";
                return back;
            }
        }
        catch (Exception ex) { LastDiag = "set: " + ex.GetType().Name + " " + Trim(ex.Message, 100); }
        try
        {
            uint ch = (uint)Math.Round(percent / 100.0 * 65535);
            if (waveOutSetVolume(IntPtr.Zero, ch | (ch << 16)) == 0)
                return GetPercent();
        }
        catch { }
        return -1;
    }

    public static void MuteToggle()
    {
        try
        {
            using var dev = DefaultRender();
            if (dev != null)
            {
                var v = dev.AudioEndpointVolume;
                v.Mute = !v.Mute;
                LastDiag = "mute: ok";
                return;
            }
        }
        catch (Exception ex) { LastDiag = "mute: " + ex.GetType().Name; }
        Press(VK_VOLUME_MUTE, 1);
    }

    public static void StepUp(int steps = 2) => Press(VK_VOLUME_UP, steps);
    public static void StepDown(int steps = 2) => Press(VK_VOLUME_DOWN, steps);

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

    public const byte VK_MEDIA_NEXT = 0xB0;
    public const byte VK_MEDIA_PREV = 0xB1;
    public const byte VK_MEDIA_PLAY_PAUSE = 0xB3;

    public static Task MediaAsync(byte vk) => Task.Run(() =>
    {
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        Thread.Sleep(70);
        keybd_event(vk, 0, 2, UIntPtr.Zero);
    });

    private static string Trim(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");
}
