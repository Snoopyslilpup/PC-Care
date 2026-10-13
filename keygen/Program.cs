using System;
using System.Globalization;
using WhateverITPCCare.Licensing;

// Whatever IT PC Care – licence key generator (SELLER TOOL, keep private).
//
// Usage:
//   WhateverITPCCare.KeyGen "<customer name>" BASIC
//   WhateverITPCCare.KeyGen "<customer name>" PRO 2027-01-31
//
// BASIC = perpetual lifetime key. PRO = subscription; pass an expiry date
// (yyyy-MM-dd). The customer activates by typing the SAME name + the key.

if (args.Length < 2)
{
    Console.WriteLine("Whatever IT PC Care - Key Generator\n");
    Console.WriteLine("  KeyGen \"Customer Name\" BASIC");
    Console.WriteLine("  KeyGen \"Customer Name\" PRO 2027-01-31");
    return 1;
}

string name = args[0];
string tier = args[1].ToUpperInvariant();
DateTime? expiry = null;

if (tier == "PRO")
{
    if (args.Length < 3 ||
        !DateTime.TryParseExact(args[2], "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var d))
    {
        Console.WriteLine("PRO keys need an expiry date, e.g.  KeyGen \"Jane\" PRO 2027-01-31");
        return 1;
    }
    expiry = d;
}
else if (tier != "BASIC")
{
    Console.WriteLine("Tier must be BASIC or PRO.");
    return 1;
}

try
{
    string key = LicenseCore.Generate(name, tier, expiry);
    Console.WriteLine();
    Console.WriteLine($"  Licensed to : {name}");
    Console.WriteLine($"  Plan        : {tier}{(expiry.HasValue ? $"  (valid until {expiry:yyyy-MM-dd})" : "  (lifetime)")}");
    Console.WriteLine($"  Licence key : {key}");
    Console.WriteLine();
    Console.WriteLine("  Send the customer BOTH the exact name and the key above.");

    // Quick self-check that the key validates.
    bool ok = LicenseCore.Validate(key, name, out _, out _);
    Console.WriteLine(ok ? "  (verified OK)" : "  (WARNING: key failed self-check!)");
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine("Error: " + ex.Message);
    return 1;
}
