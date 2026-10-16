using System;
using System.IO;
using WhateverITPCCare.Licensing;
using WhateverITPCCare.Services;

namespace WhateverITPCCare.Tests;

/// <summary>
/// Plain-console assertion harness for the licensing engine. No test framework,
/// no NuGet restore – just build-and-run. It verifies both the offline key
/// crypto (LicenseCore) and the stored-plan behaviour (LicenseManager).
///
/// IMPORTANT: LicenseManager reads/writes the SAME %AppData% file the real app
/// uses, so this harness backs that file up before the manager tests and
/// restores it afterwards – running the tests will never clobber a real licence.
/// </summary>
internal static class Program
{
    private static int _pass;
    private static int _fail;

    private static string LicenseFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WhateverITPCCare", "license.dat");

    private static int Main()
    {
        Console.WriteLine("Whatever IT PC Care – licensing tests");
        Console.WriteLine("=====================================\n");

        CoreTests();
        LicenseInfoFlagTests();
        ManagerTests();

        Console.WriteLine();
        Console.WriteLine($"RESULT: {_pass} passed, {_fail} failed.");
        return _fail == 0 ? 0 : 1;
    }

    // ---- 1. Offline key crypto ------------------------------------------
    private static void CoreTests()
    {
        Console.WriteLine("[LicenseCore]");
        var future = DateTime.Now.Date.AddDays(30);

        // BASIC round-trip
        string basic = LicenseCore.Generate("Alice", "BASIC", future);
        bool okB = LicenseCore.Validate(basic, "Alice", out var tB, out var eB);
        Check("BASIC key validates for its owner", okB && tB == "Basic");
        Check("BASIC key carries the right expiry", eB == future);

        // PRO round-trip
        string pro = LicenseCore.Generate("Bob", "PRO", future);
        bool okP = LicenseCore.Validate(pro, "Bob", out var tP, out _);
        Check("PRO key validates and reports Pro tier", okP && tP == "Pro");

        // Perpetual (no expiry)
        string perp = LicenseCore.Generate("Carol", "BASIC", null);
        bool okPerp = LicenseCore.Validate(perp, "Carol", out _, out var ePerp);
        Check("Perpetual key validates with no expiry", okPerp && ePerp == null);

        // Wrong name must fail (key is bound to the licensee name)
        bool wrongName = LicenseCore.Validate(basic, "Mallory", out _, out _);
        Check("Key rejected when the name does not match", !wrongName);

        // Tampered key must fail
        string tampered = Flip(basic);
        bool okTamper = LicenseCore.Validate(tampered, "Alice", out _, out _);
        Check("Tampered key is rejected", !okTamper);

        // Garbage / empty input must fail gracefully
        Check("Empty key is rejected", !LicenseCore.Validate("", "Alice", out _, out _));
        Check("Junk key is rejected", !LicenseCore.Validate("NOT-A-REAL-KEY-123", "Alice", out _, out _));

        Console.WriteLine();
    }

    // ---- 2. Plan flag logic ---------------------------------------------
    private static void LicenseInfoFlagTests()
    {
        Console.WriteLine("[LicenseInfo flags]");
        var basic = new Models.LicenseInfo { Activated = true, Tier = "Basic", Expiry = DateTime.Now.AddDays(10) };
        Check("Basic unlocks Basic repairs", basic.IsBasicOrBetter);
        Check("Basic does NOT unlock Pro", !basic.IsPro);

        var pro = new Models.LicenseInfo { Activated = true, Tier = "Pro", Expiry = DateTime.Now.AddDays(10) };
        Check("Pro unlocks Pro", pro.IsPro);
        Check("Pro also counts as Basic-or-better", pro.IsBasicOrBetter);

        var expired = new Models.LicenseInfo { Activated = true, Tier = "Pro", Expiry = DateTime.Now.AddDays(-1) };
        Check("Expired plan reports IsExpired", expired.IsExpired);
        Check("Expired plan unlocks nothing", !expired.IsPro && !expired.IsBasicOrBetter);

        var free = new Models.LicenseInfo();
        Check("Free plan unlocks nothing", !free.IsBasicOrBetter && !free.IsPro);
        Console.WriteLine();
    }

    // ---- 3. Stored-plan behaviour (touches %AppData%; backed up/restored) 
    private static void ManagerTests()
    {
        Console.WriteLine("[LicenseManager]");
        string? backup = BackupLicense();
        try
        {
            // In-app BASIC purchase mints a 30-day plan locked to this PC.
            var mgr = new LicenseManager();
            var info = mgr.PurchaseActivate("BASIC");
            Check("Purchase activates a Basic plan", info.Activated && info.Tier == "Basic");
            Check("Purchased plan expires in ~30 days",
                info.Expiry.HasValue && Math.Abs((info.Expiry.Value.Date - DateTime.Now.Date).TotalDays - 30) <= 1);

            // It must survive a restart (reload from disk).
            var reloaded = new LicenseManager().Load();
            Check("Purchased plan persists across a restart", reloaded.Activated && reloaded.Tier == "Basic");

            // Upgrading to PRO.
            var proInfo = mgr.PurchaseActivate("PRO");
            Check("Purchase can activate Pro", proInfo.IsPro);

            // Manual key activation (support / offline path).
            string key = LicenseCore.Generate("Support User", "BASIC", DateTime.Now.Date.AddDays(30));
            var manual = new LicenseManager().Activate("Support User", key);
            Check("Manual key activates", manual.Activated && manual.Tier == "Basic");

            // An expired manual key must NOT activate.
            string old = LicenseCore.Generate("Old User", "PRO", DateTime.Now.Date.AddDays(-5));
            var expired = new LicenseManager().Activate("Old User", old);
            Check("Expired key does not activate", !expired.Activated);

            // Machine binding: a key minted for ANOTHER PC must be rejected here.
            string foreignId = "SOME-OTHER-PC|ghost";
            string foreignKey = LicenseCore.Generate(foreignId, "PRO", DateTime.Now.Date.AddDays(30));
            Directory.CreateDirectory(Path.GetDirectoryName(LicenseFile)!);
            File.WriteAllLines(LicenseFile, new[] { "MACHINE", "Ghost", foreignKey });
            var foreign = new LicenseManager().Load();
            Check("A plan copied from another PC is rejected", !foreign.Activated);

            // Deactivate drops back to Free and removes the file.
            var m2 = new LicenseManager();
            m2.PurchaseActivate("PRO");
            m2.Deactivate();
            Check("Deactivate returns to Free", !m2.Current.Activated && m2.Current.Tier == "Free");
            Check("Deactivate removes the stored file", !File.Exists(LicenseFile));
        }
        finally
        {
            RestoreLicense(backup);
        }
        Console.WriteLine();
    }

    // ---- helpers --------------------------------------------------------
    private static void Check(string name, bool ok)
    {
        if (ok) { _pass++; Console.WriteLine("  PASS  " + name); }
        else    { _fail++; Console.WriteLine("  FAIL  " + name); }
    }

    // Flip one payload character so the signature no longer matches.
    private static string Flip(string key)
    {
        char[] c = key.ToCharArray();
        for (int i = 0; i < c.Length; i++)
        {
            if (char.IsLetterOrDigit(c[i]))
            {
                c[i] = c[i] == 'A' ? 'B' : 'A';
                break;
            }
        }
        return new string(c);
    }

    private static string? BackupLicense()
    {
        try { return File.Exists(LicenseFile) ? File.ReadAllText(LicenseFile) : null; }
        catch { return null; }
    }

    private static void RestoreLicense(string? backup)
    {
        try
        {
            if (backup == null)
            {
                if (File.Exists(LicenseFile)) File.Delete(LicenseFile);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LicenseFile)!);
                File.WriteAllText(LicenseFile, backup);
            }
        }
        catch { /* best effort */ }
    }
}
