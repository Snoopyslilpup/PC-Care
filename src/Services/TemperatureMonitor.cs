using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;

namespace WhateverITPCCare.Services;

public enum TempTrend { Unknown, Falling, Steady, Rising }

/// <summary>
/// A single temperature snapshot plus the context we build from the saved
/// history (peak, average, trend) and the current fan speed when the PC
/// exposes one. All read-only.
/// </summary>
public sealed class TemperatureReading
{
    public bool HasTemperature { get; set; }
    public double CurrentC { get; set; }
    public double PeakC { get; set; }
    public double AverageC { get; set; }
    public int SampleCount { get; set; }
    public TempTrend Trend { get; set; } = TempTrend.Unknown;
    public int? FanRpm { get; set; }
    public string? Source { get; set; }

    /// <summary>True when we found no temperature AND the app is not running as
    /// administrator - i.e. running elevated would most likely fix it. Lets the
    /// UI show a helpful "run as administrator" hint instead of a dead end.</summary>
    public bool NeedsAdminHint { get; set; }
}

/// <summary>
/// Reads the CPU/system temperature from whatever source this PC exposes,
/// remembers each reading in a small on-disk log, and reports the trend over
/// time so the app can warn BEFORE heat forces a thermal shutdown.
///
/// Everything here is read-only. Many desktops do not share temperature
/// sensors with Windows at all - in that case HasTemperature is false and the
/// caller shows a friendly "not available" note rather than a scary error.
/// </summary>
public sealed class TemperatureMonitor
{
    // Keep the log tiny: the most recent readings are all we need for a trend.
    private const int MaxSamples = 500;

    private static string LogPath
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WhateverITPCCare");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "temperature-log.csv");
        }
    }

    /// <summary>
    /// Take a reading. When <paramref name="record"/> is true the reading is
    /// appended to the history so the trend builds up over repeated scans.
    /// </summary>
    public TemperatureReading Sample(bool record = true)
    {
        // 1) Preferred: our built-in sensor reader talks to the hardware directly
        //    (CPU, motherboard, GPU) - no third-party tool required.
        double? celsius = ReadBuiltInSensors(out string? source, out int? builtInFan);
        int? fan = builtInFan;

        // 2) Fallbacks: the standard Windows thermal sensor, then any external
        //    hardware-monitor tool that happens to be running.
        if (celsius == null) celsius = ReadHottestCelsius(out source);
        if (fan == null) fan = ReadFanRpm();

        var result = new TemperatureReading { FanRpm = fan, Source = source };

        if (celsius == null)
        {
            result.HasTemperature = false;
            // If we're not elevated, running as administrator will almost always
            // unlock the readings, so tell the UI to say that.
            result.NeedsAdminHint = !IsElevated();
            return result;
        }

        double now = Math.Round(celsius.Value, 1);
        result.HasTemperature = true;
        result.CurrentC = now;

        if (record) TryAppend(now);

        var history = ReadHistory();
        if (history.Count == 0) history.Add((DateTime.Now, now));

        result.SampleCount = history.Count;
        result.PeakC = history.Max(h => h.c);
        result.AverageC = Math.Round(history.Average(h => h.c), 1);
        result.Trend = ComputeTrend(history);
        return result;
    }

    // ---- built-in sensor reader (LibreHardwareMonitor engine) ----
    // Opened once and kept alive for the life of the app, the way a monitoring
    // tool does. All access is locked so repeated scans are safe.
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

    private double? ReadBuiltInSensors(out string? source, out int? fan)
    {
        source = null;
        fan = null;
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
                    c.Open();            // loads the sensor driver (needs admin)
                    _computer = c;
                }

                _computer.Accept(new UpdateVisitor());   // refresh all readings

                double? hottest = null;
                int bestFan = 0;
                string? src = null;
                foreach (IHardware hw in _computer.Hardware)
                    CollectSensors(hw, ref hottest, ref bestFan, ref src);

                source = src;
                if (bestFan > 0) fan = bestFan;
                return hottest;
            }
        }
        catch
        {
            // Driver could not load (usually: not running as administrator) or
            // the hardware is unsupported. Fall back to the other sources.
            return null;
        }
    }

    private static void CollectSensors(IHardware hw, ref double? hottest, ref int bestFan, ref string? source)
    {
        foreach (ISensor sensor in hw.Sensors)
        {
            if (!sensor.Value.HasValue) continue;
            double v = sensor.Value.Value;
            if (sensor.SensorType == SensorType.Temperature && v > 0 && v < 150)
            {
                if (hottest == null || v > hottest)
                {
                    hottest = v;
                    source = "built-in sensor reader";
                }
            }
            else if (sensor.SensorType == SensorType.Fan && v > 0)
            {
                int rpm = (int)Math.Round(v);
                if (rpm > bestFan) bestFan = rpm;
            }
        }
        foreach (IHardware sub in hw.SubHardware)
            CollectSensors(sub, ref hottest, ref bestFan, ref source);
    }

    private static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    // ---- temperature sources ----
    private static double? ReadHottestCelsius(out string? source)
    {
        source = null;
        double? hottest = null;

        // 1) ACPI thermal zone - the most widely available source.
        try
        {
            using var s = new ManagementObjectSearcher(
                @"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            foreach (ManagementObject mo in s.Get())
            {
                double tenthKelvin = Convert.ToDouble(mo["CurrentTemperature"]);
                double c = tenthKelvin / 10.0 - 273.15;
                if (c > 0 && c < 150 && (hottest == null || c > hottest))
                {
                    hottest = c;
                    source = "system thermal sensor";
                }
            }
        }
        catch { }

        // 2) If a sensor tool (e.g. Open/LibreHardwareMonitor) is running it
        //    publishes richer per-core temps here. Optional - ignored if absent.
        if (hottest == null)
        {
            foreach (var ns in new[] { @"root\OpenHardwareMonitor", @"root\LibreHardwareMonitor" })
            {
                try
                {
                    using var s = new ManagementObjectSearcher(ns,
                        "SELECT Value,SensorType,Name FROM Sensor WHERE SensorType='Temperature'");
                    foreach (ManagementObject mo in s.Get())
                    {
                        double c = Convert.ToDouble(mo["Value"]);
                        if (c > 0 && c < 150 && (hottest == null || c > hottest))
                        {
                            hottest = c;
                            source = "hardware monitor sensor";
                        }
                    }
                }
                catch { }
                if (hottest != null) break;
            }
        }

        return hottest;
    }

    private static int? ReadFanRpm()
    {
        // Win32_Fan very rarely reports an RPM, but a running hardware-monitor
        // tool does. Try both; return null (unknown) when neither answers.
        foreach (var ns in new[] { @"root\OpenHardwareMonitor", @"root\LibreHardwareMonitor" })
        {
            try
            {
                using var s = new ManagementObjectSearcher(ns,
                    "SELECT Value,SensorType FROM Sensor WHERE SensorType='Fan'");
                int best = 0;
                foreach (ManagementObject mo in s.Get())
                {
                    int rpm = (int)Math.Round(Convert.ToDouble(mo["Value"]));
                    if (rpm > best) best = rpm;
                }
                if (best > 0) return best;
            }
            catch { }
        }
        return null;
    }

    // ---- trend from history ----
    private static TempTrend ComputeTrend(List<(DateTime t, double c)> history)
    {
        if (history.Count < 4) return TempTrend.Unknown;

        // Compare the average of the most recent 3 readings with the average of
        // the 3 before them. A few degrees of movement is normal noise.
        var ordered = history.OrderBy(h => h.t).ToList();
        var recent = ordered.Skip(Math.Max(0, ordered.Count - 3)).Select(h => h.c).ToList();
        var prior = ordered.Skip(Math.Max(0, ordered.Count - 6)).Take(Math.Min(3, ordered.Count - 3)).Select(h => h.c).ToList();
        if (prior.Count == 0) return TempTrend.Unknown;

        double delta = recent.Average() - prior.Average();
        if (delta >= 3) return TempTrend.Rising;
        if (delta <= -3) return TempTrend.Falling;
        return TempTrend.Steady;
    }

    // ---- history file I/O (best-effort, never throws out) ----
    private static void TryAppend(double celsius)
    {
        try
        {
            File.AppendAllText(LogPath,
                $"{DateTime.Now:O},{celsius.ToString(CultureInfo.InvariantCulture)}{Environment.NewLine}");
            TrimIfNeeded();
        }
        catch { }
    }

    private static List<(DateTime t, double c)> ReadHistory()
    {
        var list = new List<(DateTime, double)>();
        try
        {
            if (!File.Exists(LogPath)) return list;
            foreach (var line in File.ReadLines(LogPath))
            {
                var parts = line.Split(',');
                if (parts.Length < 2) continue;
                if (DateTime.TryParse(parts[0], null, DateTimeStyles.RoundtripKind, out var t) &&
                    double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var c))
                    list.Add((t, c));
            }
        }
        catch { }
        return list;
    }

    private static void TrimIfNeeded()
    {
        try
        {
            var all = File.ReadAllLines(LogPath);
            if (all.Length <= MaxSamples) return;
            File.WriteAllLines(LogPath, all.Skip(all.Length - MaxSamples));
        }
        catch { }
    }
}
