using System;
using System.IO;
using System.Text;
using WhateverITPCCare.Licensing;
using WhateverITPCCare.Models;

namespace WhateverITPCCare.Services;

/// <summary>
/// Loads, validates and persists the current plan. State lives in
/// %AppData%\WhateverITPCCare\license.dat and is re-validated on every launch,
/// so a tampered or copied file simply drops the app back to the free tier.
///
/// Two activation styles are supported:
///  - MACHINE: an in-app purchase. A 30-day key is minted and bound to THIS PC
///    (machine + user). Copying license.dat to another PC fails validation,
///    because the key is re-checked against the live machine identity.
///  - NAME:    a manually typed “Licensed to” name + key (support / offline).
/// </summary>
public class LicenseManager
{
    private readonly string _dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WhateverITPCCare");
    private string File_ => Path.Combine(_dir, "license.dat");
    private string TrialFile => Path.Combine(_dir, "trial.dat");

    /// <summary>True once the single free trial scan has been used on this PC.</summary>
    public bool TrialUsed
    {
        get { try { return File.Exists(TrialFile); } catch { return false; } }
    }

    /// <summary>Record that the one free trial scan has now been used, so the
    /// app locks scanning until the customer subscribes. Persisted in AppData so
    /// it survives closing/reopening the app.</summary>
    public void MarkTrialUsed()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            if (!File.Exists(TrialFile))
                File.WriteAllText(TrialFile, DateTime.Now.ToString("O"));
        }
        catch { /* best-effort */ }
    }

    public LicenseInfo Current { get; private set; } = new();

    public LicenseInfo Load()
    {
        try
        {
            if (!File.Exists(File_)) return Current = Free("No subscription – running in free mode.");
            string[] lines = File.ReadAllLines(File_, Encoding.UTF8);

            // Machine-bound in-app purchase: MACHINE / display-name / key
            if (lines.Length >= 3 && lines[0] == "MACHINE")
            {
                var info = Build(lines[2], MachineIdentity());
                info.LicensedTo = lines[1];
                return Current = info;
            }

            // Manual: optional NAME header, then name + key
            int off = (lines.Length >= 1 && lines[0] == "NAME") ? 1 : 0;
            if (lines.Length >= off + 2)
                return Current = Build(lines[off + 1], lines[off]);

            return Current = Free("No subscription – running in free mode.");
        }
        catch { return Current = Free("No subscription – running in free mode."); }
    }

    /// <summary>In-app purchase succeeded: mint a 30-day plan locked to this PC.</summary>
    public LicenseInfo PurchaseActivate(string tier)
    {
        tier = (tier ?? "").Trim().ToUpperInvariant();
        if (tier != "BASIC" && tier != "PRO") tier = "BASIC";

        string id = MachineIdentity();
        DateTime expiry = DateTime.Now.Date.AddDays(30);
        string key = LicenseCore.Generate(id, tier, expiry);

        var info = Build(key, id);
        if (info.Activated)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                File.WriteAllLines(File_, new[] { "MACHINE", FriendlyName(), key }, Encoding.UTF8);
            }
            catch { /* persistence best-effort */ }
        }
        info.LicensedTo = FriendlyName();
        Current = info;
        return info;
    }

    /// <summary>Manual activation from a typed name + key (support / offline).</summary>
    public LicenseInfo Activate(string name, string key)
    {
        var info = Build(key, name);
        if (info.Activated)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                File.WriteAllLines(File_, new[] { "NAME", name.Trim(), key.Trim() }, Encoding.UTF8);
            }
            catch { /* persistence best-effort */ }
        }
        Current = info;
        return info;
    }

    public void Deactivate()
    {
        try { if (File.Exists(File_)) File.Delete(File_); } catch { }
        Current = Free("Plan removed – running in free mode.");
    }

    private static LicenseInfo Build(string key, string name)
    {
        if (!LicenseCore.Validate(key, name, out string tier, out DateTime? expiry))
            return Free("No active subscription.");

        var info = new LicenseInfo
        {
            Activated = true,
            Tier = tier,
            LicensedTo = name.Trim(),
            Expiry = expiry,
        };
        if (info.IsExpired)
        {
            info.Activated = false;
            info.Tier = "Free";
            info.Message = $"Your {tier} plan expired on {expiry:d MMM yyyy}. Renew to restore it.";
            return info;
        }
        info.Message = expiry.HasValue
            ? $"{tier} plan active – valid until {expiry:d MMM yyyy}."
            : $"{tier} plan active.";
        return info;
    }

    private static LicenseInfo Free(string msg) => new() { Activated = false, Tier = "Free", Message = msg };

    // A stable-enough per-PC identity for soft node-locking of in-app purchases.
    private static string MachineIdentity() => Environment.MachineName + "|" + Environment.UserName;
    private static string FriendlyName() => Environment.UserName + " · " + Environment.MachineName;
}
