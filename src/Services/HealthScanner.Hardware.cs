using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using WhateverITPCCare.Models;

namespace WhateverITPCCare.Services;

// Hardware diagnostics. Everything here is READ-ONLY and only reads what
// Windows itself exposes while the PC is running (WMI / CIM). Sensors a given
// PC's firmware does not publish (common on desktops) degrade gracefully to an
// informational line rather than a scary "error".
public partial class HealthScanner
{
    private IEnumerable<HealthItem> CheckCpu()
    {
        var list = new List<HealthItem>();
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Name,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed,LoadPercentage FROM Win32_Processor");
            foreach (ManagementObject mo in s.Get())
            {
                string name = mo["Name"]?.ToString()?.Trim() ?? "Processor";
                int cores = ToInt(mo["NumberOfCores"]);
                int threads = ToInt(mo["NumberOfLogicalProcessors"]);
                int mhz = ToInt(mo["MaxClockSpeed"]);
                int load = ToInt(mo["LoadPercentage"]);
                var status = load >= 95 ? HealthStatus.Warning : HealthStatus.Good;
                list.Add(new HealthItem
                {
                    Category = "Hardware",
                    Title = "Processor (CPU)",
                    Status = status,
                    Detail = $"{name} \u2013 {cores} cores / {threads} threads @ {mhz / 1000.0:0.0} GHz, {load}% busy now",
                    Explanation = status == HealthStatus.Good
                        ? "The CPU is present and responding normally."
                        : "The CPU is almost maxed out right now. If it stays this high the PC will " +
                          "feel slow – check Task Manager for a program using all the power."
                });
            }
        }
        catch { }
        if (list.Count == 0)
            list.Add(Info("Hardware", "Processor (CPU)", "CPU details were not available from Windows."));
        return list;
    }

    private IEnumerable<HealthItem> CheckCpuTemperature()
    {
        double? hottest = null;
        try
        {
            using var s = new ManagementObjectSearcher(
                @"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            foreach (ManagementObject mo in s.Get())
            {
                double tenthKelvin = Convert.ToDouble(mo["CurrentTemperature"]);
                double celsius = tenthKelvin / 10.0 - 273.15;
                if (celsius > 0 && celsius < 150 && (hottest == null || celsius > hottest))
                    hottest = celsius;
            }
        }
        catch { }

        if (hottest == null)
        {
            yield return Info("Hardware", "Temperature",
                "This PC's firmware does not share temperature sensors with Windows (common on " +
                "desktops). The exact temperatures can be read from the BIOS/UEFI screen or the " +
                "motherboard maker's own tool.");
            yield break;
        }

        var status = hottest > 90 ? HealthStatus.Critical
                   : hottest > 80 ? HealthStatus.Warning
                   : HealthStatus.Good;
        yield return new HealthItem
        {
            Category = "Hardware",
            Title = "Temperature (thermal zone)",
            Status = status,
            Detail = $"About {hottest:0} \u00b0C",
            Explanation = status == HealthStatus.Good
                ? "The system thermal sensor is in a normal range."
                : "The PC is running hot. Heat makes it throttle and shut down. Check the fans are " +
                  "spinning and clear of dust, and that vents are not blocked."
        };
    }

    private IEnumerable<HealthItem> CheckGpu()
    {
        var list = new List<HealthItem>();
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Name,DriverVersion,CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate,Status FROM Win32_VideoController");
            foreach (ManagementObject mo in s.Get())
            {
                string name = mo["Name"]?.ToString()?.Trim() ?? "Display adapter";
                string drv = mo["DriverVersion"]?.ToString()?.Trim() ?? "?";
                int w = ToInt(mo["CurrentHorizontalResolution"]);
                int h = ToInt(mo["CurrentVerticalResolution"]);
                int hz = ToInt(mo["CurrentRefreshRate"]);
                string res = w > 0 && h > 0 ? $"{w}x{h}" + (hz > 0 ? $" @ {hz} Hz" : "") : "not active";
                list.Add(new HealthItem
                {
                    Category = "Hardware",
                    Title = "Graphics (GPU)",
                    Status = HealthStatus.Good,
                    Detail = $"{name} \u2013 {res}, driver {drv}",
                    Explanation = "Graphics adapter detected and its driver is loaded. If games or video " +
                                  "look wrong, updating this driver is usually the fix."
                });
            }
        }
        catch { }
        if (list.Count == 0)
            list.Add(Info("Hardware", "Graphics (GPU)", "No display adapter information was available."));
        return list;
    }

    private IEnumerable<HealthItem> CheckMemoryModules()
    {
        var list = new List<HealthItem>();
        try
        {
            long totalBytes = 0; int modules = 0; int speed = 0;
            using (var s = new ManagementObjectSearcher(
                "SELECT Capacity,Speed FROM Win32_PhysicalMemory"))
            foreach (ManagementObject mo in s.Get())
            {
                totalBytes += ToLong(mo["Capacity"]);
                int sp = ToInt(mo["Speed"]);
                if (sp > speed) speed = sp;
                modules++;
            }

            int slots = 0;
            try
            {
                using var a = new ManagementObjectSearcher(
                    "SELECT MemoryDevices FROM Win32_PhysicalMemoryArray");
                foreach (ManagementObject mo in a.Get()) slots += ToInt(mo["MemoryDevices"]);
            }
            catch { }

            if (modules > 0)
            {
                string slotText = slots > 0 ? $", {modules} of {slots} slots used" : $", {modules} module(s)";
                string speedText = speed > 0 ? $" at {speed} MHz" : "";
                list.Add(new HealthItem
                {
                    Category = "Hardware",
                    Title = "Memory modules (RAM sticks)",
                    Status = HealthStatus.Good,
                    Detail = $"{Fmt(totalBytes)} installed{speedText}{slotText}",
                    Explanation = slots > modules
                        ? "RAM is installed and reporting fine. There are free memory slots, so this PC " +
                          "can be upgraded with more RAM later if needed."
                        : "RAM is installed and reporting fine. All memory slots are in use."
                });
            }
        }
        catch { }
        if (list.Count == 0)
            list.Add(Info("Hardware", "Memory modules (RAM sticks)", "Physical memory details were not available."));
        return list;
    }

    private IEnumerable<HealthItem> CheckPhysicalDisks()
    {
        var list = new List<HealthItem>();

        // Preferred modern source: the Storage namespace knows SSD vs HDD and a
        // rolled-up health status.
        try
        {
            using var s = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                "SELECT FriendlyName,MediaType,HealthStatus,Size FROM MSFT_PhysicalDisk");
            foreach (ManagementObject mo in s.Get())
            {
                string name = mo["FriendlyName"]?.ToString()?.Trim() ?? "Disk";
                int media = ToInt(mo["MediaType"]);
                int health = ToInt(mo["HealthStatus"]);
                long size = ToLong(mo["Size"]);
                string type = media switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "Disk" };
                var status = health switch
                {
                    0 => HealthStatus.Good,       // Healthy
                    1 => HealthStatus.Warning,    // Warning
                    2 => HealthStatus.Critical,   // Unhealthy
                    _ => HealthStatus.Info
                };
                list.Add(new HealthItem
                {
                    Category = "Hardware",
                    Title = $"Storage device \u2013 {name}",
                    Status = status,
                    Detail = $"{type}, {Fmt(size)}, health: {(health == 0 ? "Healthy" : health == 1 ? "Warning" : health == 2 ? "Unhealthy" : "Unknown")}",
                    Explanation = status == HealthStatus.Good
                        ? "The drive reports itself as healthy."
                        : "Windows reports this drive is not fully healthy. Back up anything important " +
                          "now and have the drive checked – drives can fail suddenly."
                });
            }
        }
        catch { }

        // SMART failure prediction (older but widely supported).
        try
        {
            using var s = new ManagementObjectSearcher(
                @"root\WMI", "SELECT InstanceName,PredictFailure FROM MSStorageDriver_FailurePredictStatus");
            foreach (ManagementObject mo in s.Get())
            {
                bool predict = false;
                try { predict = Convert.ToBoolean(mo["PredictFailure"]); } catch { }
                string inst = mo["InstanceName"]?.ToString()?.Trim() ?? "drive";
                list.Add(new HealthItem
                {
                    Category = "Hardware",
                    Title = "Drive early-warning (SMART)",
                    Status = predict ? HealthStatus.Critical : HealthStatus.Good,
                    Detail = predict ? "A drive is predicting failure" : "No drive is predicting failure",
                    Explanation = predict
                        ? "A drive's own SMART self-test is warning it may fail soon. Back up your files " +
                          "immediately and replace the drive."
                        : "Drives' built-in SMART self-tests are not predicting any failure."
                });
                break; // one summary line is enough
            }
        }
        catch { }

        if (list.Count == 0)
            list.Add(Info("Hardware", "Storage devices", "Physical disk details were not available on this PC."));
        return list;
    }

    private IEnumerable<HealthItem> CheckBatteryHealth()
    {
        long design = 0, full = 0;
        try
        {
            using (var s = new ManagementObjectSearcher(@"root\WMI",
                "SELECT DesignedCapacity FROM BatteryStaticData"))
            foreach (ManagementObject mo in s.Get()) design += ToLong(mo["DesignedCapacity"]);

            using (var s = new ManagementObjectSearcher(@"root\WMI",
                "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity"))
            foreach (ManagementObject mo in s.Get()) full += ToLong(mo["FullChargedCapacity"]);
        }
        catch { }

        if (design <= 0 || full <= 0)
        {
            yield return Info("Hardware", "Battery wear",
                "No battery wear data – this is normal for a desktop PC, or the battery does not " +
                "report design capacity.");
            yield break;
        }

        double healthPct = 100.0 * full / design;
        var status = healthPct < 60 ? HealthStatus.Warning : HealthStatus.Good;
        yield return new HealthItem
        {
            Category = "Hardware",
            Title = "Battery wear",
            Status = status,
            Detail = $"{healthPct:0}% of original capacity left",
            Explanation = status == HealthStatus.Good
                ? "The battery still holds a good share of its original charge."
                : "The battery has worn down and now holds much less than when new, so it will not " +
                  "last as long on a charge. A battery replacement would restore run time."
        };
    }

    private IEnumerable<HealthItem> CheckSystemBoard()
    {
        string maker = "", model = "", board = "", bios = "", biosDate = "";
        try
        {
            using (var s = new ManagementObjectSearcher("SELECT Manufacturer,Model FROM Win32_ComputerSystem"))
            foreach (ManagementObject mo in s.Get())
            {
                maker = mo["Manufacturer"]?.ToString()?.Trim() ?? "";
                model = mo["Model"]?.ToString()?.Trim() ?? "";
            }
            using (var s = new ManagementObjectSearcher("SELECT Manufacturer,Product FROM Win32_BaseBoard"))
            foreach (ManagementObject mo in s.Get())
                board = $"{mo["Manufacturer"]?.ToString()?.Trim()} {mo["Product"]?.ToString()?.Trim()}".Trim();
            using (var s = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion,ReleaseDate FROM Win32_BIOS"))
            foreach (ManagementObject mo in s.Get())
            {
                bios = mo["SMBIOSBIOSVersion"]?.ToString()?.Trim() ?? "";
                string raw = mo["ReleaseDate"]?.ToString() ?? "";
                if (raw.Length >= 8) biosDate = $"{raw.Substring(0, 4)}-{raw.Substring(4, 2)}-{raw.Substring(6, 2)}";
            }
        }
        catch { }

        string sys = string.Join(" ", new[] { maker, model }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(sys)) parts.Add($"System: {sys}");
        if (!string.IsNullOrWhiteSpace(board)) parts.Add($"Board: {board}");
        if (!string.IsNullOrWhiteSpace(bios)) parts.Add($"BIOS: {bios}{(biosDate != "" ? $" ({biosDate})" : "")}");

        yield return new HealthItem
        {
            Category = "Hardware",
            Title = "Motherboard & BIOS",
            Status = HealthStatus.Info,
            Detail = parts.Count > 0 ? string.Join("  \u2022  ", parts) : "Details not available",
            Explanation = "Identifying details for this PC's mainboard and firmware. Useful when looking " +
                          "up drivers or a BIOS update from the maker's website."
        };
    }

    // ---- hardware helpers ----
    private static int ToInt(object? v)
    {
        try { return v == null ? 0 : Convert.ToInt32(v); } catch { return 0; }
    }

    private static long ToLong(object? v)
    {
        try { return v == null ? 0 : Convert.ToInt64(v); } catch { return 0; }
    }
}
