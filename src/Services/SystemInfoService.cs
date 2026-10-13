using System;
using System.Linq;
using System.Management;
using Microsoft.Win32;

namespace WhateverITPCCare.Services;

/// <summary>Reads basic PC facts for the dashboard header. All reads are safe
/// and read-only; WMI failures degrade gracefully to "Unknown".</summary>
public class SystemInfoService
{
    public string MachineName => SafeGet(() => Environment.MachineName);
    public string UserName => SafeGet(() => Environment.UserName);

    public string WindowsVersion => SafeGet(() =>
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        string product = k?.GetValue("ProductName")?.ToString() ?? "Windows";
        string display = k?.GetValue("DisplayVersion")?.ToString() ?? "";
        string build = k?.GetValue("CurrentBuild")?.ToString() ?? "";
        // Windows 11 still reports "Windows 10" in ProductName on older builds
        if (int.TryParse(build, out int b) && b >= 22000)
            product = product.Replace("Windows 10", "Windows 11");
        return $"{product} {display} (build {build})".Trim();
    });

    public string Processor => SafeGetWmi("Win32_Processor", "Name");

    public string TotalRam => SafeGet(() =>
    {
        double gb = GetTotalRamBytes() / 1024d / 1024d / 1024d;
        return gb > 0 ? $"{gb:0.0} GB" : "Unknown";
    });

    public double GetTotalRamBytes() => SafeGet(() =>
    {
        using var s = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem");
        foreach (ManagementObject mo in s.Get())
            return Convert.ToDouble(mo["TotalVisibleMemorySize"]) * 1024d; // KB -> bytes
        return 0d;
    }, 0d);

    public double GetFreeRamBytes() => SafeGet(() =>
    {
        using var s = new ManagementObjectSearcher("SELECT FreePhysicalMemory FROM Win32_OperatingSystem");
        foreach (ManagementObject mo in s.Get())
            return Convert.ToDouble(mo["FreePhysicalMemory"]) * 1024d;
        return 0d;
    }, 0d);

    public TimeSpan Uptime => SafeGet(() => TimeSpan.FromMilliseconds(Environment.TickCount64), TimeSpan.Zero);

    // ---- helpers ----
    private static string SafeGetWmi(string cls, string prop)
    {
        try
        {
            using var s = new ManagementObjectSearcher($"SELECT {prop} FROM {cls}");
            foreach (ManagementObject mo in s.Get())
                return mo[prop]?.ToString()?.Trim() ?? "Unknown";
        }
        catch { }
        return "Unknown";
    }

    private static T SafeGet<T>(Func<T> f, T fallback)
    {
        try { return f(); } catch { return fallback; }
    }
    private static string SafeGet(Func<string> f) => SafeGet(f, "Unknown");
}
