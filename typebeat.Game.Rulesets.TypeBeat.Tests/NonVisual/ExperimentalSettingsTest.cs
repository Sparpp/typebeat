// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Framework.Graphics.Containers;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Overlays.Settings;
using typebeat.Game.Overlays.Settings.Sections;
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
        /// Backlog 479: the "Visual feedback" and "Timing tint" groups settled and moved to the
        /// ruleset's own settings in Settings &gt; Gameplay. What is left here is the "Lyric timing"
        /// group and the "Sound" group (the keyboard hitsounds, on trial), and none of the moved
        /// controls may come back (their home is pinned by
        /// <c>TypeBeatSettingsTest.TheSettingsMovedFromExperimentalKeepTheirWiring</c>).
        /// </summary>
        [Test]
        public void OnlyLyricTimingAndSoundAreLeftHere()
        {
            var ruleset = new TypeBeatRuleset();
            var subsection = (TypeBeatExperimentalSettingsSubsection)ruleset.CreateExperimentalSettings()!;

            using (var config = new TypeBeatRulesetConfigManager(null, ruleset.RulesetInfo))
            {
                var controls = subsection.BuildControls(config);

                Assert.That(controls.OfType<OsuSpriteText>().Select(t => t.Text.ToString()), Is.EqualTo(new[] { LocalAlignerVersion.SETTINGS_HEADING, TypeBeatExperimentalSettingsSubsection.SOUND_HEADING }));

                var terms = controls.OfType<SettingsItemV2>().SelectMany(c => c.Control.FilterTerms).Select(t => t.ToString()).ToArray();

                foreach (string caption in new[]
                         {
                             "Show sync metric",
                             "Pace bar reveal",
                             "Colour typed-letter bars by judgement",
                             "Red screen edges before forced line changes",
                             TypeBeatSettingsSubsection.TIMING_TINT_CAPTION,
                             TypeBeatSettingsSubsection.TIMING_TINT_MIN_CAPTION,
                             TypeBeatSettingsSubsection.TIMING_TINT_MAX_CAPTION,
                             TypeBeatSettingsSubsection.TIMING_TINT_EARLY_COLOUR_CAPTION,
                             TypeBeatSettingsSubsection.TIMING_TINT_LATE_COLOUR_CAPTION,
                         })
                {
                    Assert.That(terms, Does.Not.Contain(caption), $"'{caption}' moved to Settings > Gameplay");
                }
            }
        }

        /// <summary>
        /// Everything still on trial in the section: the local auto-aligner's high-accuracy tier (its
        /// on/off switch was hidden by backlog 381 and deleted by backlog 413, so it must NOT be
        /// listed) and the two install buttons. Pinned by their labels because that is the only thing
        /// a player sees.
        ///
        /// <para>Everything else that used to be listed here settled and moved to the ruleset's own
        /// settings in Settings &gt; Gameplay: the four typing behaviours first, then (backlog 479) the
        /// sync metric, pace bar reveal, judgement-coloured bars, the push warning edges and the
        /// timing tint, pinned by <c>TypeBeatSettingsTest</c>.</para>
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
                    "High-accuracy alignment (about 4x slower per import)",
                }), "backlog 413 deleted the 'Use local auto-aligner' switch: the install is the opt-in");

                // Backlog 499: the keyboard hitsounds are a three-way switch, off by default, bound to the
                // setting KeyboardHitsounds reads.
                var hitsounds = controls.OfType<SettingsItemV2>().Select(c => c.Control).OfType<FormTriStateSwitch<KeyboardHitsoundMode>>().Single();

                Assert.That(hitsounds.Caption.ToString(), Is.EqualTo(TypeBeatExperimentalSettingsSubsection.KEYBOARD_HITSOUNDS_CAPTION));
                Assert.That(hitsounds.HintText.ToString(), Is.EqualTo(TypeBeatExperimentalSettingsSubsection.KEYBOARD_HITSOUNDS_HINT));
                Assert.That(hitsounds.Current.Value, Is.EqualTo(KeyboardHitsoundMode.Off));
                Assert.That(TriStateSwitch<KeyboardHitsoundMode>.VALUES, Is.EqualTo(new[] { KeyboardHitsoundMode.Off, KeyboardHitsoundMode.PerSyllable, KeyboardHitsoundMode.PerCharacter }),
                    "left to right: off, per syllable, per character");
                hitsounds.Current.Value = KeyboardHitsoundMode.PerCharacter;
                Assert.That(config.Get<KeyboardHitsoundMode>(TypeBeatRulesetSetting.KeyboardHitsoundMode), Is.EqualTo(KeyboardHitsoundMode.PerCharacter));

                Assert.That(controls.OfType<SettingsCheckbox>(), Is.Empty, "all experimental toggles use the shared form UI");

                // OFF: the full tier makes every import about four times slower, a cost the
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
                var buttons = controls.OfType<Container>().Select(c => c.Child).OfType<FormButton>().ToArray();
                var installButton = buttons.First();

                Assert.That(installButton.Caption.ToString(), Is.EqualTo("Install local auto-aligner (~4.5 GB)"));

                // Demucs (aligner version 12) right under it, installed on its own.
                Assert.That(buttons.Select(b => b.Caption.ToString()), Is.EqualTo(new[] { "Install local auto-aligner (~4.5 GB)", "Install Demucs (~2 GB)" }));

                // ILocalAlignerManager is resolved CanBeNull and is absent here, exactly as it is in a
                // headless scene: the buttons must go dead rather than throw on a click nothing services.
                Assert.That(buttons.All(b => !b.Enabled.Value), Is.True);
            }
        }

        /// <summary>
        /// Backlog 413: the "use local auto-aligner" switch backlog 381 hid is deleted for good. A player
        /// opts IN to the server aligner on the import screen instead, so Settings carries no switch of
        /// either kind: no local-aligner row, and no server-aligner row either.
        /// </summary>
        [Test]
        public void ThereIsNoAlignerSwitchInSettings()
        {
            var ruleset = new TypeBeatRuleset();
            var subsection = (TypeBeatExperimentalSettingsSubsection)ruleset.CreateExperimentalSettings()!;

            using (var config = new TypeBeatRulesetConfigManager(null, ruleset.RulesetInfo))
            {
                var captions = subsection.BuildControls(config).OfType<SettingsItemV2>().Select(c => c.Control).OfType<FormCheckBox>().Select(c => c.Caption.ToString()).ToArray();

                Assert.That(captions, Does.Not.Contain("Use local auto-aligner"));
                Assert.That(captions.Where(c => c.Contains("server", StringComparison.OrdinalIgnoreCase)), Is.Empty);
            }
        }

        /// <summary>
        /// The path every aligner sentence names ("Update it in Settings &gt; Experimental &gt; Lyric
        /// timing") is the section's title and the heading the install button sits under, read off the
        /// drawables themselves, so renaming either moves the sentences with it.
        /// </summary>
        [Test]
        public void TheSettingsPathIsTheHeadersOnScreen()
        {
            var ruleset = new TypeBeatRuleset();
            var subsection = (TypeBeatExperimentalSettingsSubsection)ruleset.CreateExperimentalSettings()!;

            using (var config = new TypeBeatRulesetConfigManager(null, ruleset.RulesetInfo))
            {
                var controls = subsection.BuildControls(config);
                int button = Array.FindIndex(controls, c => c is Container { Child: FormButton });
                string heading = controls.Take(button).OfType<OsuSpriteText>().Last().Text.ToString();

                Assert.That(heading, Is.EqualTo(LocalAlignerVersion.SETTINGS_HEADING));
                Assert.That(LocalAlignerVersion.SETTINGS_PATH, Is.EqualTo($"Settings > {new ExperimentalSection().Header} > {heading}"));
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
                Is.EqualTo("Install local auto-aligner (~4.5 GB)"));
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

        /// <summary>The Demucs button (aligner version 12) offers Install, Repair or Reinstall, independently of the aligner's.</summary>
        [Test]
        public void TheDemucsButtonOffersInstallRepairOrReinstall()
        {
            Assert.That(TypeBeatExperimentalSettingsSubsection.DemucsButtonText(new FakeAlignerManager()), Is.EqualTo("Install Demucs (~2 GB)"));
            Assert.That(TypeBeatExperimentalSettingsSubsection.DemucsButtonText(new FakeAlignerManager { DemucsNeedsRepair = true }), Is.EqualTo("Repair Demucs"));
            Assert.That(TypeBeatExperimentalSettingsSubsection.DemucsButtonText(new FakeAlignerManager { DemucsInstalled = true }), Is.EqualTo("Reinstall Demucs"));
            Assert.That(TypeBeatExperimentalSettingsSubsection.DemucsButtonText(new FakeAlignerManager { IsInstalled = true }), Is.EqualTo("Install Demucs (~2 GB)"),
                "the aligner being installed says nothing about Demucs");
            Assert.That(TypeBeatExperimentalSettingsSubsection.DemucsButtonTooltip(new FakeAlignerManager()), Does.Contain("Vocals waveform"));
        }

        private class FakeAlignerManager : ILocalAlignerManager
        {
            public bool IsInstalled { get; init; }
            public bool NeedsRepair { get; init; }
            public bool DemucsInstalled { get; init; }
            public bool DemucsNeedsRepair { get; init; }
            public string? InstalledDevice => null;
            public bool GpuDetected => false;
            public string? InstalledVersion { get; init; }
            public string? ShippedVersion { get; init; }

            public Task<LyricImportResult> InstallAsync(Action<string> progress, CancellationToken token) => throw new NotSupportedException();
        }
    }
}
