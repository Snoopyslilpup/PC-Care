using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;

namespace WhateverITPCCare.Services;

/// <summary>
/// Which recorded line to play. These map one-to-one onto the folders of MP3
/// voice clips shipped with the app (one set per companion character).
/// </summary>
public enum WolfClip
{
    Click,      // user poked the wolf
    Good,       // everything healthy / cooled down
    Hot,        // overheating warning / critical
    Random,     // idle chit-chat
    Startup,    // first greeting
    Shutdown    // farewell
}

/// <summary>
/// Plays the companion's REAL recorded voice clips (professionally voiced MP3s
/// that ship with the app), picking the correct set for the chosen character
/// (male / female) and never repeating the same clip twice in a row.
///
/// This is separate from <see cref="WolfVoice"/> (the Windows text-to-speech
/// fallback): when a matching recording exists we play that, because it sounds
/// far better than synthesised speech. If the clips are missing for any reason
/// the caller falls back to TTS, so the wolf is never left mute.
/// </summary>
public sealed class WolfAudio : IDisposable
{
    private readonly MediaPlayer _player = new();
    private readonly Random _rng = new();
    private readonly Dictionary<WolfClip, List<string>> _clips = new();
    private readonly Dictionary<WolfClip, int> _last = new();
    private CompanionGender _gender = CompanionGender.Male;
    private double _volume = 1.0;

    public WolfAudio()
    {
        try { _player.Volume = _volume; } catch { }
        LoadClips();
    }

    /// <summary>0.0 - 1.0 playback volume.</summary>
    public double Volume
    {
        get => _volume;
        set { _volume = Math.Max(0, Math.Min(1, value)); try { _player.Volume = _volume; } catch { } }
    }

    /// <summary>Switch the active character; reloads that character's clip set.</summary>
    public void SetGender(CompanionGender gender)
    {
        if (gender == _gender && _clips.Count > 0) return;
        _gender = gender;
        LoadClips();
    }

    /// <summary>True when at least one recorded clip is available to play.</summary>
    public bool HasClips => _clips.Values.Any(l => l.Count > 0);

    /// <summary>
    /// Where the voice clips live: &lt;app folder&gt;\Assets\voices\&lt;male|female&gt;.
    /// They are shipped as plain files next to the executable (copied on build)
    /// so the media player can open them directly.
    /// </summary>
    private string VoiceDir
    {
        get
        {
            string sub = _gender == CompanionGender.Female ? "female" : "male";
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "voices", sub);
        }
    }

    private static readonly Dictionary<WolfClip, string> Tags = new()
    {
        [WolfClip.Click]    = "click",
        [WolfClip.Good]     = "good",
        [WolfClip.Hot]      = "hot",
        [WolfClip.Random]   = "random",
        [WolfClip.Startup]  = "startup",
        [WolfClip.Shutdown] = "shutdown",
    };

    private void LoadClips()
    {
        _clips.Clear();
        _last.Clear();
        try
        {
            var dir = VoiceDir;
            if (!Directory.Exists(dir)) return;
            var files = Directory.GetFiles(dir, "*.mp3");
            foreach (var kv in Tags)
            {
                // match e.g. wolf_alive_click_01.mp3 / wolf_female_startup.mp3
                var matches = files
                    .Where(f => Path.GetFileNameWithoutExtension(f)
                        .ToLowerInvariant()
                        .Contains("_" + kv.Value))
                    .OrderBy(f => f)
                    .ToList();
                _clips[kv.Key] = matches;
            }
        }
        catch { /* missing clips just mean we fall back to TTS */ }
    }

    /// <summary>
    /// Play a random clip for the given situation. Returns true if a clip was
    /// actually started, false if none exist (so the caller can fall back to
    /// text-to-speech). Never throws.
    /// </summary>
    public bool TryPlay(WolfClip clip)
    {
        try
        {
            if (!_clips.TryGetValue(clip, out var list) || list.Count == 0) return false;

            int idx;
            if (list.Count == 1) idx = 0;
            else
            {
                _last.TryGetValue(clip, out int prev);
                do { idx = _rng.Next(list.Count); } while (idx == prev);
            }
            _last[clip] = idx;

            _player.Stop();
            _player.Open(new Uri(list[idx], UriKind.Absolute));
            _player.Play();
            return true;
        }
        catch { return false; }
    }

    public void Stop()
    {
        try { _player.Stop(); } catch { }
    }

    public void Dispose()
    {
        try { _player.Stop(); _player.Close(); } catch { }
    }
}
