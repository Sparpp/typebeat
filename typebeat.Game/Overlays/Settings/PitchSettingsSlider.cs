// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Bindables;
using osu.Framework.Localisation;
using typebeat.Game.Graphics.UserInterface;

namespace typebeat.Game.Overlays.Settings
{
    public partial class PitchSettingsSlider : SettingsSlider<double, PitchSettingsSlider.PitchRoundedSliderBar>
    {
        public PitchSettingsSlider()
        {
            KeyboardStep = 0.1f;
        }

        public override Bindable<double> Current
        {
            get => base.Current;
            set
            {
                // Slider binding initializes its default, which the framework rejects while disabled.
                // Automatic pitch may already be enabled when the mod menu recreates this control.
                bool disabled = value.Disabled;
                value.Disabled = false;
                try
                {
                    base.Current = value;
                }
                finally
                {
                    value.Disabled = disabled;
                }
            }
        }

        public static LocalisableString FormatPitch(double semitones) =>
            FormattableString.Invariant($"{semitones:+0.##;-0.##;0} semitones");

        public partial class PitchRoundedSliderBar : RoundedSliderBar<double>
        {
            protected override LocalisableString GetTooltipText(double value) => FormatPitch(value);
        }
    }
}
