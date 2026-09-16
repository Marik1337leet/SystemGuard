using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SystemGuard.Desktop.Services;

// Выполнение действий хоткеев по ActionId. Дергается и из глобального хука,
// и из кнопки «Выполнить» в настройках, и из RemoteActions (WebApp/бот).
public static class HotkeyActionRunner
{
    public static async Task<string> RunAsync(string actionId)
    {
        try
        {
            switch ((actionId ?? "").Trim().ToLowerInvariant())
            {
                case "optimize":
                {
                    int n = await MemoryTrimmer.TrimAllAsync().ConfigureAwait(false);
                    return $"RAM оптимизирована: {n} процессов";
                }
                case "gameboost":
                {
                    string r = await new GameModeService().BoostNowAsync().ConfigureAwait(false);
                    return r;
                }
                case "widget":
                {
                    // Тоггл виджетов. Show/Hide маршалятся на UI-поток асинхронно,
                    // поэтому статус считаем от состояния ДО вызова, а не после.
                    try
                    {
                        bool wasOpen = OverlayManager.IsWidgetsOpen;
                        if (wasOpen) OverlayManager.HideWidgets();
                        else OverlayManager.ShowWidgets();
                        return wasOpen ? "Виджет выключен" : "Виджет включён";
                    }
                    catch (Exception ex) { return "Виджет: " + ex.Message; }
                }
                case "screenshot":
                {
                    var jpg = await ScreenCaptureService.CaptureScreenJpegAsync(1920, 85).ConfigureAwait(false);
                    if (jpg == null) return "Скриншот не удался";
                    string path = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                        $"SystemGuard_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");
                    await File.WriteAllBytesAsync(path, jpg).ConfigureAwait(false);
                    return "Скриншот: " + path;
                }
                case "mute":
                {
                    await Task.Run(() => VolumeService.MuteToggle()).ConfigureAwait(false);
                    await Task.Delay(150).ConfigureAwait(false);
                    return VolumeService.IsMuted() ? "Звук выключен" : $"Звук включён ({VolumeService.GetPercent()}%)";
                }
                case "lock":
                {
                    try { Process.Start("rundll32.exe", "user32.dll,LockWorkStation"); return "ПК заблокирован"; }
                    catch (Exception ex) { return "Блокировка: " + ex.Message; }
                }
                default: return "Неизвестное действие: " + actionId;
            }
        }
        catch (Exception ex) { return ex.Message; }
    }

    public static string RunSync(string actionId)
    {
        try { return RunAsync(actionId).ConfigureAwait(false).GetAwaiter().GetResult(); }
        catch (Exception ex) { return ex.Message; }
    }
}
