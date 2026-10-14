using System;
using System.Linq;
using System.Speech.Synthesis;

namespace WhateverITPCCare.Services;

/// <summary>
/// How the wolf should sound.
/// </summary>
public enum WolfVoiceStyle
{
    /// <summary>No speech - the wolf only shows text speech-bubbles.</summary>
    Silent,
    /// <summary>Light, higher-pitched, faster: the cheeky \"funny\" wolf.</summary>
    Funny,
    /// <summary>Measured, lower, calmer: the \"serious technician\" wolf.</summary>
    Technician
}

/// <summary>
/// Voice narration for the companion, built on the Windows built-in speech
/// engine (System.Speech / SAPI) so it works offline with no extra download.
/// Speaking is always asynchronous so it never freezes the UI, and if the PC
/// has no speech voice installed we simply fall back to text only.
/// </summary>
public sealed class WolfVoice : IDisposable
{
    private SpeechSynthesizer? _synth;
    private bool _available;

    public WolfVoiceStyle Style { get; set; } = WolfVoiceStyle.Funny;

    /// <summary>
    /// When true the TTS fallback prefers a female installed voice, to match a
    /// female companion character; otherwise it prefers a male voice.
    /// </summary>
    public bool CompanionFemale { get; set; } = false;

    public WolfVoice()
    {
        try
        {
            _synth = new SpeechSynthesizer();
            // Only count the voice engine as usable if at least one installed
            // voice is enabled; otherwise Speak would throw.
            _available = _synth.GetInstalledVoices().Any(v => v.Enabled);
        }
        catch
        {
            _synth = null;
            _available = false;
        }
    }

    public bool Available => _available;

    /// <summary>
    /// Static check used by the control panel to tell the user, honestly,
    /// whether this PC has any usable Windows speech voice installed.
    /// </summary>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                using var s = new SpeechSynthesizer();
                return s.GetInstalledVoices().Any(v => v.Enabled);
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Speak a line out loud (non-blocking). Does nothing when the style is
    /// Silent or no speech engine is available - the caller still shows the
    /// text bubble, so the wolf is never mute visually.
    /// </summary>
    public void Say(string text)
    {
        if (!_available || _synth == null) return;
        if (Style == WolfVoiceStyle.Silent) return;
        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            _synth.SpeakAsyncCancelAll();
            ApplyStyle();
            _synth.SpeakAsync(text);
        }
        catch
        {
            // A voice can disappear (e.g. language pack removed) between calls -
            // never let narration crash the companion.
        }
    }

    public void StopSpeaking()
    {
        try { _synth?.SpeakAsyncCancelAll(); } catch { }
    }

    private void ApplyStyle()
    {
        if (_synth == null) return;
        switch (Style)
        {
            case WolfVoiceStyle.Funny:
                // Faster and a touch higher = livelier, cheekier delivery.
                _synth.Rate = 2;
                _synth.Volume = 100;
                PreferVoice(hintFemale: CompanionFemale);
                break;
            case WolfVoiceStyle.Technician:
                // Slower and steady = calm, serious technician.
                _synth.Rate = -1;
                _synth.Volume = 100;
                PreferVoice(hintFemale: CompanionFemale);
                break;
        }
    }

    /// <summary>
    /// Picks an installed voice that loosely matches the requested character.
    /// Falls back silently to whatever is available - never throws.
    /// </summary>
    private void PreferVoice(bool hintFemale)
    {
        try
        {
            var voices = _synth!.GetInstalledVoices().Where(v => v.Enabled).ToList();
            if (voices.Count == 0) return;

            var match = voices.FirstOrDefault(v =>
                v.VoiceInfo.Gender == (hintFemale ? VoiceGender.Female : VoiceGender.Male));
            var chosen = match ?? voices[0];
            _synth.SelectVoice(chosen.VoiceInfo.Name);
        }
        catch { }
    }

    public void Dispose()
    {
        try { _synth?.Dispose(); } catch { }
        _synth = null;
    }
}
