using System;
using System.Globalization;
using System.Windows;
using WhateverITPCCare.Services;

namespace WhateverITPCCare
{
    /// <summary>
    /// Control panel for the desktop wolf companion. Edits <see cref="CompanionConfig"/>
    /// and pushes the changes back into the live <see cref="CompanionWindow"/>.
    /// </summary>
    public partial class CompanionPanel : Window
    {
        private readonly CompanionConfig _cfg;
        private readonly CompanionWindow? _wolf;

        public CompanionPanel(CompanionConfig cfg, CompanionWindow? wolf)
        {
            InitializeComponent();
            _cfg = cfg;
            _wolf = wolf;
            LoadIntoControls();
            DescribeCapabilities();
        }

        private void LoadIntoControls()
        {
            CharacterCombo.SelectedIndex = _cfg.Companion == CompanionGender.Female ? 1 : 0;

            EnabledCheck.IsChecked = _cfg.Enabled;
            TopCheck.IsChecked     = _cfg.AlwaysOnTop;
            RoamCheck.IsChecked    = _cfg.RoamScreen;
            PlayCheck.IsChecked    = _cfg.PlayWhenBored;
            ScaleSlider.Value      = _cfg.Scale;

            VoiceCombo.SelectedIndex = _cfg.Voice switch
            {
                WolfVoiceStyle.Silent     => 0,
                WolfVoiceStyle.Funny      => 1,
                WolfVoiceStyle.Technician => 2,
                _ => 1
            };

            WarnCheck.IsChecked    = _cfg.SpeakWarnings;
            ChitCheck.IsChecked    = _cfg.SpeakChit;
            CoolingCheck.IsChecked = _cfg.AutoCooling;
            GamingCheck.IsChecked  = _cfg.GamingModeAuto;

            CpuWarnBox.Text = _cfg.CpuWarnC.ToString(CultureInfo.InvariantCulture);
            CpuCritBox.Text = _cfg.CpuCriticalC.ToString(CultureInfo.InvariantCulture);
            GpuWarnBox.Text = _cfg.GpuWarnC.ToString(CultureInfo.InvariantCulture);
            GpuCritBox.Text = _cfg.GpuCriticalC.ToString(CultureInfo.InvariantCulture);
        }

        private void DescribeCapabilities()
        {
            // Be honest about what the voice engine and the fans can actually do.
            VoiceAvailNote.Text = WolfVoice.IsAvailable
                ? "Voice uses the Windows speech engine installed on this PC."
                : "No Windows speech voice was found, so the wolf will only show text bubbles.";

            var caps = FanControl.Describe();
            FanStatusText.Text = caps.CanControl
                ? "Fan control: " + caps.Detail
                : "Honest heads-up: this app cannot directly change your fan speeds on this " +
                  "machine (" + caps.Detail + "). When it gets hot the wolf will warn you and, " +
                  "if you allow it, ask Windows to use a cooler power plan \u2014 but it will never " +
                  "pretend to spin a fan it cannot reach.";
        }

        private double ParseOr(string s, double fallback)
            => double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        private void OnCheckNow(object sender, RoutedEventArgs e) => _wolf?.SpeakHealthSummary();

        private void OnCancel(object sender, RoutedEventArgs e) => Close();

        private void OnSave(object sender, RoutedEventArgs e)
        {
            _cfg.Companion = CharacterCombo.SelectedIndex == 1
                ? CompanionGender.Female
                : CompanionGender.Male;

            _cfg.Enabled       = EnabledCheck.IsChecked == true;
            _cfg.AlwaysOnTop   = TopCheck.IsChecked == true;
            _cfg.RoamScreen    = RoamCheck.IsChecked == true;
            _cfg.PlayWhenBored = PlayCheck.IsChecked == true;
            _cfg.Scale         = Math.Round(ScaleSlider.Value, 2);

            _cfg.Voice = VoiceCombo.SelectedIndex switch
            {
                0 => WolfVoiceStyle.Silent,
                2 => WolfVoiceStyle.Technician,
                _ => WolfVoiceStyle.Funny
            };

            _cfg.SpeakWarnings = WarnCheck.IsChecked == true;
            _cfg.SpeakChit     = ChitCheck.IsChecked == true;
            _cfg.AutoCooling   = CoolingCheck.IsChecked == true;
            _cfg.GamingModeAuto = GamingCheck.IsChecked == true;

            // Keep warn below critical, clamp to sane ranges.
            _cfg.CpuWarnC     = Clamp(ParseOr(CpuWarnBox.Text, _cfg.CpuWarnC), 40, 110);
            _cfg.CpuCriticalC = Clamp(ParseOr(CpuCritBox.Text, _cfg.CpuCriticalC), _cfg.CpuWarnC + 1, 115);
            _cfg.GpuWarnC     = Clamp(ParseOr(GpuWarnBox.Text, _cfg.GpuWarnC), 40, 110);
            _cfg.GpuCriticalC = Clamp(ParseOr(GpuCritBox.Text, _cfg.GpuCriticalC), _cfg.GpuWarnC + 1, 115);

            _cfg.Save();
            _wolf?.ReloadConfig();
            Close();
        }

        private static double Clamp(double v, double lo, double hi)
            => v < lo ? lo : (v > hi ? hi : v);
    }
}
