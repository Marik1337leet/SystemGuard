using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SystemGuard.Desktop.Services;

// Громкость через CoreAudio (IAudioEndpointVolume) — настоящий мастер-канал
// устройства вывода, а не legacy waveOut, который на половине систем
// двигает "не тот" ползунок и звук не меняется.
// Fallback-цепочка: CoreAudio → winmm waveOut → системные клавиши.
public static class VolumeService
{
    #region CoreAudio COM
    private enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
    private enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCom { }

    [Guid("A95664D2-9614-4F35-A746-DE8DB636B87C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, int dwStateMask, out IntPtr ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppDevice);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    }

    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr pNotify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr pNotify);
        [PreserveSig] int GetChannelCount(out int pnChannelCount);
        [PreserveSig] int SetMasterVolumeLevel(float fLevelDB, Guid pguidEventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float fLevel, Guid pguidEventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float pfLevelDB);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float pfLevel);
        [PreserveSig] int SetChannelVolumeLevel(uint nChannel, float fLevelDB, Guid pguidEventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, Guid pguidEventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, Guid pguidEventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
    }

    private static IAudioEndpointVolume? GetEndpoint()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            try
            {
                if (enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var dev) != 0 || dev == null)
                    return null;
                try
                {
                    var iid = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
                    if (dev.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out var obj) != 0)
                        return null;
                    return (IAudioEndpointVolume)obj;
                }
                finally { try { Marshal.ReleaseComObject(dev); } catch { } }
            }
            finally { try { Marshal.ReleaseComObject(enumerator); } catch { } }
        }
        catch { return null; }
    }
    #endregion

    #region Legacy
    [DllImport("winmm.dll")] private static extern int waveOutSetVolume(IntPtr hwo, uint dwVolume);
    [DllImport("winmm.dll")] private static extern int waveOutGetVolume(IntPtr hwo, out uint dwVolume);
    [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const byte VK_VOLUME_MUTE = 0xAD;
    private const byte VK_VOLUME_DOWN = 0xAE;
    private const byte VK_VOLUME_UP = 0xAF;
    #endregion

    public static int GetPercent()
    {
        try
        {
            var ep = GetEndpoint();
            if (ep != null)
            {
                try
                {
                    if (ep.GetMute(out bool muted) == 0 && muted) return 0;
                    if (ep.GetMasterVolumeLevelScalar(out float level) == 0)
                        return (int)Math.Round(Math.Clamp(level, 0, 1) * 100);
                }
                finally { try { Marshal.ReleaseComObject(ep); } catch { } }
            }
        }
        catch { }
        try
        {
            if (waveOutGetVolume(IntPtr.Zero, out uint v) == 0)
                return (int)Math.Round((v & 0xFFFF) / 65535.0 * 100);
        }
        catch { }
        return -1;
    }

    public static bool IsMuted()
    {
        try
        {
            var ep = GetEndpoint();
            if (ep == null) return false;
            try { return ep.GetMute(out bool m) == 0 && m; }
            finally { try { Marshal.ReleaseComObject(ep); } catch { } }
        }
        catch { return false; }
    }

    // Точная установка 0-100. Возвращает итоговый уровень или -1.
    public static int SetPercent(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        try
        {
            var ep = GetEndpoint();
            if (ep != null)
            {
                try
                {
                    if (ep.SetMute(false, Guid.Empty) != 0) return -1;
                    if (ep.SetMasterVolumeLevelScalar(percent / 100f, Guid.Empty) != 0) return -1;
                    return GetPercent();
                }
                finally { try { Marshal.ReleaseComObject(ep); } catch { } }
            }
        }
        catch { }
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
            var ep = GetEndpoint();
            if (ep != null)
            {
                try
                {
                    ep.GetMute(out bool m);
                    if (ep.SetMute(!m, Guid.Empty) == 0) return;
                }
                finally { try { Marshal.ReleaseComObject(ep); } catch { } }
            }
        }
        catch { }
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
}
