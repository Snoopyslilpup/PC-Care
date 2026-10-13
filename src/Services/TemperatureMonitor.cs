using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;

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
        double? celsius = ReadHottestCelsius(out string? source);
        int? fan = ReadFanRpm();

        var result = new TemperatureReading { FanRpm = fan, Source = source };

        if (celsius == null)
        {
            result.HasTemperature = false;
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
