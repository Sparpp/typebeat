// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Framework.Graphics.Containers;
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
                    "High-accuracy alignment (about 4x slower per import)",
                }), "backlog 381 hid the 'Use local auto-aligner' switch: the install is the opt-in");

                Assert.That(controls.OfType<SettingsItemV2>().Select(c => c.Control).OfType<FormSliderBar<float>>(), Is.Empty, "pop-in controls live in type!beat");
                Assert.That(controls.OfType<SettingsCheckbox>(), Is.Empty, "all experimental toggles use the shared form UI");

                // The one that has to be OFF here: the whole point of the toggle is that the metric
                // is gone unless a player goes looking for it, so a checkbox that came up ticked
                // would ship the thing backlog 251 removed.
                var syncCheckbox = controls.OfType<SettingsItemV2>().Select(c => c.Control).OfType<FormCheckBox>().Single(c => c.Caption.ToString() == "Show sync metric");

                Assert.That(syncCheckbox.Current.Value, Is.False);

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

        /// <summary>
        /// Backlog 410: a mismatched install keeps automatic alignment off, so the button names both
        /// versions with the verb that fixes it (update an older one, reinstall a newer one) and its
        /// hint says alignment is unavailable until then. A matching one is a plain reinstall.
        /// </summary>
        [Test]
        public void TheButtonSaysAMismatchedAlignerBlocksAlignmentUntilFixed()
        {
            var older = new FakeAlignerManager { IsInstalled = true, InstalledVersion = "8", ShippedVersion = "9" };
            var unversioned = new FakeAlignerManager { IsInstalled = true, InstalledVersion = null, ShippedVersion = "9" };
            var newer = new FakeAlignerManager { IsInstalled = true, InstalledVersion = "10", ShippedVersion = "9" };
            var matching = new FakeAlignerManager { IsInstalled = true, InstalledVersion = "9", ShippedVersion = "9" };

            Assert.That(TypeBeatExperimentalSettingsSubsection.InstallButtonText(older), Is.EqualTo("Update local auto-aligner (v8 → v9)"));
            Assert.That(TypeBeatExperimentalSettingsSubsection.InstallButtonText(unversioned), Is.EqualTo("Update local auto-aligner (v1 → v9)"));
            Assert.That(TypeBeatExperimentalSettingsSubsection.InstallButtonText(newer), Is.EqualTo("Reinstall local auto-aligner (v10 → v9)"));
            Assert.That(TypeBeatExperimentalSettingsSubsection.InstallButtonText(matching), Is.EqualTo("Reinstall local auto-aligner"));

            Assert.That(TypeBeatExperimentalSettingsSubsection.InstallButtonTooltip(older), Does.Contain("automatic alignment is unavailable until you update it"));
            Assert.That(TypeBeatExperimentalSettingsSubsection.InstallButtonTooltip(newer), Does.Contain("automatic alignment is unavailable until you reinstall it"));
            Assert.That(TypeBeatExperimentalSettingsSubsection.InstallButtonTooltip(matching), Does.Not.Contain("unavailable"));
        }

        private class FakeAlignerManager : ILocalAlignerManager
        {
            public bool IsInstalled { get; init; }
            public bool NeedsRepair { get; init; }
            public string? InstalledDevice => null;
            public bool GpuDetected => false;
            public string? InstalledVersion { get; init; }
            public string? ShippedVersion { get; init; }

            public Task<LyricImportResult> InstallAsync(Action<string> progress, CancellationToken token) => throw new NotSupportedException();
        }
    }
}
