// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using osu.Framework.Audio;
using osu.Framework.Bindables;
using typebeat.Game.Beatmaps;
using typebeat.Game.Configuration;
using typebeat.Game.Online.API;
using typebeat.Game.Overlays.Settings;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class TypeBeatPitchAdjustmentTest
    {
        [TestCase("DT", 1.5)]
        [TestCase("HT", 0.75)]
        public void CustomisationUsesAnIndependentPitchSlider(string acronym, double rate)
        {
            var mod = createMod(acronym);
            var controls = mod.CreateSettingsControls().ToArray();

            try
            {
                var automatic = controls.OfType<SettingsCheckbox>().Single();
                Assert.That(automatic.LabelText.ToString(), Is.EqualTo("Adjust pitch automatically"));
                Assert.That(automatic.Current.Value, Is.False);
                var slider = controls.OfType<PitchSettingsSlider>().Single();
                Assert.That(slider.LabelText.ToString(), Is.EqualTo("Pitch adjustment"));
                Assert.That(slider.KeyboardStep, Is.EqualTo(0.1f));
                Assert.That(pitchFor(mod).MinValue, Is.EqualTo(-12));
                Assert.That(pitchFor(mod).MaxValue, Is.EqualTo(12));
                Assert.That(slider.Current.Value, Is.Zero);

                slider.Current.Value = -3.5;
                Assert.That(pitchFor(mod).Value, Is.EqualTo(-3.5));
                Assert.That(mod.SpeedChange.Value, Is.EqualTo(rate));
                Assert.That(mod.SettingDescription.Single(d => d.setting.ToString() == "Pitch adjustment").value.ToString(),
                    Is.EqualTo("-3.5 semitones"));

                slider.ApplyDefault();
                Assert.That(pitchFor(mod).Value, Is.Zero);

                automatic.Current.Value = true;
                Assert.That(automaticPitchFor(mod).Value, Is.True);
                Assert.That(slider.Current.Value, Is.EqualTo(12 * Math.Log2(rate)).Within(1e-12));
                Assert.That(slider.Current.Disabled, Is.True);
                Assert.Throws<InvalidOperationException>(() => slider.Current.Value = 2);

                mod.SpeedChange.Value = acronym == "DT" ? 1.8 : 0.6;
                double automaticPitch = 12 * Math.Log2(mod.SpeedChange.Value);
                Assert.That(slider.Current.Value, Is.EqualTo(automaticPitch).Within(1e-12));
                Assert.That(slider.Current.Disabled, Is.True);

                automatic.Current.Value = false;
                Assert.That(slider.Current.Disabled, Is.False);
                Assert.That(slider.Current.Value, Is.EqualTo(automaticPitch).Within(1e-12));
                slider.Current.Value = -2;
                Assert.That(pitchFor(mod).Value, Is.EqualTo(-2));
            }
            finally
            {
                foreach (var control in controls)
                    control.Dispose();
            }
        }

        [TestCase("DT", 1.5)]
        [TestCase("DT", 1.73)]
        [TestCase("HT", 0.75)]
        [TestCase("HT", 0.62)]
        public void AutomaticPitchControlsCanBeRecreatedWhileTheSliderIsLocked(string acronym, double rate)
        {
            var mod = createMod(acronym);
            mod.SpeedChange.Value = rate;
            automaticPitchFor(mod).Value = true;
            double pitch = pitchFor(mod).Value;

            for (int i = 0; i < 3; i++)
            {
                var controls = mod.CreateSettingsControls().ToArray();
                try
                {
                    var slider = controls.OfType<PitchSettingsSlider>().Single();
                    Assert.That(slider.Current.Disabled, Is.True);
                    Assert.That(slider.Current.Value, Is.EqualTo(pitch));
                    Assert.That(slider.Current.Default, Is.Zero);
                    Assert.Throws<InvalidOperationException>(() => slider.Current.Value = 2);

                    // Selecting another mod rebuilds this tracker while automatic pitch remains enabled.
                    using (var tracker = new ModSettingChangeTracker(new Mod[] { mod, new TypeBeatModLiterate() }))
                        Assert.That(automaticPitchFor(mod).Value, Is.True);
                }
                finally
                {
                    foreach (var control in controls)
                        control.Dispose();
                }

                Assert.That(mod.SpeedChange.Value, Is.EqualTo(rate));
                Assert.That(pitchFor(mod).Value, Is.EqualTo(pitch));
                Assert.That(pitchFor(mod).Disabled, Is.True);
            }
        }

        [TestCase("DT", 1.01)]
        [TestCase("DT", 1.5)]
        [TestCase("DT", 2.0)]
        [TestCase("HT", 0.5)]
        [TestCase("HT", 0.75)]
        [TestCase("HT", 0.99)]
        public void AutomaticPitchTracksSpeedAndMatchesTheOriginalFrequencyAdjustment(string acronym, double rate)
        {
            var mod = createMod(acronym);
            automaticPitchFor(mod).Value = true;
            var adjustments = new AudioAdjustments();
            mod.ApplyToTrack(adjustments);

            mod.SpeedChange.Value = rate;
            Assert.That(pitchFor(mod).Value, Is.EqualTo(12 * Math.Log2(rate)).Within(1e-12));
            Assert.That(pitchFor(mod).Disabled, Is.True);
            Assert.That(adjustments.AggregateFrequency.Value, Is.EqualTo(rate).Within(1e-12));
            Assert.That(adjustments.AggregateTempo.Value, Is.EqualTo(1).Within(1e-12));
            Assert.That(mod.ApplyToRate(0, 1), Is.EqualTo(rate));
            Assert.That(mod.Ranked, Is.True);

            automaticPitchFor(mod).Value = false;
            double manualPitch = pitchFor(mod).Value;
            mod.SpeedChange.Value = acronym == "DT" ? 1.7 : 0.7;
            Assert.That(pitchFor(mod).Value, Is.EqualTo(manualPitch));
            Assert.That(pitchFor(mod).Disabled, Is.False);
            Assert.That(adjustments.AggregateFrequency.Value, Is.EqualTo(rate).Within(1e-12));
            Assert.That(adjustments.AggregateTempo.Value * adjustments.AggregateFrequency.Value,
                Is.EqualTo(mod.SpeedChange.Value).Within(1e-12));
        }

        [TestCase("DT", 1.01)]
        [TestCase("DT", 1.5)]
        [TestCase("DT", 2.0)]
        [TestCase("HT", 0.5)]
        [TestCase("HT", 0.75)]
        [TestCase("HT", 0.99)]
        public void PitchChangesDoNotChangePlaybackRateOrScoreMultiplier(string acronym, double rate)
        {
            var mod = createMod(acronym);
            var adjustments = new AudioAdjustments();
            var calculator = new TypeBeatScoreMultiplierCalculator(new ScoreMultiplierContext(new BeatmapDifficulty()));
            mod.SpeedChange.Value = rate;
            mod.ApplyToTrack(adjustments);
            double multiplier = calculator.CalculateFor(new Mod[] { mod });

            foreach (double pitch in new[] { -12, -6, -0.5, 0, 0.5, 6, 12, 0 })
            {
                pitchFor(mod).Value = pitch;
                Assert.That(adjustments.AggregateFrequency.Value, Is.EqualTo(Math.Pow(2, pitch / 12)).Within(1e-12));
                Assert.That(adjustments.AggregateTempo.Value * adjustments.AggregateFrequency.Value, Is.EqualTo(rate).Within(1e-12));
                Assert.That(mod.ApplyToRate(0, 1), Is.EqualTo(rate));
                Assert.That(mod.Ranked, Is.True);
                Assert.That(calculator.CalculateFor(new Mod[] { mod }), Is.EqualTo(multiplier));
            }

            pitchFor(mod).Value = 3;
            mod.SpeedChange.Value = acronym == "DT" ? 1.8 : 0.6;
            Assert.That(adjustments.AggregateTempo.Value * adjustments.AggregateFrequency.Value,
                Is.EqualTo(mod.SpeedChange.Value).Within(1e-12));
            Assert.That(adjustments.AggregateFrequency.Value, Is.EqualTo(Math.Pow(2, 0.25)).Within(1e-12));
        }

        [TestCase("DT")]
        [TestCase("HT")]
        public void TrackReplacementAndRepeatedApplicationDoNotStackPitch(string acronym)
        {
            var mod = createMod(acronym);
            var previous = new AudioAdjustments();
            var current = new AudioAdjustments();
            pitchFor(mod).Value = 6;
            mod.ApplyToTrack(previous);
            mod.ApplyToTrack(previous);
            Assert.That(previous.AggregateTempo.Value * previous.AggregateFrequency.Value,
                Is.EqualTo(mod.SpeedChange.Value).Within(1e-12));

            mod.ApplyToTrack(current);
            pitchFor(mod).Value = -12;
            Assert.That(previous.AggregateFrequency.Value, Is.EqualTo(1));
            Assert.That(previous.AggregateTempo.Value, Is.EqualTo(1));
            Assert.That(current.AggregateFrequency.Value, Is.EqualTo(0.5));
            Assert.That(current.AggregateTempo.Value * current.AggregateFrequency.Value,
                Is.EqualTo(mod.SpeedChange.Value).Within(1e-12));
        }

        [TestCase("DT", 5.5)]
        [TestCase("HT", -4.25)]
        public void PitchSurvivesStorageCloningAndReset(string acronym, double pitch)
        {
            var mod = createMod(acronym);
            pitchFor(mod).Value = pitch;
            var apiMod = new APIMod(mod);
            Assert.That(apiMod.Settings["adjust_pitch"], Is.EqualTo(pitch));

            var restored = (ModRateAdjust)JsonConvert.DeserializeObject<APIMod>(JsonConvert.SerializeObject(apiMod))!.ToMod(new TypeBeatRuleset());
            Assert.That(pitchFor(restored).Value, Is.EqualTo(pitch));
            Assert.That(restored, Is.EqualTo(mod));

            var clone = (ModRateAdjust)restored.DeepClone();
            Assert.That(pitchFor(clone).Value, Is.EqualTo(pitch));
            clone.ResetSettingsToDefaults();
            Assert.That(pitchFor(clone).Value, Is.Zero);
            Assert.That(new APIMod(clone).Settings.ContainsKey("adjust_pitch"), Is.False);
            Assert.That(pitchFor(restored).Value, Is.EqualTo(pitch));
        }

        [TestCase("DT", 1.73)]
        [TestCase("HT", 0.62)]
        public void AutomaticPitchSurvivesStorageCloningAndReset(string acronym, double rate)
        {
            var mod = createMod(acronym);
            mod.SpeedChange.Value = rate;
            automaticPitchFor(mod).Value = true;
            var apiMod = new APIMod(mod);
            Assert.That(apiMod.Settings["automatic_pitch_adjustment"], Is.True);

            // The stored slider value must not override the rate-derived value in automatic mode.
            apiMod.Settings["adjust_pitch"] = -2.0;
            var restored = (ModRateAdjust)JsonConvert.DeserializeObject<APIMod>(JsonConvert.SerializeObject(apiMod))!.ToMod(new TypeBeatRuleset());
            Assert.That(automaticPitchFor(restored).Value, Is.True);
            Assert.That(pitchFor(restored).Disabled, Is.True);
            Assert.That(pitchFor(restored).Value, Is.EqualTo(12 * Math.Log2(rate)).Within(1e-12));
            Assert.That(restored, Is.EqualTo(mod));

            var clone = (ModRateAdjust)restored.DeepClone();
            Assert.That(automaticPitchFor(clone).Value, Is.True);
            Assert.That(pitchFor(clone).Disabled, Is.True);
            Assert.That(clone, Is.EqualTo(restored));
            clone.SpeedChange.Value = acronym == "DT" ? 1.9 : 0.8;
            Assert.That(pitchFor(clone).Value, Is.EqualTo(12 * Math.Log2(clone.SpeedChange.Value)).Within(1e-12));
            Assert.That(pitchFor(restored).Value, Is.EqualTo(12 * Math.Log2(rate)).Within(1e-12));

            clone.ResetSettingsToDefaults();
            Assert.That(automaticPitchFor(clone).Value, Is.False);
            Assert.That(pitchFor(clone).Disabled, Is.False);
            Assert.That(pitchFor(clone).Value, Is.Zero);
            Assert.That(automaticPitchFor(restored).Value, Is.True);
        }

        [Test]
        public void CopyingCommonSettingsKeepsAutomaticPitchAtTheTargetRate()
        {
            var source = new TypeBeatModDoubleTime { SpeedChange = { Value = 1.73 }, AutomaticPitchAdjustment = { Value = true } };
            var target = new TypeBeatModHalfTime();

            Assert.DoesNotThrow(() => target.CopyCommonSettingsFrom(source));
            Assert.That(target.SpeedChange.Value, Is.EqualTo(0.99));
            Assert.That(target.AutomaticPitchAdjustment.Value, Is.True);
            Assert.That(target.AdjustPitch.Disabled, Is.True);
            Assert.That(target.AdjustPitch.Value, Is.EqualTo(12 * Math.Log2(0.99)).Within(1e-12));
        }

        [TestCase("DT", 1.5, true)]
        [TestCase("DT", 1.73, true)]
        [TestCase("HT", 0.75, true)]
        [TestCase("HT", 0.62, true)]
        [TestCase("DT", 1.73, false)]
        [TestCase("HT", 0.62, false)]
        public void LegacyPitchTogglesRetainTheirOriginalSound(string acronym, double rate, bool adjustPitch)
        {
            // Deliberately put the legacy toggle before speed in the JSON to exercise saved presets too.
            string json = JsonConvert.SerializeObject(new
            {
                acronym,
                settings = new { adjust_pitch = adjustPitch, speed_change = rate },
            });
            var mod = (ModRateAdjust)JsonConvert.DeserializeObject<APIMod>(json)!.ToMod(new TypeBeatRuleset());
            var adjustments = new AudioAdjustments();
            mod.ApplyToTrack(adjustments);

            Assert.That(automaticPitchFor(mod).Value, Is.EqualTo(adjustPitch));
            Assert.That(pitchFor(mod).Disabled, Is.EqualTo(adjustPitch));
            Assert.That(adjustments.AggregateFrequency.Value, Is.EqualTo(adjustPitch ? rate : 1).Within(1e-12));
            Assert.That(adjustments.AggregateTempo.Value, Is.EqualTo(adjustPitch ? 1 : rate).Within(1e-12));
        }

        [TestCase("DT", 1.5)]
        [TestCase("HT", 0.75)]
        public void LegacyDefaultRateAndPitchPresetsRemainCompatible(string acronym, double defaultRate)
        {
            var apiMod = new APIMod { Acronym = acronym };
            var mod = (ModRateAdjust)apiMod.ToMod(new TypeBeatRuleset());
            Assert.That(pitchFor(mod).Value, Is.Zero);

            apiMod.Settings["adjust_pitch"] = true;
            mod = (ModRateAdjust)apiMod.ToMod(new TypeBeatRuleset());
            var adjustments = new AudioAdjustments();
            mod.ApplyToTrack(adjustments);
            Assert.That(adjustments.AggregateFrequency.Value, Is.EqualTo(defaultRate).Within(1e-12));
            Assert.That(adjustments.AggregateTempo.Value, Is.EqualTo(1).Within(1e-12));
        }

        private static ModRateAdjust createMod(string acronym) => (ModRateAdjust)new TypeBeatRuleset().CreateModFromAcronym(acronym)!;

        private static BindableBool automaticPitchFor(ModRateAdjust mod) => mod switch
        {
            TypeBeatModDoubleTime dt => dt.AutomaticPitchAdjustment,
            TypeBeatModHalfTime ht => ht.AutomaticPitchAdjustment,
            _ => throw new ArgumentOutOfRangeException(nameof(mod)),
        };

        private static BindableNumber<double> pitchFor(ModRateAdjust mod) => mod switch
        {
            TypeBeatModDoubleTime dt => dt.AdjustPitch,
            TypeBeatModHalfTime ht => ht.AdjustPitch,
            _ => throw new ArgumentOutOfRangeException(nameof(mod)),
        };
    }
}
