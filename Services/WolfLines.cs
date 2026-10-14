using System;
using System.Collections.Generic;
using System.Linq;

namespace WhateverITPCCare.Services;

/// <summary>
/// The wolf companion's moods. The desktop sprite tints / animates itself
/// differently for each one and the voice picks a matching line.
/// </summary>
public enum WolfMood
{
    Happy,
    Sleepy,
    Annoyed,
    Worried,
    Proud,
    Angry
}

/// <summary>
/// All of the wolf's spoken / displayed lines, grouped by situation, plus a
/// small helper that returns a RANDOM line but never repeats the same one twice
/// in a row for a given group. This is what gives the companion its cheeky,
/// non-repetitive personality.
/// </summary>
public static class WolfLines
{
    // When the user clicks / pokes the wolf's body.
    public static readonly string[] Clicked =
    {
        "Don't touch me, I'm working!",
        "Stop touching me! I'm monitoring your PC!",
        "What do you want now?",
        "I'm a wolf, not a touchscreen!",
        "Touch me again and I'll log a complaint.",
        "Do I look like I have time for this?"
    };

    // Everything healthy / running cool.
    public static readonly string[] AllGood =
    {
        "All systems looking good. I'm brilliant, I know.",
        "Your PC is behaving itself. For now.",
        "Nothing to fix. I'll just look busy.",
        "Running cool and running smooth. That's how we do it!"
    };

    // Temperatures climbing into the warning / critical band.
    public static readonly string[] TooHot =
    {
        "Whoa! It's getting hot in here!",
        "Boss, we've got a temperature problem!",
        "I'm a wolf, not a firefighter! Fix this heat!",
        "Your CPU is cooking itself. Let's sort this out!",
        "Activating cooling protocol. Stand back!"
    };

    // Idle / random mutterings while nothing much is happening.
    public static readonly string[] Random =
    {
        "You again?",
        "I swear, I work harder than you do.",
        "Still here. Still watching.",
        "These fans won't check themselves, you know.",
        "Ten more degrees and I'm filing for danger pay."
    };

    public static readonly string[] Startup =
    {
        "Morning, boss. Let's see what you've done to this machine."
    };

    public static readonly string[] Shutdown =
    {
        "Finally! I can get some sleep."
    };

    // Said once in a while when the user has been working a long time.
    public static readonly string[] LongSession =
    {
        "You've been at this for ages. Take a break, I'll keep watch.",
        "Still working? Your PC's tired and so am I."
    };

    // ----- non-repeating random picker -----
    private static readonly Random _rng = new();
    private static readonly Dictionary<string[], int> _last = new();

    /// <summary>Returns a random line from <paramref name="group"/> that is not
    /// the same as the one returned last time for that same group.</summary>
    public static string Pick(string[] group)
    {
        if (group == null || group.Length == 0) return string.Empty;
        if (group.Length == 1) return group[0];

        _last.TryGetValue(group, out int prev);
        int idx;
        do { idx = _rng.Next(group.Length); } while (idx == prev);
        _last[group] = idx;
        return group[idx];
    }
}
