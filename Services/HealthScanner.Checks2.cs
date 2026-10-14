using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using Microsoft.Win32;
using WhateverITPCCare.Models;

namespace WhateverITPCCare.Services;

public partial class HealthScanner
{
    private IEnumerable<HealthItem> CheckStartupPrograms()
    {
        // Programs the user has already switched OFF in Task Manager still leave
        // their Run-key value in place; Windows just records the "disabled"
        // state separately under StartupApproved. If we count the raw Run keys
        // the number never drops after the user disables things – the classic
        // "I fixed it but the scan still flags it" loop. So we skip anything
        // marked disabled.
        var disabledRun = DisabledStartup(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
        var disabledFolder = DisabledStartup(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder");

        int count = 0;
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var k = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
            if (k == null) continue;
            foreach (var name in k.GetValueNames())
                if (!string.IsNullOrEmpty(name) && !disabledRun.Contains(name)) count++;
        }
        try
        {
            string startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            if (Directory.Exists(startup))
                foreach (var f in Directory.GetFiles(startup, "*.lnk"))
                    if (!disabledFolder.Contains(Path.GetFileName(f))) count++;
        }
        catch { }

        var status = count > 12 ? HealthStatus.Warning : HealthStatus.Good;
        yield return new HealthItem
        {
            Category = "Performance",
            Title = "Programs that start with Windows",
            Status = status,
            Detail = $"{count} startup item{(count == 1 ? "" : "s")} enabled",
            Explanation = status == HealthStatus.Good
                ? "A reasonable number of programs start automatically."
                : "Many programs launch at startup, which can slow down how long the PC takes to " +
                  "become usable. You can safely turn off ones you do not need – the ones you " +
                  "switch off here will drop off this list on the next scan.",
            RepairId = status == HealthStatus.Good ? null : "startup-manager"
        };
    }

    /// <summary>
    /// Names recorded as DISABLED under a StartupApproved key (across HKCU and
    /// HKLM). In the binary value the first byte is even (2, 6…) when the item
    /// is enabled and odd (3, 7…) when the user disabled it.
    /// </summary>
    private static HashSet<string> DisabledStartup(string subkey)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var k = hive.OpenSubKey(subkey);
                if (k == null) continue;
                foreach (var name in k.GetValueNames())
                {
                    if (k.GetValue(name) is byte[] data && data.Length > 0 && (data[0] & 1) == 1)
                        set.Add(name);
                }
            }
            catch { }
        }
        return set;
    }

    private IEnumerable<HealthItem> CheckSecurity()
    {
        var results = new List<HealthItem>();
        bool found = false;
        try
        {
            using var s = new ManagementObjectSearcher(@"root\SecurityCenter2", "SELECT displayName,productState FROM AntiVirusProduct");
            foreach (ManagementObject mo in s.Get())
            {
                found = true;
                string name = mo["displayName"]?.ToString() ?? "Antivirus";
                int state = Convert.ToInt32(mo["productState"]);
                bool enabled = ((state >> 12) & 0xF) != 0;      // product is on
                bool upToDate = ((state) & 0xF) == 0;            // definitions current
                var st = enabled ? (upToDate ? HealthStatus.Good : HealthStatus.Warning) : HealthStatus.Critical;
                results.Add(new HealthItem
                {
                    Category = "Security",
                    Title = $"Antivirus – {name}",
                    Status = st,
                    Detail = enabled ? (upToDate ? "On and up to date" : "On, but definitions may be out of date") : "Turned off",
                    Explanation = st == HealthStatus.Good
                        ? "Real-time protection is active and current."
                        : "Your PC is better protected when antivirus is on with up-to-date definitions. " +
                          "Open Windows Security to turn it on or update it.",
                    RepairId = st == HealthStatus.Good ? null : "open-security"
                });
            }
        }
        catch { }
        if (!found)
            results.Add(Info("Security", "Antivirus", "No antivirus product was reported by Windows Security Center."));
        return results;
    }

    private IEnumerable<HealthItem> CheckFirewall()
    {
        bool? on = null;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\StandardProfile");
            var v = k?.GetValue("EnableFirewall");
            if (v != null) on = Convert.ToInt32(v) == 1;
        }
        catch { }
        yield return new HealthItem
        {
            Category = "Security",
            Title = "Windows Firewall",
            Status = on == true ? HealthStatus.Good : on == false ? HealthStatus.Warning : HealthStatus.Info,
            Detail = on == true ? "On" : on == false ? "Off" : "Unknown",
            Explanation = on == true
                ? "The firewall is helping block unwanted network connections."
                : "The firewall helps protect your PC from the internet. It is best left on " +
                  "unless a technician asked you to disable it.",
            RepairId = on == false ? "open-security" : null
        };
    }

    private IEnumerable<HealthItem> CheckPendingReboot()
    {
        bool pending = false;
        try
        {
            using var cbs = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            using var wu = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            pending = cbs != null || wu != null;
        }
        catch { }
        yield return new HealthItem
        {
            Category = "Updates",
            Title = "Pending restart",
            Status = pending ? HealthStatus.Warning : HealthStatus.Good,
            Detail = pending ? "A restart is needed to finish updates" : "No restart needed",
            Explanation = pending
                ? "Windows has finished installing something but needs a restart to apply it. " +
                  "Save your work and restart when convenient."
                : "Nothing is waiting on a restart.",
        };
    }

    private IEnumerable<HealthItem> CheckEventErrors()
    {
        int errors = 0;
        bool readable = true;
        try
        {
            var since = DateTime.Now.AddDays(-7);
            using var log = new EventLog("System");
            foreach (EventLogEntry e in log.Entries)
            {
                if (e.EntryType == EventLogEntryType.Error && e.TimeGenerated >= since) errors++;
            }
        }
        catch { readable = false; }

        if (!readable)
        {
            yield return Info("Stability", "Recent system errors", "The system event log could not be read.");
            yield break;
        }

        var status = errors > 60 ? HealthStatus.Warning : HealthStatus.Good;
        yield return new HealthItem
        {
            Category = "Stability",
            Title = "Recent system errors (last 7 days)",
            Status = status,
            Detail = $"{errors} error event{(errors == 1 ? "" : "s")} logged",
            Explanation = status == HealthStatus.Good
                ? "A normal, low number of background errors."
                : "Windows logged a lot of errors recently. This can point to a failing driver or " +
                  "device. If the PC is misbehaving, Whatever IT can look into the specific errors.",
        };
    }

    private IEnumerable<HealthItem> CheckBattery()
    {
        var results = new List<HealthItem>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT EstimatedChargeRemaining FROM Win32_Battery");
            foreach (ManagementObject mo in s.Get())
            {
                int pct = Convert.ToInt32(mo["EstimatedChargeRemaining"]);
                results.Add(new HealthItem
                {
                    Category = "Power",
                    Title = "Battery charge",
                    Status = HealthStatus.Info,
                    Detail = $"{pct}% remaining",
                    Explanation = "Current battery charge level. For long-term battery wear, run the " +
                                  "battery report repair to see design vs. current capacity.",
                    RepairId = "battery-report"
                });
            }
        }
        catch { }
        if (results.Count == 0)
            yield return Info("Power", "Battery", "No battery detected – this looks like a desktop PC.");
        else
            foreach (var r in results) yield return r;
    }

    private IEnumerable<HealthItem> CheckUptime()
    {
        var up = _sys.Uptime;
        var status = up.TotalDays > 7 ? HealthStatus.Warning : HealthStatus.Good;
        yield return new HealthItem
        {
            Category = "Performance",
            Title = "Time since last restart",
            Status = status,
            Detail = up.TotalDays >= 1 ? $"{up.Days}d {up.Hours}h" : $"{up.Hours}h {up.Minutes}m",
            Explanation = status == HealthStatus.Good
                ? "The PC has been restarted recently, which keeps things running smoothly."
                : "It has been a while since the last restart. A quick restart clears out memory " +
                  "and often fixes small slowdowns.",
        };
    }
}
