using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;

namespace WhateverITPCCare.Services;

/// <summary>One device driver we care about, with its current version/date and a
/// plain-English hint about whether it is worth checking for an update.</summary>
public sealed class DriverInfo
{
    public string Device { get; set; } = "";
    public string Category { get; set; } = "";   // Graphics / Network / Storage / Chipset
    public string Version { get; set; } = "—";
    public DateTime? Date { get; set; }
    public string Vendor { get; set; } = "";

    /// <summary>True when the driver is old enough to be worth checking.</summary>
    public bool Stale { get; set; }

    public string DateText => Date.HasValue ? Date.Value.ToString("d MMM yyyy") : "—";
    public string AgeText
    {
        get
        {
            if (!Date.HasValue) return "age unknown";
            int days = (int)(DateTime.Now - Date.Value).TotalDays;
            if (days < 0) days = 0;
            double years = days / 365.0;
            return years >= 1 ? $"{years:0.0} yr old" : $"{days} days old";
        }
    }
    public string Advice => Stale
        ? "Worth checking the maker's site for a newer driver."
        : "Looks current - no action needed.";
}

/// <summary>
/// Read-only driver awareness. Reads the CURRENT driver version and date for
/// the key devices (graphics, network, storage controllers, chipset) straight
/// from Windows (Win32_PnPSignedDriver) and flags any that are old enough to be
/// worth checking for an update.
///
/// HONEST BY DESIGN: it does NOT claim to know the latest available version and
/// never downloads or installs anything - it surfaces what you have, with the
/// vendor's official download page, and leaves the actual update to you or a
/// Whatever IT technician.
/// </summary>
public sealed class DriverScanner
{
    // How old (in days) before we suggest checking for an update.
    private const int StaleDays = 730; // ~2 years

    public IReadOnlyList<DriverInfo> Scan()
    {
        var list = new List<DriverInfo>();
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT DeviceName, DriverVersion, DriverDate, DeviceClass, Manufacturer FROM Win32_PnPSignedDriver");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ManagementObject mo in s.Get())
            {
                string cls = (mo["DeviceClass"]?.ToString() ?? "").ToUpperInvariant();
                string? category = cls switch
                {
                    "DISPLAY" => "Graphics",
                    "NET" => "Network",
                    "SCSIADAPTER" => "Storage",
                    "HDC" => "Storage",
                    "SYSTEM" => "Chipset",
                    _ => null
                };
                if (category == null) continue;

                string device = (mo["DeviceName"]?.ToString() ?? "").Trim();
                if (string.IsNullOrEmpty(device)) continue;
                // Chipset class is noisy - keep only the obviously useful ones.
                if (category == "Chipset" &&
                    !(device.Contains("Chipset", StringComparison.OrdinalIgnoreCase) ||
                      device.Contains("SMBus", StringComparison.OrdinalIgnoreCase) ||
                      device.Contains("PCI Express", StringComparison.OrdinalIgnoreCase)))
                    continue;

                string key = category + "|" + device;
                if (!seen.Add(key)) continue;

                DateTime? date = ParseCimDate(mo["DriverDate"]?.ToString());
                var info = new DriverInfo
                {
                    Device = device,
                    Category = category,
                    Version = (mo["DriverVersion"]?.ToString() ?? "—").Trim(),
                    Date = date,
                    Vendor = (mo["Manufacturer"]?.ToString() ?? "").Trim(),
                    Stale = date.HasValue && (DateTime.Now - date.Value).TotalDays > StaleDays
                };
                list.Add(info);
            }
        }
        catch { /* WMI unavailable - return whatever we have */ }

        // Graphics first (gamers care most), then Network, Storage, Chipset.
        int Rank(string c) => c switch
        {
            "Graphics" => 0, "Network" => 1, "Storage" => 2, "Chipset" => 3, _ => 4
        };
        return list.OrderBy(d => Rank(d.Category))
                   .ThenByDescending(d => d.Stale)
                   .ThenBy(d => d.Device, StringComparer.OrdinalIgnoreCase)
                   .ToList();
    }

    // WMI dates look like "20231104000000.000000+000".
    private static DateTime? ParseCimDate(string? cim)
    {
        if (string.IsNullOrWhiteSpace(cim) || cim.Length < 8) return null;
        try
        {
            return DateTime.ParseExact(cim.Substring(0, 8), "yyyyMMdd",
                CultureInfo.InvariantCulture, DateTimeStyles.None);
        }
        catch { return null; }
    }
}
