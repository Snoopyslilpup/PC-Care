using System;
using System.Collections.Generic;
using WhateverITPCCare.Models;

namespace WhateverITPCCare.Services;

/// <summary>
/// Runs a set of read-only diagnostic checks and returns a plain-English report.
/// Nothing here changes the system – scanning is always safe. Each check is
/// wrapped so one failing check never aborts the whole scan.
/// </summary>
public partial class HealthScanner
{
    private readonly SystemInfoService _sys = new();

    public IReadOnlyList<HealthItem> Scan(Action<string>? progress = null)
    {
        var items = new List<HealthItem>();

        void Step(string label, Func<IEnumerable<HealthItem>> check)
        {
            progress?.Invoke(label);
            try { items.AddRange(check()); }
            catch (Exception ex)
            {
                items.Add(new HealthItem
                {
                    Category = "Diagnostics",
                    Title = label,
                    Status = HealthStatus.Info,
                    Detail = "Could not complete this check.",
                    Explanation = $"This check could not run on your PC ({ex.Message}). This is not harmful."
                });
            }
        }

        Step("Checking disk space…", CheckDiskSpace);
        Step("Checking drive health…", CheckDriveHealth);
        Step("Checking memory…", CheckMemory);
        Step("Checking temporary files…", CheckTempFiles);
        Step("Checking recycle bin…", CheckRecycleBin);
        Step("Checking startup programs…", CheckStartupPrograms);
        Step("Checking security software…", CheckSecurity);
        Step("Checking Windows Firewall…", CheckFirewall);
        Step("Checking for a pending restart…", CheckPendingReboot);
        Step("Checking recent system errors…", CheckEventErrors);
        Step("Checking battery…", CheckBattery);
        Step("Checking uptime…", CheckUptime);

        // ---- Hardware checks (read-only, everything Windows exposes live) ----
        Step("Checking processor…", CheckCpu);
        Step("Checking temperature…", CheckCpuTemperature);
        Step("Checking graphics…", CheckGpu);
        Step("Checking memory modules…", CheckMemoryModules);
        Step("Checking physical disks…", CheckPhysicalDisks);
        Step("Checking battery wear…", CheckBatteryHealth);
        Step("Checking motherboard & BIOS…", CheckSystemBoard);

        progress?.Invoke("Finishing up…");
        return items;
    }

    /// <summary>0-100 overall score derived from the report.</summary>
    public static int Score(IReadOnlyList<HealthItem> items)
    {
        if (items.Count == 0) return 100;
        int penalty = 0;
        foreach (var i in items)
        {
            penalty += i.Status switch
            {
                HealthStatus.Critical => 18,
                HealthStatus.Warning => 7,
                _ => 0
            };
        }
        return Math.Max(5, 100 - penalty);
    }
}
