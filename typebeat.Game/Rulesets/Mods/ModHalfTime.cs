// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using osu.Framework.Audio;
using osu.Framework.Bindables;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using typebeat.Game.Configuration;
using typebeat.Game.Graphics;
using typebeat.Game.Overlays.Settings;

namespace typebeat.Game.Rulesets.Mods
{
    public abstract class ModHalfTime : ModRateAdjust
    {
        public override string Name => "Half Time";
        public override string Acronym => "HT";
        public override IconUsage? Icon => OsuIcon.ModHalfTime;
        public override ModType Type => ModType.DifficultyReduction;
        public override LocalisableString Description => "Less zoom...";
        public override bool Ranked => SpeedChange.IsDefault;

        [SettingSource("Speed decrease", "The actual decrease to apply", 0, SettingControlType = typeof(MultiplierSettingsSlider))]
        public override BindableNumber<double> SpeedChange { get; } = new BindableDouble(0.75)
        {
            MinValue = 0.5,
            MaxValue = 0.99,
            Precision = 0.01,
        };

        [SettingSource("Adjust pitch automatically", "Adjust pitch to match the selected playback speed", 2)]
        public BindableBool AutomaticPitchAdjustment { get; } = new BindableBool();

        [SettingSource("Pitch adjustment", "Shift the song's pitch independently of speed. Negative values lower it; positive values raise it.", 1, SettingControlType = typeof(PitchSettingsSlider))]
        public virtual BindableNumber<double> AdjustPitch { get; } = new BindableDouble
        {
            MinValue = -12,
            MaxValue = 12,
        };

        public override IEnumerable<(LocalisableString setting, LocalisableString value)> SettingDescription
        {
            get
            {
                foreach (var description in base.SettingDescription)
                    yield return description;

                if (!AdjustPitch.IsDefault)
                    yield return ("Pitch adjustment", PitchSettingsSlider.FormatPitch(AdjustPitch.Value));

                if (AutomaticPitchAdjustment.Value)
                    yield return ("Adjust pitch automatically", "On");
            }
        }

        private readonly RateAdjustModHelper rateAdjustHelper;

        protected ModHalfTime()
        {
            rateAdjustHelper = new RateAdjustModHelper(SpeedChange);
            rateAdjustHelper.HandleAutomaticPitchAdjustment(AdjustPitch, AutomaticPitchAdjustment);
            rateAdjustHelper.HandleAudioAdjustments(AdjustPitch);
        }

        public override void ApplyToTrack(IAdjustableAudioComponent track)
        {
            rateAdjustHelper.ApplyToTrack(track);
        }

        internal override void CopyAdjustedSetting(IBindable target, object source)
        {
            // Old replays and presets stored a toggle: On pitched the song by its playback rate.
            if (ReferenceEquals(target, AdjustPitch))
            {
                if (source is bool legacyAdjustPitch)
                {
                    AutomaticPitchAdjustment.Value = legacyAdjustPitch;
                    if (!legacyAdjustPitch)
                        AdjustPitch.Value = 0;
                    return;
                }

                // Automatic mode derives pitch from speed, including when restoring or cloning settings.
                if (AutomaticPitchAdjustment.Value)
                    return;
            }

            base.CopyAdjustedSetting(target, source);
        }
    }
}
