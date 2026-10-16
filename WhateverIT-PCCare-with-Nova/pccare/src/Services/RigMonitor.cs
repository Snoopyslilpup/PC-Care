using System;
using System.IO;
using System.Management;
using LibreHardwareMonitor.Hardware;

namespace WhateverITPCCare.Services;

/// <summary>
/// A single live snapshot of the machine for "The Rig" dashboard. Every value
/// is nullable - when a sensor is not available (e.g. the app is not running as
/// administrator, or the hardware has no such sensor) the value is simply null
/// and the UI shows a dash instead of a wrong number.
/// </summary>
public sealed class RigSnapshot
{
    public double? CpuTempC { get; set; }
    public double? GpuTempC { get; set; }
    public double? CpuClockMhz { get; set; }
    public double? GpuClockMhz { get; set; }
    public double? CpuLoadPct { get; set; }
    public double? GpuLoadPct { get; set; }
    public double? CpuVoltage { get; set; }
    public int? CpuFanRpm { get; set; }
    public int? GpuFanRpm { get; set; }
    public double? RamUsedGB { get; set; }
    public double? RamTotalGB { get; set; }
    public double? StorageUsedGB { get; set; }
    public double? StorageTotalGB { get; set; }
    public string? CpuName { get; set; }
    public string? GpuName { get; set; }

    /// <summary>Best CPU-ish temperature read from the motherboard / SuperIO /
    /// embedded-controller chip (e.g. a sensor literally named "CPU", or
    /// "Tctl"/"Tdie"/"Package"). Used only as a fallback when the CPU driver
    /// itself exposes no temperature. Not shown directly in the UI.</summary>
    internal double? MoboCpuTempC { get; set; }

    public double? RamPct =>
        RamTotalGB.HasValue && RamTotalGB > 0 && RamUsedGB.HasValue
            ? Math.Round(RamUsedGB.Value / RamTotalGB.Value * 100, 0)
            : (double?)null;

    public double? StoragePct =>
        StorageTotalGB.HasValue && StorageTotalGB > 0 && StorageUsedGB.HasValue
            ? Math.Round(StorageUsedGB.Value / StorageTotalGB.Value * 100, 0)
            : (double?)null;
}

/// <summary>
/// Reads live CPU / GPU / RAM / storage figures for "The Rig" dashboard.
/// Temperatures, clocks, voltages and fan speeds come from the built-in sensor
/// reader (needs administrator rights); RAM and storage come from Windows and
/// work for every user. Everything is best-effort and never throws out.
/// </summary>
public sealed class RigMonitor
{
    private Computer? _computer;
    private readonly object _lock = new();

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);
        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (IHardware sub in hardware.SubHardware) sub.Accept(this);
        }
        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }

    public RigSnapshot Read()
    {
        var snap = new RigSnapshot();
        ReadSensors(snap);
        // If the low-level CPU driver did not expose a CPU temperature (some
        // chipsets, or a sensor that only the chipset's own tool reads), use
        // the next best source so the dial still shows a real value:
        //   1) a CPU-named temperature from the motherboard / SuperIO chip,
        //   2) failing that, the Windows ACPI thermal zone.
        if (snap.CpuTempC == null && snap.MoboCpuTempC != null)
            snap.CpuTempC = Math.Round(snap.MoboCpuTempC.Value, 1);
        if (snap.CpuTempC == null)
        {
            double? acpi = ReadAcpiThermalZoneC();
            if (acpi != null) snap.CpuTempC = Math.Round(acpi.Value, 1);
        }
        ReadMemory(snap);
        ReadStorage(snap);
        return snap;
    }

    /// <summary>
    /// Reads the Windows ACPI thermal zone (root\WMI). Widely available even
    /// when the low-level CPU driver cannot. Returns null when the PC does not
    /// publish it. Value is in Celsius.
    /// </summary>
    private static double? ReadAcpiThermalZoneC()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            double? hottest = null;
            foreach (ManagementObject mo in searcher.Get())
            {
                double tenthKelvin = Convert.ToDouble(mo["CurrentTemperature"]);
                double c = tenthKelvin / 10.0 - 273.15;
                if (c > 0 && c < 150 && (hottest == null || c > hottest))
                    hottest = c;
            }
            return hottest;
        }
        catch { return null; }
    }

    private void ReadSensors(RigSnapshot snap)
    {
        try
        {
            lock (_lock)
            {
                if (_computer == null)
                {
                    var c = new Computer
                    {
                        IsCpuEnabled = true,
                        IsGpuEnabled = true,
                        IsMotherboardEnabled = true,
                    };
                    c.Open();
                    _computer = c;
                }

                _computer.Accept(new UpdateVisitor());
                foreach (IHardware hw in _computer.Hardware)
                    Harvest(hw, snap);
            }
        }
        catch { /* driver unavailable (usually: not admin) - leave values null */ }
    }

    private static void Harvest(IHardware hw, RigSnapshot snap)
    {
        bool isCpu = hw.HardwareType == HardwareType.Cpu;
        bool isGpu = hw.HardwareType == HardwareType.GpuNvidia
                  || hw.HardwareType == HardwareType.GpuAmd
                  || hw.HardwareType == HardwareType.GpuIntel;
        // Motherboard super-I/O / embedded-controller chips also carry a CPU
        // temperature on many boards; we read it as a fallback source.
        bool isMobo = hw.HardwareType == HardwareType.Motherboard
                   || hw.HardwareType == HardwareType.SuperIO
                   || hw.HardwareType == HardwareType.EmbeddedController;

        if (isCpu && string.IsNullOrEmpty(snap.CpuName)) snap.CpuName = hw.Name;
        if (isGpu && string.IsNullOrEmpty(snap.GpuName)) snap.GpuName = hw.Name;

        foreach (ISensor s in hw.Sensors)
        {
            if (!s.Value.HasValue) continue;
            double v = s.Value.Value;
            string name = s.Name ?? "";

            switch (s.SensorType)
            {
                case SensorType.Temperature:
                    if (v > 0 && v < 150)
                    {
                        if (isCpu) snap.CpuTempC = Hotter(snap.CpuTempC, v);
                        else if (isGpu) snap.GpuTempC = Hotter(snap.GpuTempC, v);
                        else if (isMobo && IsCpuTempName(name))
                            snap.MoboCpuTempC = Hotter(snap.MoboCpuTempC, v);
                    }
                    break;

                case SensorType.Clock:
                    if (isCpu && name.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0)
                        snap.CpuClockMhz = Hotter(snap.CpuClockMhz, v);
                    else if (isGpu && name.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0)
                        snap.GpuClockMhz = Hotter(snap.GpuClockMhz, v);
                    break;

                case SensorType.Load:
                    if (isCpu && name.IndexOf("Total", StringComparison.OrdinalIgnoreCase) >= 0)
                        snap.CpuLoadPct = Math.Round(v, 0);
                    else if (isGpu && name.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0)
                        snap.GpuLoadPct = Math.Round(v, 0);
                    break;

                case SensorType.Voltage:
                    if (isCpu && snap.CpuVoltage == null &&
                        name.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0)
                        snap.CpuVoltage = Math.Round(v, 3);
                    break;

                case SensorType.Fan:
                    int rpm = (int)Math.Round(v);
                    if (rpm > 0)
                    {
                        if (isGpu) snap.GpuFanRpm = Math.Max(snap.GpuFanRpm ?? 0, rpm);
                        else snap.CpuFanRpm = Math.Max(snap.CpuFanRpm ?? 0, rpm);
                    }
                    break;
            }
        }

        foreach (IHardware sub in hw.SubHardware)
            Harvest(sub, snap);
    }

    private static double Hotter(double? current, double candidate) =>
        current == null ? candidate : Math.Max(current.Value, candidate);

    /// <summary>True when a motherboard temperature sensor's name looks like it
    /// reports the CPU/processor temperature (board labels vary a lot).</summary>
    private static bool IsCpuTempName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string[] hints = { "CPU", "Tctl", "Tdie", "Package", "Processor", "Core" };
        foreach (string h in hints)
            if (name.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        return false;
    }

    private static void ReadMemory(RigSnapshot snap)
    {
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT FreePhysicalMemory,TotalVisibleMemorySize FROM Win32_OperatingSystem");
            foreach (ManagementObject mo in s.Get())
            {
                double freeKb = Convert.ToDouble(mo["FreePhysicalMemory"]);
                double totalKb = Convert.ToDouble(mo["TotalVisibleMemorySize"]);
                snap.RamTotalGB = Math.Round(totalKb / 1024.0 / 1024.0, 1);
                snap.RamUsedGB = Math.Round((totalKb - freeKb) / 1024.0 / 1024.0, 1);
                break;
            }
        }
        catch { }
    }

    private static void ReadStorage(RigSnapshot snap)
    {
        try
        {
            string sysRoot =
                Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\";
            var d = new DriveInfo(sysRoot);
            if (d.IsReady)
            {
                double totalGb = d.TotalSize / 1024.0 / 1024.0 / 1024.0;
                double freeGb = d.TotalFreeSpace / 1024.0 / 1024.0 / 1024.0;
                snap.StorageTotalGB = Math.Round(totalGb, 0);
                snap.StorageUsedGB = Math.Round(totalGb - freeGb, 0);
            }
        }
        catch { }
    }
}
