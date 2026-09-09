using LibreHardwareMonitor.Hardware;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SystemGuard.Desktop.Services;

public class HardwareMonitorService : IDisposable
{
    private Computer? _computer;
    private System.Timers.Timer? _timer;
    private volatile bool _isMonitoring;
    private int _updating; // 0/1 — защита от перекрытия тиков (hardware.Update ~200-500мс)
    private readonly object _lock = new();

    public event Action<HardwareInfo>? OnHardwareUpdated;

    public void Start(int updateIntervalMs = 2000)
    {
        try
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = true,
                IsStorageEnabled = true,
                IsNetworkEnabled = true
            };
            _computer.Open();
            _isMonitoring = true;

            _timer = new System.Timers.Timer(Math.Max(updateIntervalMs, 1500));
            _timer.AutoReset = false; // следующий тик только после завершения UpdateData
            _timer.Elapsed += (_, _) =>
            {
                try { UpdateData(); }
                finally
                {
                    try { if (_isMonitoring) _timer?.Start(); } catch { }
                }
            };
            _timer.Start();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Monitor init error: {ex.Message}");
        }
    }

    private void UpdateData()
    {
        if (!_isMonitoring || _computer == null) return;
        // Не даём тикам накапливаться: пропуск дешевле, чем очередь из Update()
        if (System.Threading.Interlocked.Exchange(ref _updating, 1) != 0) return;

        lock (_lock)
        {
            try
            {
                foreach (var hardware in _computer.Hardware)
                    hardware.Update();

                var info = new HardwareInfo();
                CollectCpuData(_computer, info);
                CollectGpuData(_computer, info);
                CollectMemoryData(_computer, info);
                CollectNetworkData(_computer, info);
                CollectStorageData(_computer, info);

                OnHardwareUpdated?.Invoke(info);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Update error: {ex.Message}");
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _updating, 0);
            }
        }
    }

    private void CollectCpuData(Computer computer, HardwareInfo info)
    {
        var cpu = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        if (cpu == null) return;

        info.CpuName = cpu.Name.Trim();

        foreach (var sensor in cpu.Sensors)
        {
            if (!sensor.Value.HasValue) continue;

            switch (sensor.SensorType)
            {
                case SensorType.Load when sensor.Name.Contains("Total"):
                    info.CpuLoad = sensor.Value.Value;
                    break;
                case SensorType.Temperature when sensor.Name.Contains("Package") || sensor.Name.Contains("Core Average"):
                    if (info.CpuTemperature == 0) info.CpuTemperature = sensor.Value.Value;
                    break;
                case SensorType.Temperature when sensor.Name.Contains("Core") && !sensor.Name.Contains("Distance"):
                    // Берём МАКСИМУМ по ядрам, а не последнее ядро (иначе цифра прыгает)
                    info.CpuTemperature = Math.Max(info.CpuTemperature, sensor.Value.Value);
                    break;
                case SensorType.Clock when sensor.Name.Contains("Core #"):
                    // LibreHardwareMonitor называет ядра "Core #1..#N" (МГц) —
                    // старого "Core Max" нет, берём максимум активных ядер
                    info.CpuClock = Math.Max(info.CpuClock, sensor.Value.Value);
                    break;
                case SensorType.Power when sensor.Name.Contains("Package"):
                    info.CpuPower = sensor.Value.Value;
                    break;
            }
        }

        // Fallback: any temperature sensor
        if (info.CpuTemperature == 0)
        {
            var tempSensor = cpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Value.HasValue);
            if (tempSensor != null) info.CpuTemperature = tempSensor.Value.Value;
        }

        // Fallback: максимальный clock (а НЕ первый — первым идёт Bus Speed ~100 МГц
        // и на экране красовалось "0.10 GHz")
        if (info.CpuClock == 0)
        {
            float max = 0;
            foreach (var s in cpu.Sensors)
                if (s.SensorType == SensorType.Clock && s.Value.HasValue)
                    max = Math.Max(max, s.Value.Value);
            // Bus Speed (~100) отбрасываем: реальный минимум ядра — 400+ МГц
            info.CpuClock = max >= 400 ? max : 0;
        }
    }

    private void CollectGpuData(Computer computer, HardwareInfo info)
    {
        var gpu = computer.Hardware.FirstOrDefault(h =>
            h.HardwareType == HardwareType.GpuNvidia ||
            h.HardwareType == HardwareType.GpuAmd ||
            h.HardwareType == HardwareType.GpuIntel);

        if (gpu == null) return;

        info.GpuName = gpu.Name.Trim();

        foreach (var sensor in gpu.Sensors)
        {
            if (!sensor.Value.HasValue) continue;

            switch (sensor.SensorType)
            {
                case SensorType.Load when sensor.Name.Contains("Core"):
                    info.GpuLoad = sensor.Value.Value;
                    break;
                case SensorType.Temperature when sensor.Name.Contains("Core"):
                    info.GpuTemperature = sensor.Value.Value;
                    break;
                case SensorType.SmallData when sensor.Name.Contains("Used"):
                    info.GpuMemoryUsed = sensor.Value.Value;
                    break;
                case SensorType.SmallData when sensor.Name.Contains("Total"):
                    info.GpuMemoryTotal = sensor.Value.Value;
                    break;
            }
        }

        // Fallback: any temperature
        if (info.GpuTemperature == 0)
        {
            var tempSensor = gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Value.HasValue);
            if (tempSensor != null) info.GpuTemperature = tempSensor.Value.Value;
        }
    }

    private System.Diagnostics.PerformanceCounter? _memAvailCounter;

    private void CollectMemoryData(Computer computer, HardwareInfo info)
    {
        var memory = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Memory);
        if (memory == null) return;

        foreach (var sensor in memory.Sensors)
        {
            if (!sensor.Value.HasValue) continue;

            switch (sensor.SensorType)
            {
                case SensorType.Data when sensor.Name.Contains("Used"):
                    info.MemoryUsed = sensor.Value.Value;
                    break;
                case SensorType.Data when sensor.Name.Contains("Available"):
                    info.MemoryAvailable = sensor.Value.Value;
                    break;
                case SensorType.Load when sensor.Name.Contains("Memory"):
                    info.MemoryLoad = sensor.Value.Value;
                    break;
            }
        }

        // Fallback: если LHM не отдал память (нет прав/чипсет) — считаем сами,
        // иначе RAM навсегда остаётся "0.0 / 0.0 GB".
        // Total — ТОЛЬКО физическая память (GlobalMemoryStatusEx).
        // GC.TotalAvailableMemoryBytes включает pagefile и завышает (16→24 ГБ).
        if (info.MemoryUsed == 0 && info.MemoryAvailable == 0)
        {
            try
            {
                _memAvailCounter ??= new System.Diagnostics.PerformanceCounter("Memory", "Available MBytes");
                float availMb = _memAvailCounter.NextValue();
                double totalGb = DetailedSystemInfoService.GetTotalPhysicalGb();
                if (totalGb > 0 && availMb >= 0)
                {
                    info.MemoryAvailable = availMb / 1024f;
                    info.MemoryUsed = (float)Math.Max(0, totalGb - info.MemoryAvailable);
                }
            }
            catch { }
        }
        else
        {
            // Sanity-clamp: used+avail не может превышать физическую RAM.
            // Если LHM насчитал больше (единицы/виртуализация) — пересчитываем used.
            try
            {
                double totalGb = DetailedSystemInfoService.GetTotalPhysicalGb();
                if (totalGb > 0 && info.MemoryUsed + info.MemoryAvailable > totalGb * 1.02)
                    info.MemoryUsed = (float)Math.Max(0, totalGb - info.MemoryAvailable);
            }
            catch { }
        }
    }

    private void CollectNetworkData(Computer computer, HardwareInfo info)
    {
        // Суммируем ПО ВСЕМ адаптерам: раньше брался только первый NIC
        // (часто отключённый/Bluetooth) и сеть показывала нули
        float down = 0, up = 0;
        foreach (var network in computer.Hardware.Where(h => h.HardwareType == HardwareType.Network))
        {
            foreach (var sensor in network.Sensors)
            {
                if (!sensor.Value.HasValue) continue;
                if (sensor.SensorType == SensorType.Throughput && sensor.Name.Contains("Download"))
                    down += sensor.Value.Value;
                else if (sensor.SensorType == SensorType.Throughput && sensor.Name.Contains("Upload"))
                    up += sensor.Value.Value;
            }
        }
        info.NetworkDownload = down;
        info.NetworkUpload = up;
    }

    private void CollectStorageData(Computer computer, HardwareInfo info)
    {
        var storage = computer.Hardware.Where(h => h.HardwareType == HardwareType.Storage);
        foreach (var drive in storage)
        {
            var driveInfo = new DriveInfoModel
            {
                Name = drive.Name.Trim()
            };

            foreach (var sensor in drive.Sensors)
            {
                if (!sensor.Value.HasValue) continue;

                switch (sensor.SensorType)
                {
                    case SensorType.Temperature:
                        driveInfo.Temperature = sensor.Value.Value;
                        break;
                    case SensorType.Data when sensor.Name.Contains("Used"):
                        driveInfo.UsedSpace = sensor.Value.Value;
                        break;
                    case SensorType.Data when sensor.Name.Contains("Total"):
                        driveInfo.TotalSpace = sensor.Value.Value;
                        break;
                    case SensorType.Throughput when sensor.Name.Contains("Read"):
                        driveInfo.ReadSpeed = sensor.Value.Value;
                        break;
                    case SensorType.Throughput when sensor.Name.Contains("Write"):
                        driveInfo.WriteSpeed = sensor.Value.Value;
                        break;
                }
            }

            info.Drives.Add(driveInfo);
        }
    }

    public void Stop()
    {
        _isMonitoring = false;
        _timer?.Stop();
        _timer?.Dispose();
        _computer?.Close();
        _computer = null;
    }

    // Синхронный one-shot замер CPU (нагрузка + температура) для стресс-теста.
    public (double Load, double Temp) ReadCpuOnce()
    {
        var computer = new Computer { IsCpuEnabled = true };
        try
        {
            computer.Open();
            foreach (var h in computer.Hardware) h.Update();
            var cpu = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
            if (cpu == null) return (0, 0);
            double load = 0, temp = 0;
            foreach (var s in cpu.Sensors)
            {
                if (!s.Value.HasValue) continue;
                if (s.SensorType == LibreHardwareMonitor.Hardware.SensorType.Load && s.Name.Contains("Total")) load = s.Value.Value;
                if (s.SensorType == LibreHardwareMonitor.Hardware.SensorType.Temperature && temp == 0) temp = s.Value.Value;
            }
            return (Math.Round(load, 1), Math.Round(temp, 1));
        }
        catch { return (0, 0); }
        finally { try { computer.Close(); } catch { } }
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}

public class HardwareInfo
{
    public string CpuName { get; set; } = "CPU";
    public float CpuLoad { get; set; }
    public float CpuTemperature { get; set; }
    public float CpuClock { get; set; }
    public float CpuPower { get; set; }

    public string GpuName { get; set; } = "GPU";
    public float GpuLoad { get; set; }
    public float GpuTemperature { get; set; }
    public float GpuMemoryUsed { get; set; }
    public float GpuMemoryTotal { get; set; }

    public float MemoryUsed { get; set; }
    public float MemoryAvailable { get; set; }
    public float MemoryLoad { get; set; }

    public float NetworkUpload { get; set; }
    public float NetworkDownload { get; set; }

    public List<DriveInfoModel> Drives { get; set; } = new();
}

public class DriveInfoModel
{
    public string Name { get; set; } = "";
    public float Temperature { get; set; }
    public float UsedSpace { get; set; }
    public float TotalSpace { get; set; }
    public float ReadSpeed { get; set; }
    public float WriteSpeed { get; set; }
}