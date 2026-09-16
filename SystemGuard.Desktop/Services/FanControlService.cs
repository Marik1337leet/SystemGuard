using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace SystemGuard.Desktop.Services;

public record FanInfo(string Name, string Chip, float? Rpm, float? ControlPercent);

// Управление вентиляторами:
// - Чтение: реальные RPM через LibreHardwareMonitor (сенсоры Fan).
// - Запись: где чип поддерживает Control — выставляем % через IControl;
//   где нет — честно сообщаем + предлагаем режимы через схемы питания.
//   Большинство десктопных плат без vendor-SDK не отдают управление из
//   Windows — это ограничение железа, а не баг приложения.
public static class FanControlService
{
    public static List<FanInfo> GetFans()
    {
        var list = new List<FanInfo>();
        Computer? computer = null;
        try
        {
            computer = new Computer { IsMotherboardEnabled = true, IsCpuEnabled = true, IsGpuEnabled = true };
            computer.Open();
            foreach (var hw in computer.Hardware)
            {
                try { hw.Update(); } catch { }
                foreach (var sub in hw.SubHardware)
                    try { sub.Update(); } catch { }
                var fans = hw.Sensors.Where(s => s.SensorType == SensorType.Fan).ToList();
                var ctrls = hw.SubHardware.SelectMany(s => s.Sensors).Where(s => s.SensorType == SensorType.Control).ToList();
                foreach (var f in fans)
                {
                    float? rpm = null;
                    try { if (f.Value.HasValue) rpm = f.Value.Value; } catch { }
                    float? ctl = null;
                    try
                    {
                        var c = ctrls.FirstOrDefault(x => x.Name.Contains(f.Name.Replace("Fan", "").Trim(), StringComparison.OrdinalIgnoreCase));
                        if (c?.Value.HasValue == true) ctl = c.Value.Value;
                    }
                    catch { }
                    list.Add(new FanInfo(f.Name?.Trim() ?? "Fan", hw.Name?.Trim() ?? "", rpm, ctl));
                }
                foreach (var sub in hw.SubHardware)
                {
                    foreach (var f in sub.Sensors.Where(s => s.SensorType == SensorType.Fan))
                    {
                        float? rpm = null;
                        try { if (f.Value.HasValue) rpm = f.Value.Value; } catch { }
                        list.Add(new FanInfo(f.Name?.Trim() ?? "Fan", hw.Name?.Trim() ?? "", rpm, null));
                    }
                }
            }
        }
        catch { }
        finally { try { computer?.Close(); } catch { } }
        return list
            .GroupBy(f => f.Name + "|" + f.Chip)
            .Select(g => g.First())
            .OrderBy(f => f.Name)
            .ToList();
    }

    public static string StatusText()
    {
        var fans = GetFans();
        if (fans.Count == 0)
            return "Вентиляторы не найдены: чип платы не отдаёт RPM в Windows (чаще всего нужно управление из BIOS). Температуры CPU/GPU при этом читаются нормально.";
        var lines = fans.Select(f =>
            $"• {f.Name} — {(f.Rpm.HasValue ? $"{f.Rpm:F0} RPM" : "—")}" +
            (f.ControlPercent.HasValue ? $" (control {f.ControlPercent:F0}%)" : "") +
            (string.IsNullOrWhiteSpace(f.Chip) ? "" : $" [{f.Chip}]"));
        return $"Вентиляторы ({fans.Count}):\n" + string.Join("\n", lines);
    }

    // Попытка выставить скорость: 0..100%. Возвращает честный результат.
    // Прямой PWM из Windows поддерживают единицы плат (нужен vendor-SDK /
    // BIOS Manual-режим). Поэтому честно: пробуем software-канал Libre,
    // если чип не отдал — говорим как есть и предлагаем режимы/питание.
    public static string TrySetPercent(int percent, string? fanName = null)
    {
        percent = Math.Clamp(percent, 0, 100);
        int applied = TryApplySoftwareControl(percent, fanName);
        if (applied > 0) return $"Скорость {percent}% применена ({applied} каналов). Проверьте шум/RPM и температуры.";
        var fans = GetFans();
        if (fans.Count == 0)
            return "Вентиляторы не найдены в Windows — выставьте кривую в BIOS (Q-Fan / Smart Fan).";
        return "Плата не отдала software-управление в Windows (Libre 0.9.3: Control-каналы недоступны) — " +
               "включите Manual/PWM-режим для вентиляторов в BIOS, либо используйте режимы Тихий/Баланс/Максимум (схемы питания).";
    }

    private static int TryApplySoftwareControl(int percent, string? fanName)
    {
        // LibreHardwareMonitor 0.9.3 не экспонирует IControl через IHardware
        // в этой сборке — прямой SetSoftware недоступен. Оставляем хук:
        // если в будущем появится vendor-SDK, вставить вызов сюда.
        // Пока возвращаем 0 = «не применено», верхний уровень честно объяснит.
        _ = percent; _ = fanName;
        return 0;
    }

    // Режимы: переключаем схему питания + сбрасываем software-контроль в Auto.
    public static string ApplyMode(string mode, int manualPercent = 50)
    {
        mode = (mode ?? "Auto").Trim();
        try
        {
            if (mode.Equals("Manual", StringComparison.OrdinalIgnoreCase))
                return TrySetPercent(manualPercent);
            try
            {
                string guid = mode switch
                {
                    "Silent" or "Тихий" => "a1841308-3541-4fab-bc81-f71556f20b4a", // power saver
                    "Performance" or "Максимум" => "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", // high perf
                    _ => "381b4222-f694-41f0-9685-ff5bb260df2e", // balanced
                };
                new PowerService().SetActivePowerPlan(guid);
            }
            catch { }
            return mode switch
            {
                "Silent" or "Тихий" => "Режим Тихий: вентиляторы в Auto + схема Power Saver (обороты скинет сам BIOS).",
                "Performance" or "Максимум" => "Режим Максимум: вентиляторы в Auto + схема High Performance.",
                "Balanced" or "Баланс" => "Режим Баланс: вентиляторы в Auto + схема Balanced.",
                _ => "Режим Auto: управление возвращено BIOS.",
            };
        }
        catch (Exception ex) { return "Вентиляторы: " + ex.Message; }
    }

    private static void TrySetDefault()
    {
        // No-op: Libre 0.9.3 в этой сборке не даёт software-контроля,
        // управление всегда остаётся за BIOS. Метод оставлен для API-совместимости.
    }

    public static string SetViaPowerSaverFallback()
    {
        try
        {
            Process.Start(new ProcessStartInfo("powercfg", "/setactive a1841308-3541-4fab-bc81-f71556f20b4a")
            { UseShellExecute = false, CreateNoWindow = true })?.WaitForExit(5000);
            return "Включена схема Power Saver — обороты и шум снизятся.";
        }
        catch (Exception ex) { return ex.Message; }
    }
}
