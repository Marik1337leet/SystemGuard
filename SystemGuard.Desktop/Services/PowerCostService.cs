using System;

namespace SystemGuard.Desktop.Services;

// Расчёт стоимости электроэнергии и углеродного следа. Чистая математика — легко тестируется.
public static class PowerCostService
{
    // watts — потребление ПК, hoursPerDay — часов в день, tariff — руб/кВт·ч (или любая валюта)
    public static double MonthlyKwh(double watts, double hoursPerDay, int daysPerMonth = 30)
        => watts / 1000.0 * hoursPerDay * daysPerMonth;

    public static double MonthlyCost(double watts, double hoursPerDay, double tariff, int daysPerMonth = 30)
        => MonthlyKwh(watts, hoursPerDay, daysPerMonth) * tariff;

    // ~0.4 кг CO2 на кВт·ч (средний коэффициент энергосети)
    public static double MonthlyCo2Kg(double watts, double hoursPerDay, int daysPerMonth = 30)
        => MonthlyKwh(watts, hoursPerDay, daysPerMonth) * 0.4;

    public static string Format(double watts, double hoursPerDay, double tariff)
    {
        if (watts <= 0 || hoursPerDay <= 0 || tariff < 0) return "Enter watts, hours and tariff";
        var kwh = MonthlyKwh(watts, hoursPerDay);
        var cost = MonthlyCost(watts, hoursPerDay, tariff);
        var co2 = MonthlyCo2Kg(watts, hoursPerDay);
        return $"{kwh:F1} kWh/month ≈ {cost:F2} • CO₂ ≈ {co2:F1} kg/month";
    }
}
