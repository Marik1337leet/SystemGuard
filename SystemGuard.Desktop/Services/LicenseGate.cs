using System;

namespace SystemGuard.Desktop.Services;

/// <summary>
/// Единая точка проверки тарифа для UI-гейтов.
/// Читает лицензию свежую из файла при каждом вызове —
/// так истёкшая подписка закрывает функции сразу после
/// следующей навигации (без перезапуска приложения).
/// Виды доступа: Free (истёкшая/невалидная тоже Free),
/// Pro (Pro + Enterprise), Enterprise (только Enterprise),
/// Trial (активный триал считается Pro).
/// </summary>
public static class LicenseGate
{
    /// <summary>
    /// Пинается при активации/деактивации/триале и при истечении.
    /// Подписчики (MainWindow, Settings) мгновенно перекрашивают гейты —
    /// без переходов по вкладкам.
    /// </summary>
    public static event Action? Changed;

    public static void NotifyChanged()
    {
        try { Changed?.Invoke(); } catch { }
    }

    public static LicenseInfo ReadFresh()
    {
        try { return new LicenseService().CurrentLicense; }
        catch { return new LicenseInfo(); }
    }

    public static bool IsActive(LicenseInfo l) => l.IsValid && !l.IsExpired;

    /// <summary>Триал = 14 дней полного Pro (Tier Free + Plan Trial + Valid).</summary>
    public static bool IsTrial(LicenseInfo l) =>
        IsActive(l) && l.Tier == "Free" && string.Equals(l.Plan, "Trial", StringComparison.OrdinalIgnoreCase);

    public static bool IsPro() => IsPro(ReadFresh());

    public static bool IsPro(LicenseInfo l) =>
        IsActive(l) && (l.Tier == "Pro" || l.Tier == "Enterprise" || IsTrial(l));

    public static bool IsEnterprise() => IsEnterprise(ReadFresh());

    public static bool IsEnterprise(LicenseInfo l) =>
        IsActive(l) && l.Tier == "Enterprise";

    public static string TierName()
    {
        var l = ReadFresh();
        if (!IsActive(l)) return "Free";
        if (l.Tier == "Enterprise") return "Enterprise";
        if (l.Tier == "Pro") return "Pro";
        if (IsTrial(l)) return "Pro Trial";
        return "Free";
    }
}
