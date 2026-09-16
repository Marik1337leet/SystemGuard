using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using SystemGuard.Desktop.Models;

namespace SystemGuard.Desktop.Services;

public class PowerService
{
    public List<PowerPlanInfo> GetPowerPlans()
    {
        var plans = new List<PowerPlanInfo>();
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powercfg",
                    Arguments = "/list",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    // powercfg на RU-Windows пишет в OEM-кодовой странице (CP866):
                    // без этого кириллица превращается в «У « бЕаФу п».
                    StandardOutputEncoding = CmdEncoding.Oem
                }
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            // powercfg локализован (RU: "GUID схемы питания: ... (Сбалансированная) *").
            // Ищем GUID регуляркой и имя в скобках — языконезависимо.
            var guidRx = new System.Text.RegularExpressions.Regex(
                @"[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}");
            foreach (var line in output.Split('\n'))
            {
                var m = guidRx.Match(line);
                if (!m.Success) continue;

                var guid = m.Value;
                var parenStart = line.IndexOf('(');
                var parenEnd = line.LastIndexOf(')');
                var name = parenStart >= 0 && parenEnd > parenStart
                    ? line[(parenStart + 1)..parenEnd].Trim()
                    : "";
                // Если имя пустое/битое (не та локаль/кодировка) — подставляем
                // понятное по известному GUID, иначе короткий GUID.
                if (string.IsNullOrWhiteSpace(name) || name.Contains('�'))
                    name = FriendlyPlanName(guid);

                plans.Add(new PowerPlanInfo
                {
                    Name = name,
                    Guid = guid,
                    IsActive = line.Contains('*')
                });
            }
        }
        catch { }
        return plans;
    }

    private static string FriendlyPlanName(string guid) => guid.ToLowerInvariant() switch
    {
        "381b4222-f694-41f0-9685-ff5bb260df2e" => "Balanced",
        "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" => "High performance",
        "a1841308-3541-4fab-bc81-f71556f20b4a" => "Power saver",
        "e9a42b02-d5df-448b-aa00-03f14749eb61" => "Ultimate Performance",
        _ => $"Power plan {guid[..8]}…"
    };

    public void SetActivePowerPlan(string guid)
    {
        try { Process.Start("powercfg", $"/setactive {guid}"); }
        catch { }
    }

    // ── Battery ───────────────────────────────────────────────────────────────

    public BatteryInfo? GetBatteryInfo()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Battery");
            foreach (ManagementObject battery in searcher.Get())
            {
                var chargePercent = System.Convert.ToInt32(battery["EstimatedChargeRemaining"] ?? 0);

                // BatteryStatus коды WMI:
                // 1 = Discharging, 2 = AC + Charging, 3 = Fully Charged,
                // 4 = Low, 5 = Critical, 6 = Charging, 7 = Charging High,
                // 8 = Charging Low, 9 = Charging Critical, 10 = Undefined, 11 = Partially Charged
                var statusCode = System.Convert.ToInt32(battery["BatteryStatus"] ?? 0);
                var (status, isCharging) = MapBatteryStatus(statusCode);

                var estimatedRunTime = battery["EstimatedRunTime"];
                TimeSpan? timeRemaining = null;
                if (estimatedRunTime != null)
                {
                    var minutes = System.Convert.ToInt32(estimatedRunTime);
                    // WMI возвращает 71582788 как "unknown" sentinel value
                    if (minutes > 0 && minutes < 71582788)
                        timeRemaining = TimeSpan.FromMinutes(minutes);
                }

                // Wear level — требует доп. провайдера (не доступен через Win32_Battery).
                // Оставляем 0 как индикатор "не определено", UI должен это учитывать.
                double wearLevel = GetWearLevelIfAvailable();

                return new BatteryInfo
                {
                    ChargePercent = chargePercent,
                    WearLevel = wearLevel,
                    IsCharging = isCharging,
                    Status = status,
                    TimeRemaining = timeRemaining
                };
            }
        }
        catch { }
        return null; // нет батареи (десктоп)
    }

    private static (string Status, bool IsCharging) MapBatteryStatus(int code) => code switch
    {
        1 => ("Discharging", false),
        2 => ("Charging", true),
        3 => ("Full", false),
        4 => ("Low", false),
        5 => ("Critical", false),
        6 => ("Charging", true),
        7 => ("Charging", true),
        8 => ("Charging (Low)", true),
        9 => ("Charging (Critical)", true),
        11 => ("Partially Charged", false),
        _ => ("Unknown", false)
    };

    private static double GetWearLevelIfAvailable()
    {
        try
        {
            // Wear level через WMI root\WMI:BatteryStaticData / BatteryFullChargedCapacity
            // доступен не на всех системах (зависит от драйвера ACPI производителя)
            using var designSearcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT * FROM BatteryStaticData");
            using var fullChargeSearcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT * FROM BatteryFullChargedCapacity");

            uint designCapacity = 0;
            uint fullChargeCapacity = 0;

            foreach (ManagementObject obj in designSearcher.Get())
            {
                designCapacity = (uint)(obj["DesignedCapacity"] ?? 0u);
                break;
            }
            foreach (ManagementObject obj in fullChargeSearcher.Get())
            {
                fullChargeCapacity = (uint)(obj["FullChargedCapacity"] ?? 0u);
                break;
            }

            if (designCapacity > 0 && fullChargeCapacity > 0)
            {
                var wear = 100.0 - ((double)fullChargeCapacity / designCapacity * 100.0);
                return Math.Round(Math.Max(0, wear), 1);
            }
        }
        catch { }
        return 0; // не определено
    }

    // ── System power actions ─────────────────────────────────────────────────

    public void Shutdown(int delaySeconds = 0) =>
        SafeStart("shutdown", $"/s /t {delaySeconds}");

    public void Restart(int delaySeconds = 0) =>
        SafeStart("shutdown", $"/r /t {delaySeconds}");

    public void CancelShutdown() =>
        SafeStart("shutdown", "/a");

    public void Sleep() =>
        SafeStart("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0");

    public void Hibernate() =>
        SafeStart("shutdown", "/h");

    public void LockWorkstation() =>
        SafeStart("rundll32.exe", "user32.dll,LockWorkStation");

    private static void SafeStart(string fileName, string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(fileName, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch { }
    }
}
