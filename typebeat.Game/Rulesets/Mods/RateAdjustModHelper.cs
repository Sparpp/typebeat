// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Audio;
using osu.Framework.Bindables;

namespace typebeat.Game.Rulesets.Mods
{
    /// <summary>
    /// Provides common functionality shared across various rate adjust mods.
    /// </summary>
    public class RateAdjustModHelper : IApplicableToTrack
    {
        public readonly IBindableNumber<double> SpeedChange;

        private IAdjustableAudioComponent? track;

        private BindableBool? adjustPitch;

        private IBindableNumber<double>? pitchShift;

        private readonly BindableDouble frequencyAdjust = new BindableDouble(1);
        private readonly BindableDouble tempoAdjust = new BindableDouble(1);

        /// <summary>
        /// Construct a new <see cref="RateAdjustModHelper"/>.
        /// </summary>
        /// <param name="speedChange">The main speed adjust parameter which is exposed to the user.</param>
        public RateAdjustModHelper(IBindableNumber<double> speedChange)
        {
            SpeedChange = speedChange;
        }

        /// <summary>
        /// Keep the pitch slider at the rate's natural pitch shift and disable manual input while automatic adjustment is enabled.
        /// </summary>
        public void HandleAutomaticPitchAdjustment(BindableNumber<double> pitchShift, BindableBool automaticPitchAdjustment)
        {
            SpeedChange.BindValueChanged(_ =>
            {
                if (automaticPitchAdjustment.Value)
                    updateAutomaticPitch();
            });
            automaticPitchAdjustment.BindValueChanged(_ => updateAutomaticPitch(), true);

            void updateAutomaticPitch()
            {
                pitchShift.Disabled = false;
                if (automaticPitchAdjustment.Value)
                    pitchShift.Value = 12 * Math.Log2(SpeedChange.Value);
                pitchShift.Disabled = automaticPitchAdjustment.Value;
            }
        }

        /// <summary>
        /// Setup audio track adjustments for a rate adjust mod.
        /// Importantly, <see cref="ApplyToTrack"/> must be called when a track is obtained/changed for this to work.
        /// </summary>
        /// <param name="adjustPitch">The "adjust pitch" setting as exposed to the user.</param>
        public void HandleAudioAdjustments(BindableBool adjustPitch)
        {
            this.adjustPitch = adjustPitch;

            // When switching between pitch adjust, we need to update adjustments to time-shift or frequency-scale.
            adjustPitch.BindValueChanged(adjustPitchSetting =>
            {
                track?.RemoveAdjustment(adjustmentForPitchSetting(adjustPitchSetting.OldValue), SpeedChange);
                track?.AddAdjustment(adjustmentForPitchSetting(adjustPitchSetting.NewValue), SpeedChange);

                AdjustableProperty adjustmentForPitchSetting(bool adjustPitchSettingValue)
                    => adjustPitchSettingValue ? AdjustableProperty.Frequency : AdjustableProperty.Tempo;
            });
        }

        /// <summary>
        /// Setup an independent pitch shift, measured in semitones, while preserving the selected playback rate.
        /// </summary>
        public void HandleAudioAdjustments(IBindableNumber<double> pitchShift)
        {
            this.pitchShift = pitchShift;
            SpeedChange.BindValueChanged(_ => updatePitchAdjustments());
            pitchShift.BindValueChanged(_ => updatePitchAdjustments(), true);
        }

        private void updatePitchAdjustments()
        {
            frequencyAdjust.Value = Math.Pow(2, pitchShift!.Value / 12);
            tempoAdjust.Value = SpeedChange.Value / frequencyAdjust.Value;
        }

        /// <summary>
        /// Should be invoked when a track is obtained / changed.
        /// </summary>
        /// <param name="track">The new track.</param>
        /// <exception cref="InvalidOperationException">If neither overload of <c>HandleAudioAdjustments</c> has been called.</exception>
        public void ApplyToTrack(IAdjustableAudioComponent track)
        {
            if (pitchShift != null)
            {
                this.track?.RemoveAdjustment(AdjustableProperty.Frequency, frequencyAdjust);
                this.track?.RemoveAdjustment(AdjustableProperty.Tempo, tempoAdjust);
                this.track = track;
                track.AddAdjustment(AdjustableProperty.Frequency, frequencyAdjust);
                track.AddAdjustment(AdjustableProperty.Tempo, tempoAdjust);
                return;
            }

            if (adjustPitch == null)
                throw new InvalidOperationException($"Must call {nameof(HandleAudioAdjustments)} first");

            this.track = track;
            adjustPitch.TriggerChange();
        }
    }
}
