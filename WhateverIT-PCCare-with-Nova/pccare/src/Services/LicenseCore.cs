using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WhateverITPCCare.Licensing;

/// <summary>
/// Offline licence-key engine shared by the app (to validate) and the keygen
/// tool (to issue). A key encodes: tier, licensee name, optional expiry, and an
/// HMAC-SHA256 signature over those fields. Validation is fully offline – no
/// server required – which suits a product sold and activated on a home PC.
///
/// SECURITY: Change SECRET below to your own private phrase BEFORE you build
/// and sell. Anyone with this phrase can mint keys, so never ship the keygen
/// to customers and never commit the real secret to a public repo.
/// </summary>
public static class LicenseCore
{
    // >>> CHANGE THIS before you build for sale <<<
    private const string SECRET = "WhateverIT-1Zt8HGWpkxseqnKJyTHVTbPpgFPUZy0e-9cG5aeB7BTMpclYohXn8At4hXOSoe9R";

    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // Crockford-ish base32

    /// <summary>Issue a key. tier = "BASIC" or "PRO". expiry null = perpetual.</summary>
    public static string Generate(string name, string tier, DateTime? expiry)
    {
        name = (name ?? "").Trim();
        tier = (tier ?? "BASIC").Trim().ToUpperInvariant();
        if (tier != "BASIC" && tier != "PRO")
            throw new ArgumentException("tier must be BASIC or PRO");

        string exp = expiry.HasValue ? expiry.Value.ToString("yyyyMMdd") : "00000000";
        string payload = $"{tier}|{exp}|{name}";
        string sig = Sign(payload);
        // key body = base32(tier marker + expiry) ; signature appended
        string tierMark = tier == "PRO" ? "P" : "B";
        string body = tierMark + exp;                       // 9 chars
        string encoded = Base32(Encoding.ASCII.GetBytes(body));
        return Group($"{encoded}{sig}");
    }

    /// <summary>Validate a key against the licensee name the customer typed.</summary>
    public static bool Validate(string key, string name, out string tier, out DateTime? expiry)
    {
        tier = "Free";
        expiry = null;
        try
        {
            string clean = (key ?? "").Replace("-", "").Replace(" ", "").Trim().ToUpperInvariant();
            name = (name ?? "").Trim();
            if (clean.Length < 20) return false;

            // Encoded body is first 15 base32 chars (9 bytes -> 15 chars), rest = signature
            string encoded = clean.Substring(0, 15);
            string sigPart = clean.Substring(15);
            string body = Encoding.ASCII.GetString(UnBase32(encoded));
            if (body.Length < 9) return false;

            char tierMark = body[0];
            string exp = body.Substring(1, 8);
            string tierName = tierMark == 'P' ? "PRO" : tierMark == 'B' ? "BASIC" : "";
            if (tierName == "") return false;

            string payload = $"{tierName}|{exp}|{name}";
            string expectedSig = Sign(payload);
            if (!FixedEquals(expectedSig, sigPart)) return false;

            tier = tierName == "PRO" ? "Pro" : "Basic";
            if (exp != "00000000" &&
                DateTime.TryParseExact(exp, "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var d))
                expiry = d;
            return true;
        }
        catch { return false; }
    }

    // ---- internals ----
    private static string Sign(string payload)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(SECRET));
        byte[] mac = h.ComputeHash(Encoding.UTF8.GetBytes(payload));
        // take 10 bytes -> 16 base32 chars for a compact but strong signature
        var slice = new byte[10];
        Array.Copy(mac, slice, 10);
        return Base32(slice);
    }

    private static bool FixedEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    private static string Base32(byte[] data)
    {
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0)
            sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    private static byte[] UnBase32(string s)
    {
        int buffer = 0, bits = 0;
        var outBytes = new System.Collections.Generic.List<byte>();
        foreach (char c in s)
        {
            int val = Alphabet.IndexOf(c);
            if (val < 0) continue;
            buffer = (buffer << 5) | val;
            bits += 5;
            if (bits >= 8)
            {
                outBytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return outBytes.ToArray();
    }

    private static string Group(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (i > 0 && i % 5 == 0) sb.Append('-');
            sb.Append(s[i]);
        }
        return sb.ToString();
    }
}
