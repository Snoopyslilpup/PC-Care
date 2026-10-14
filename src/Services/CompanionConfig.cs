using System;
using System.IO;
using System.Text.Json;

namespace WhateverITPCCare.Services;

/// <summary>
/// Which wolf character the user has chosen. Each one has its own artwork and
/// its own set of recorded voice clips, and can be switched at any time from
/// the control panel.
/// </summary>
public enum CompanionGender
{
    Male,
    Female
}

/// <summary>
/// User-adjustable settings for the desktop wolf companion, persisted to a
/// small JSON file in the user's local app-data folder. Everything here is a
/// preference - nothing in it changes the PC.
///
/// Temperature thresholds are deliberately PER-HARDWARE rather than one fixed
/// limit for every machine: a laptop CPU that happily sits at 85 C is very
/// different from a desktop chip, and GPUs run hotter than CPUs by design.
/// <see cref="ThermalProfile"/> seeds sensible defaults from the detected
/// hardware; the user can then fine-tune them in the control panel.
/// </summary>
public sealed class CompanionConfig
{
    // ----- character -----
    // Which wolf (and therefore which recorded voice set) to show. The user can
    // change this later at any time in the control panel.
    public CompanionGender Companion { get; set; } = CompanionGender.Male;

    // ----- behaviour -----
    public bool Enabled { get; set; } = true;
    public bool AlwaysOnTop { get; set; } = true;
    public bool RoamScreen { get; set; } = true;   // walk all over the desktop
    public bool PlayWhenBored { get; set; } = true; // wander / poke icons when idle
    public double Scale { get; set; } = 1.0;        // sprite size multiplier

    // ----- voice -----
    public WolfVoiceStyle Voice { get; set; } = WolfVoiceStyle.Funny;
    public bool SpeakWarnings { get; set; } = true;
    public bool SpeakChit { get; set; } = true;     // jokes / random remarks

    // ----- gaming mode -----
    public bool GamingModeAuto { get; set; } = true; // quieten down while a game runs

    // ----- thermal thresholds (Celsius) -----
    public double CpuWarnC { get; set; } = 80;
    public double CpuCriticalC { get; set; } = 92;
    public double GpuWarnC { get; set; } = 83;
    public double GpuCriticalC { get; set; } = 95;

    // true once we have seeded the thresholds from detected hardware, so we do
    // not overwrite a user's manual edits on the next launch.
    public bool ThresholdsSeeded { get; set; } = false;

    // ----- cooling -----
    public bool AutoCooling { get; set; } = true;   // raise the power plan when hot

    // ======================================================================
    private static string Dir
    {
        get
        {
            string d = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WhateverITPCCare");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    private static string FilePath => Path.Combine(Dir, "companion.json");

    public static CompanionConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var cfg = JsonSerializer.Deserialize<CompanionConfig>(json);
                if (cfg != null) return cfg;
            }
        }
        catch { /* corrupt / unreadable - fall back to defaults */ }
        return new CompanionConfig();
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch { /* best-effort; a failed save must never crash the app */ }
    }
}

/// <summary>
/// Seeds sensible per-hardware temperature thresholds from the CPU / GPU names
/// reported by the sensor reader, so the companion warns at a limit that suits
/// THIS machine instead of a one-size-fits-all number. These are conservative
/// starting points; the user can override every value in the control panel.
/// </summary>
public static class ThermalProfile
{
    public static void SeedDefaults(CompanionConfig cfg, string? cpuName, string? gpuName)
    {
        if (cfg.ThresholdsSeeded) return;

        var cpu = (cpuName ?? "").ToLowerInvariant();
        var gpu = (gpuName ?? "").ToLowerInvariant();

        // CPUs: laptops throttle high by design; AMD Ryzen runs hotter than
        // many Intel desktop parts. Pick a warn/critical pair that fits.
        bool mobile = cpu.Contains("laptop") || cpu.Contains("mobile") ||
                      cpu.EndsWith("h") || cpu.EndsWith("u") || cpu.EndsWith("hs");
        if (cpu.Contains("ryzen") || cpu.Contains("amd"))
        {
            cfg.CpuWarnC = 85; cfg.CpuCriticalC = 95;
        }
        else if (cpu.Contains("intel") || cpu.Contains("core"))
        {
            cfg.CpuWarnC = mobile ? 90 : 83;
            cfg.CpuCriticalC = mobile ? 99 : 95;
        }
        if (mobile) { cfg.CpuWarnC = Math.Max(cfg.CpuWarnC, 88); }

        // GPUs: Nvidia/AMD gaming cards are happy in the 80s and only worry in
        // the 90s; integrated graphics track the CPU.
        if (gpu.Contains("nvidia") || gpu.Contains("geforce") || gpu.Contains("rtx") || gpu.Contains("gtx"))
        {
            cfg.GpuWarnC = 84; cfg.GpuCriticalC = 93;
        }
        else if (gpu.Contains("radeon") || gpu.Contains("amd"))
        {
            cfg.GpuWarnC = 90; cfg.GpuCriticalC = 100;
        }
        else
        {
            // integrated / unknown: stay close to the CPU limits.
            cfg.GpuWarnC = cfg.CpuWarnC; cfg.GpuCriticalC = cfg.CpuCriticalC;
        }

        cfg.ThresholdsSeeded = true;
        cfg.Save();
    }
}
