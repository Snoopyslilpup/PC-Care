using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WhateverITPCCare.Services;

/// <summary>One installed game we can list and launch.</summary>
public sealed class RigGame
{
    public string Name { get; set; } = "";
    public string AppId { get; set; } = "";
    public string Launch => $"steam://rungameid/{AppId}";
}

/// <summary>
/// Finds installed Steam games so "The Rig" can list and launch them like a
/// game hub. Read-only: it only reads Steam's own library files. If Steam is
/// not installed it simply returns an empty list.
/// </summary>
public sealed class GameLibrary
{
    public IReadOnlyList<RigGame> Scan()
    {
        var games = new List<RigGame>();
        try
        {
            string? steam = FindSteamPath();
            if (string.IsNullOrEmpty(steam)) return games;

            foreach (string lib in LibraryFolders(steam))
            {
                string apps = Path.Combine(lib, "steamapps");
                if (!Directory.Exists(apps)) continue;

                foreach (string acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
                {
                    try
                    {
                        string text = File.ReadAllText(acf);
                        string id = Match(text, "\"appid\"\\s*\"(\\d+)\"");
                        string name = Match(text, "\"name\"\\s*\"([^\"]+)\"");
                        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name) &&
                            !games.Any(g => g.AppId == id))
                            games.Add(new RigGame { AppId = id, Name = name });
                    }
                    catch { }
                }
            }
        }
        catch { }

        return games.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? FindSteamPath()
    {
        // Registry is the most reliable source.
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            string? p = k?.GetValue("SteamPath") as string;
            if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) return p;
        }
        catch { }
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            string? p = k?.GetValue("InstallPath") as string;
            if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) return p;
        }
        catch { }

        // Fall back to the usual install locations.
        foreach (string guess in new[]
        {
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam",
        })
            if (Directory.Exists(guess)) return guess;

        return null;
    }

    private static IEnumerable<string> LibraryFolders(string steam)
    {
        var paths = new List<string> { steam };
        try
        {
            string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                string text = File.ReadAllText(vdf);
                foreach (Match m in Regex.Matches(text, "\"path\"\\s*\"([^\"]+)\""))
                {
                    // The VDF escapes back-slashes, so un-escape them.
                    string p = m.Groups[1].Value.Replace("\\\\", "\\");
                    if (Directory.Exists(p) && !paths.Contains(p)) paths.Add(p);
                }
            }
        }
        catch { }
        return paths;
    }

    private static string Match(string text, string pattern)
    {
        try
        {
            var m = Regex.Match(text, pattern);
            return m.Success ? m.Groups[1].Value : "";
        }
        catch { return ""; }
    }
}
