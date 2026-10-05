// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Overlays.Settings;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Screens.ImportLyrics;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Pins the contents of Settings &gt; Experimental. The section itself holds nothing: it loops the
    /// available rulesets asking each for <c>CreateExperimentalSettings()</c>, so a ruleset that stops
    /// answering leaves an empty section on screen rather than failing anywhere. These assertions are
    /// what makes that silent, and dropping one of the controls, loud.
    ///
    /// The controls are built through <c>BuildControls</c> rather than by loading the subsection: the
    /// dependency loader needs a game host, and all this needs to know is which controls exist.
    /// </summary>
    [TestFixture]
    public class ExperimentalSettingsTest
    {
        [Test]
        public void TypeBeatAnswersTheExperimentalSettingsHook()
        {
            var ruleset = new TypeBeatRuleset();

            Assert.That(ruleset.CreateExperimentalSettings(), Is.InstanceOf<TypeBeatExperimentalSettingsSubsection>(),
                "the Experimental section is empty unless the ruleset hands it a subsection");
        }

        /// <summary>
        /// The timing tint's settings sit under their own "Timing tint" heading, between "Visual
        /// feedback" (which keeps the sync metric) and "Lyric timing", and their captions leave
        /// the feature's name to the heading.
        /// </summary>
        [Test]
        public void TheTimingTintSettingsSitUnderTheirOwnHeading()
        {
            var ruleset = new TypeBeatRuleset();
            var subsection = (TypeBeatExperimentalSettingsSubsection)ruleset.CreateExperimentalSettings()!;

            using (var config = new TypeBeatRulesetConfigManager(null, ruleset.RulesetInfo))
            {
                var controls = subsection.BuildControls(config).ToList();

                int heading(string text) => controls.FindIndex(c => c is OsuSpriteText t && t.Text.ToString() == text);

                int captioned(string caption) => controls.FindIndex(c => c is SettingsItemV2 item && item.Control.FilterTerms.Any(t => t.ToString() == caption));

                Assert.That(controls.OfType<OsuSpriteText>().Select(t => t.Text.ToString()), Is.EqualTo(new[]
                {
                    "Visual feedback",
                    TypeBeatExperimentalSettingsSubsection.TIMING_TINT_HEADING,
                    "Lyric timing",
                }), "the timing tint heading sits between visual feedback and lyric timing");

                int visualFeedback = heading("Visual feedback");
                int timingTint = heading(TypeBeatExperimentalSettingsSubsection.TIMING_TINT_HEADING);
                int lyricTiming = heading("Lyric timing");

                Assert.That(captioned("Show sync metric"), Is.InRange(visualFeedback + 1, timingTint - 1), "the sync metric stays under visual feedback");

                string[] tintCaptions =
                {
                    TypeBeatExperimentalSettingsSubsection.TIMING_TINT_CAPTION,
                    TypeBeatExperimentalSettingsSubsection.TIMING_TINT_MIN_CAPTION,
                    TypeBeatExperimentalSettingsSubsection.TIMING_TINT_MAX_CAPTION,
                    TypeBeatExperimentalSettingsSubsection.TIMING_TINT_EARLY_COLOUR_CAPTION,
                    TypeBeatExperimentalSettingsSubsection.TIMING_TINT_LATE_COLOUR_CAPTION,
                };

                foreach (string caption in tintCaptions)
                {
                    Assert.That(captioned(caption), Is.InRange(timingTint + 1, lyricTiming - 1), $"'{caption}' sits under the timing tint heading");
                    Assert.That(caption, Does.Not.StartWith("Timing tint"), "the heading names the feature, so the rows do not repeat it");
                }
            }
        }

        /// <summary>
        /// Everything still on trial in the section, in source order: the sync metric (backlog 251 put
        /// it behind a switch) and the local auto-aligner's high-accuracy tier (its on/off switch
        /// was hidden by backlog 381, so it must NOT be listed). Pinned by their
        /// labels because that is the only thing a player sees: the bindables behind them deliberately
        /// did not move (Realm keys stored rows by enum member name), so nothing else here would
        /// notice a control quietly going missing.
        ///
        /// <para>The four typing behaviours that used to be listed here - space to skip a word, manual
        /// newlines, the space error dot and the syllable markers - have settled and their controls
        /// now live in the type!beat section, pinned by
        /// <c>TypeBeatSettingsTest.TheSettledSettingsAreAllPresent</c>. What this test is left guarding
        /// is that they do not quietly come back here or go missing altogether.</para>
        ///
        /// <para>The sync one matters more than the others do: it is the ONLY way back to a display
        /// the game used to ship on, so losing the checkbox would not degrade a feature, it would
        /// delete one with no way to notice.</para>
        /// </summary>
        [Test]
        public void TheOnTrialSettingsAreAllPresent()
        {
            var ruleset = new TypeBeatRuleset();
            var subsection = (TypeBeatExperimentalSettingsSubsection)ruleset.CreateExperimentalSettings()!;

            using (var config = new TypeBeatRulesetConfigManager(null, ruleset.RulesetInfo))
            {
                var controls = subsection.BuildControls(config);

                Assert.That(controls.OfType<SettingsItemV2>().Select(c => c.Control).OfType<FormCheckBox>().Select(c => c.Caption.ToString()), Is.EqualTo(new[]
                {
                    "Show sync metric",
                    TypeBeatExperimentalSettingsSubsection.TIMING_TINT_CAPTION,
                    "High-accuracy alignment (about 4x slower per import)",
                }), "backlog 381 hid the 'Use local auto-aligner' switch: the install is the opt-in");

                // The only sliders here are the timing tint's strength range; pop-in controls live in type!beat.
                var sliders = controls.OfType<SettingsItemV2>().Select(c => c.Control).OfType<FormSliderBar<float>>().ToArray();

                Assert.That(sliders.Select(c => c.Caption.ToString()), Is.EqualTo(new[]
                {
                    TypeBeatExperimentalSettingsSubsection.TIMING_TINT_MIN_CAPTION,
                    TypeBeatExperimentalSettingsSubsection.TIMING_TINT_MAX_CAPTION,
                }));

                Assert.That(sliders[0].Current.Value, Is.EqualTo(33f));
                Assert.That(sliders[1].Current.Value, Is.EqualTo(100f));
                sliders[0].Current.Value = 50f;
                Assert.That(config.Get<float>(TypeBeatRulesetSetting.TimingTintMinStrength), Is.EqualTo(50f), "bound to the stored setting, not a copy");

                // The two colour rows, which edit a colour over a stored #rrggbb string in both directions.
                var swatches = controls.OfType<SettingsItemV2>().Select(c => c.Control).OfType<FormColourSwatch>().ToArray();

                Assert.That(swatches.Select(s => s.Caption.ToString()), Is.EqualTo(new[]
                {
                    TypeBeatExperimentalSettingsSubsection.TIMING_TINT_EARLY_COLOUR_CAPTION,
                    TypeBeatExperimentalSettingsSubsection.TIMING_TINT_LATE_COLOUR_CAPTION,
                }));

                Assert.That(swatches[0].Current.Value, Is.EqualTo((Colour4)TypeBeatStyle.EarlyTint));
                Assert.That(swatches[1].Current.Value, Is.EqualTo((Colour4)TypeBeatStyle.LateTint));

                swatches[0].Current.Value = new Colour4((byte)18, (byte)52, (byte)86, (byte)255);
                Assert.That(config.Get<string>(TypeBeatRulesetSetting.TimingTintEarlyColour), Is.EqualTo("#123456"), "a picked colour is stored");

                config.SetValue(TypeBeatRulesetSetting.TimingTintLateColour, "#abcdef");
                Assert.That(swatches[1].Current.Value, Is.EqualTo(new Colour4((byte)0xab, (byte)0xcd, (byte)0xef, (byte)255)), "a stored change reaches the row");

                swatches[0].Current.SetDefault();
                Assert.That(config.Get<string>(TypeBeatRulesetSetting.TimingTintEarlyColour), Is.EqualTo("#ff8a3d"), "resetting the row resets the setting");

                // Settings rows like the rest, so the settings search finds them by caption.
                Assert.That(swatches.Select(s => s.FilterTerms.Single().ToString()), Is.EqualTo(new[]
                {
                    TypeBeatExperimentalSettingsSubsection.TIMING_TINT_EARLY_COLOUR_CAPTION,
                    TypeBeatExperimentalSettingsSubsection.TIMING_TINT_LATE_COLOUR_CAPTION,
                }));
                Assert.That(controls.OfType<SettingsCheckbox>(), Is.Empty, "all experimental toggles use the shared form UI");

                // The one that has to be OFF here: the whole point of the toggle is that the metric
                // is gone unless a player goes looking for it, so a checkbox that came up ticked
                // would ship the thing backlog 251 removed.
                var syncCheckbox = controls.OfType<SettingsItemV2>().Select(c => c.Control).OfType<FormCheckBox>().Single(c => c.Caption.ToString() == "Show sync metric");

                Assert.That(syncCheckbox.Current.Value, Is.False);

                // The timing tint is opt-in on the same terms, and bound to its own stored setting.
                var timingCheckbox = controls.OfType<SettingsItemV2>().Select(c => c.Control).OfType<FormCheckBox>()
                                             .Single(c => c.Caption.ToString() == TypeBeatExperimentalSettingsSubsection.TIMING_TINT_CAPTION);

                Assert.That(timingCheckbox.Current.Value, Is.False);
                timingCheckbox.Current.Value = true;
                Assert.That(config.Get<bool>(TypeBeatRulesetSetting.ShowTimingTint), Is.True);
                Assert.That(config.Get<bool>(TypeBeatRulesetSetting.ShowSyncMetric), Is.False, "a separate switch, not a mode of the sync metric");

                // Also OFF: the full tier makes every import about four times slower, a cost the
                // player opts into. The hint is pinned whole because it is the only place the trade
                // (accuracy gained, minutes spent) is spelled out.
                var qualityCheckbox = controls.OfType<SettingsItemV2>().Select(c => c.Control).OfType<FormCheckBox>()
                                              .Single(c => c.Caption.ToString() == TypeBeatExperimentalSettingsSubsection.HIGH_QUALITY_CAPTION);

                Assert.That(qualityCheckbox.Current.Value, Is.False);
                Assert.That(qualityCheckbox.HintText.ToString(), Is.EqualTo(
                    "The aligner listens to the song eight times instead of twice. That times about one more word in a hundred "
                    + "correctly on stamped lyrics, and about two more when the lyrics have no timestamps at all. On a 6-core CPU "
                    + "a 4 minute song takes roughly 2 minutes to import instead of 30 seconds; on a 2-core machine, about twice that."));

                // Bound to the setting the importer reads, not to a copy: ticking it reaches config.
                qualityCheckbox.Current.Value = true;
                Assert.That(config.Get<bool>(TypeBeatRulesetSetting.LocalAlignerHighQuality), Is.True);

                // The high-accuracy checkbox is meaningless without the installer, so they sit together.
                var installButton = controls.OfType<Container>().Select(c => c.Child).OfType<FormButton>().Single();

                Assert.That(installButton.Caption.ToString(), Is.EqualTo("Install local auto-aligner (~2 GB)"));

                // ILocalAlignerManager is resolved CanBeNull and is absent here, exactly as it is in a
                // headless scene: the button must go dead rather than throw on a click nothing services.
                Assert.That(installButton.Enabled.Value, Is.False);
            }
        }

        /// <summary>
        /// Backlog 381: the local-aligner switch is hidden, not deleted. With the surfacing flag
        /// on, the row comes back in its old place (between the sync metric and the high-accuracy
        /// tier) still bound to the same setting, so a returning hosted aligner only has to flip
        /// <see cref="TypeBeatRulesetConfigManager.LOCAL_ALIGNER_TOGGLE_SURFACED"/>.
        /// </summary>
        [Test]
        public void TheLocalAlignerSwitchIsHiddenButComesBackWithItsFlag()
        {
            var ruleset = new TypeBeatRuleset();
            var subsection = (TypeBeatExperimentalSettingsSubsection)ruleset.CreateExperimentalSettings()!;

            using (var config = new TypeBeatRulesetConfigManager(null, ruleset.RulesetInfo))
            {
                var hidden = subsection.BuildControls(config).OfType<SettingsItemV2>().Select(c => c.Control).OfType<FormCheckBox>();
                Assert.That(hidden.Select(c => c.Caption.ToString()), Does.Not.Contain(TypeBeatExperimentalSettingsSubsection.LOCAL_ALIGNER_CAPTION));

                var surfaced = subsection.BuildControls(config, showLocalAlignerToggle: true).OfType<SettingsItemV2>().Select(c => c.Control).OfType<FormCheckBox>().ToArray();

                Assert.That(surfaced.Select(c => c.Caption.ToString()), Is.EqualTo(new[]
                {
                    "Show sync metric",
                    TypeBeatExperimentalSettingsSubsection.TIMING_TINT_CAPTION,
                    TypeBeatExperimentalSettingsSubsection.LOCAL_ALIGNER_CAPTION,
                    TypeBeatExperimentalSettingsSubsection.HIGH_QUALITY_CAPTION,
                }));

                var toggle = surfaced.Single(c => c.Caption.ToString() == TypeBeatExperimentalSettingsSubsection.LOCAL_ALIGNER_CAPTION);
                Assert.That(toggle.Current.Value, Is.True, "defaults on");

                toggle.Current.Value = false;
                Assert.That(config.Get<bool>(TypeBeatRulesetSetting.LocalAlignerEnabled), Is.False, "still bound to the stored setting, not a copy");
            }
        }

        /// <summary>
        /// Backlog 353: a venv whose setup never completed offers Repair, not Install, Update or
        /// Reinstall, whatever the version comparison says.
        /// </summary>
        [Test]
        public void TheButtonOffersRepairForAnIncompleteInstall()
        {
            Assert.That(TypeBeatExperimentalSettingsSubsection.InstallButtonText(new FakeAlignerManager { NeedsRepair = true }),
                Is.EqualTo("Repair local auto-aligner"));
            Assert.That(TypeBeatExperimentalSettingsSubsection.InstallButtonText(new FakeAlignerManager()),
                Is.EqualTo("Install local auto-aligner (~2 GB)"));
            Assert.That(TypeBeatExperimentalSettingsSubsection.InstallButtonText(new FakeAlignerManager { IsInstalled = true }),
                Is.EqualTo("Reinstall local auto-aligner"));
        }

        private class FakeAlignerManager : ILocalAlignerManager
        {
            public bool IsInstalled { get; init; }
            public bool NeedsRepair { get; init; }
            public string? InstalledDevice => null;
            public bool GpuDetected => false;
            public string? InstalledVersion => null;
            public string? ShippedVersion => null;
            public bool UpdateAvailable => false;

            public Task<LyricImportResult> InstallAsync(Action<string> progress, CancellationToken token) => throw new NotSupportedException();
        }
    }
}
