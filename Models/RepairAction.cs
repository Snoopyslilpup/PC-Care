using System;

namespace WhateverITPCCare.Models;

/// <summary>
/// A single safe repair the user can run. Every repair carries a plain-English
/// description of exactly what it will do, whether it needs Administrator
/// rights, and whether it is a Pro-only feature.
/// </summary>
public class RepairAction
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>True if this repair requires Administrator (UAC) elevation.</summary>
    public bool RequiresAdmin { get; set; }

    /// <summary>True if this repair needs at least a Basic plan to run.</summary>
    public bool RequiresBasic { get; set; }

    /// <summary>True if this repair is only available on a Pro plan.</summary>
    public bool ProOnly { get; set; }

    /// <summary>The work to perform. Returns a short human-readable result line.</summary>
    public Func<Action<string>, string> Run { get; set; } = _ => "Done";
}

public class LicenseInfo
{
    public bool Activated { get; set; }
    public string Tier { get; set; } = "Free";      // Free | Basic | Pro
    public string LicensedTo { get; set; } = "";
    public DateTime? Expiry { get; set; }            // null = perpetual
    public string Message { get; set; } = "";

    public bool IsPro => Activated && Tier == "Pro" && !IsExpired;
    public bool IsBasicOrBetter => Activated && (Tier == "Basic" || Tier == "Pro") && !IsExpired;
    public bool IsExpired => Expiry.HasValue && Expiry.Value.Date < DateTime.UtcNow.Date;
}
