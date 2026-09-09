using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;

namespace SystemGuard.Desktop.Services;

// Детали железа из WMI: CPU (напряжение, кэши, ядра), RAM (скорость, каналы, XMP),
// диски (SMART/health), батарея. Все методы best-effort с безопасными fallback.
public class CpuDetails
{
    public string Name { get; set; } = "";
    public int Cores { get; set; }
    public int LogicalProcessors { get; set; }
    public double Voltage { get; set; }
    public uint L2CacheKb { get; set; }
    public uint L3CacheKb { get; set; }
    public uint MaxClockMhz { get; set; }
    public string Socket { get; set; } = "";
}

public class RamStickInfo
{
    public string Bank { get; set; } = "";
    public double CapacityGb { get; set; }
    public uint SpeedMhz { get; set; }
    public string MemoryType { get; set; } = "";
    public string PartNumber { get; set; } = "";
}

public class RamDetails
{
    public List<RamStickInfo> Sticks { get; set; } = new();
    public int ChannelCount => Sticks.Count >= 4 ? 4 : Sticks.Count >= 2 ? 2 : 1;
    public string ChannelMode => Sticks.Count >= 4 ? "Quad" : Sticks.Count >= 2 ? "Dual" : "Single";
    public uint SpeedMhz => Sticks.Count > 0 ? Sticks.Max(s => s.SpeedMhz) : 0;
    public double TotalGb => Math.Round(Sticks.Sum(s => s.CapacityGb), 1);
    // XMP: эвристика — скорость выше JEDEC-базы DDR4 (2133) / DDR5 (4800) считаем XMP/EXPO
    public bool XmpLikelyEnabled => SpeedMhz > 2133 && Sticks.Any(s => s.SpeedMhz > 2666);
}

public class SmartDiskInfo
{
    public string Model { get; set; } = "";
    public string Interface { get; set; } = "";
    public string MediaType { get; set; } = "";
    public string Health { get; set; } = "Unknown";
    public ulong SizeGb { get; set; }
}

public static class DetailedSystemInfoService
{
    // ── Реальный объём физической RAM ─────────────────────────────────────
    // GlobalMemoryStatusEx.ullTotalPhys — установленная память, БЕЗ pagefile.
    // (GC.GetGCMemoryInfo().TotalAvailableMemoryBytes включает своп и врёт:
    // 16 ГБ RAM + 8 ГБ pagefile показывались как 24 ГБ.)

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private static ulong? _cachedTotalPhys;
    private static readonly object _memLock = new();

    public static ulong GetTotalPhysicalBytes()
    {
        lock (_memLock)
        {
            if (_cachedTotalPhys.HasValue) return _cachedTotalPhys.Value;
            try
            {
                var st = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (GlobalMemoryStatusEx(ref st) && st.ullTotalPhys > 0)
                {
                    _cachedTotalPhys = st.ullTotalPhys;
                    return st.ullTotalPhys;
                }
            }
            catch { }
            try
            {
                using var s = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
                foreach (ManagementObject o in s.Get())
                {
                    var total = Convert.ToUInt64(o["TotalPhysicalMemory"] ?? 0);
                    if (total > 0) { _cachedTotalPhys = total; return total; }
                }
            }
            catch { }
            // Последний шанс (неточно: может включать pagefile)
            var gc = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            _cachedTotalPhys = (ulong)gc;
            return (ulong)gc;
        }
    }

    public static double GetTotalPhysicalGb() => GetTotalPhysicalBytes() / 1073741824.0;

    public static (double TotalGb, double AvailGb) GetPhysicalMemory()
    {
        try
        {
            var st = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref st) && st.ullTotalPhys > 0)
                return (st.ullTotalPhys / 1073741824.0, st.ullAvailPhys / 1073741824.0);
        }
        catch { }
        return (GetTotalPhysicalGb(), 0);
    }

    public static CpuDetails GetCpuDetails()
    {
        var d = new CpuDetails();
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, CurrentVoltage, L2CacheSize, L3CacheSize, MaxClockSpeed, SocketDesignation FROM Win32_Processor");
            foreach (ManagementObject o in s.Get())
            {
                d.Name = o["Name"]?.ToString()?.Trim() ?? d.Name;
                d.Cores = Convert.ToInt32(o["NumberOfCores"] ?? d.Cores);
                d.LogicalProcessors = Convert.ToInt32(o["NumberOfLogicalProcessors"] ?? d.LogicalProcessors);
                // CurrentVoltage: старший бит = флаг, младшие 8 бит = вольты*10
                try
                {
                    var v = Convert.ToInt32(o["CurrentVoltage"] ?? 0);
                    if (v > 0) d.Voltage = Math.Round((v & 0xFF) / 10.0, 2);
                }
                catch { }
                d.L2CacheKb = Convert.ToUInt32(o["L2CacheSize"] ?? 0);
                d.L3CacheKb = Convert.ToUInt32(o["L3CacheSize"] ?? 0);
                d.MaxClockMhz = Convert.ToUInt32(o["MaxClockSpeed"] ?? 0);
                d.Socket = o["SocketDesignation"]?.ToString() ?? "";
                break;
            }
        }
        catch { }
        if (d.LogicalProcessors == 0) d.LogicalProcessors = Environment.ProcessorCount;
        if (d.Cores == 0) d.Cores = Environment.ProcessorCount;
        return d;
    }

    public static RamDetails GetRamDetails()
    {
        var r = new RamDetails();
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT BankLabel, Capacity, Speed, MemoryType, PartNumber, SMBIOSMemoryType FROM Win32_PhysicalMemory");
            foreach (ManagementObject o in s.Get())
            {
                try
                {
                    var bytes = Convert.ToUInt64(o["Capacity"] ?? 0);
                    r.Sticks.Add(new RamStickInfo
                    {
                        Bank = (o["BankLabel"]?.ToString() ?? "").Trim(),
                        CapacityGb = Math.Round(bytes / 1073741824.0, 1),
                        SpeedMhz = Convert.ToUInt32(o["Speed"] ?? 0),
                        MemoryType = DecodeMemoryType(Convert.ToInt32(o["SMBIOSMemoryType"] ?? Convert.ToInt32(o["MemoryType"] ?? 0))),
                        PartNumber = (o["PartNumber"]?.ToString() ?? "").Trim(),
                    });
                }
                catch { }
            }
        }
        catch { }
        return r;
    }

    private static string DecodeMemoryType(int t) => t switch
    {
        0x14 => "DDR2",
        0x15 => "DDR2 FB-DIMM",
        0x18 => "DDR3",
        0x1A => "DDR4",
        0x22 => "DDR5",
        _ => t > 0 ? $"Type {t}" : "Unknown",
    };

    public static List<SmartDiskInfo> GetSmartDisks()
    {
        var list = new List<SmartDiskInfo>();
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Model, InterfaceType, MediaType, Size, Status FROM Win32_DiskDrive");
            foreach (ManagementObject o in s.Get())
            {
                try
                {
                    var sizeBytes = Convert.ToUInt64(o["Size"] ?? 0);
                    list.Add(new SmartDiskInfo
                    {
                        Model = (o["Model"]?.ToString() ?? "").Trim(),
                        Interface = o["InterfaceType"]?.ToString() ?? "",
                        MediaType = o["MediaType"]?.ToString() ?? "",
                        Health = o["Status"]?.ToString() ?? "Unknown",
                        SizeGb = sizeBytes / 1000000000,
                    });
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    public static string FormatSummary()
    {
        var cpu = GetCpuDetails();
        var ram = GetRamDetails();
        var disks = GetSmartDisks();
        var lines = new List<string>
        {
            $"CPU: {cpu.Name} | {cpu.Cores}C/{cpu.LogicalProcessors}T | {cpu.MaxClockMhz} MHz | {cpu.Voltage:F2} V | L2 {cpu.L2CacheKb}KB L3 {cpu.L3CacheKb}KB | {cpu.Socket}",
            $"RAM: {ram.TotalGb} GB {ram.ChannelMode}-channel ({ram.Sticks.Count} sticks) @ {ram.SpeedMhz} MHz | XMP: {(ram.XmpLikelyEnabled ? "likely ON" : "off/JEDEC")}",
        };
        lines.AddRange(disks.Select(d => $"Disk: {d.Model} [{d.Interface}/{d.MediaType}] {d.SizeGb}GB — {d.Health}"));
        return string.Join("\n", lines);
    }
}
