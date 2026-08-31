// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using typebeat.Game.Overlays.Settings;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.UI;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Pins the contents of Settings &gt; Experimental. The section itself holds nothing: it loops the
    /// available rulesets asking each for <c>CreateExperimentalSettings()</c>, so a ruleset that stops
    /// answering leaves an empty section on screen rather than failing anywhere. These assertions are
    /// what makes that silent, and dropping one of the three controls, loud.
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
        /// Everything on trial in the section, in source order: the three settings backlog 221 moved
        /// out of the type!beat section, plus the syllable markers backlog 225 opened here rather
        /// than in the settled set and the sync metric backlog 251 put behind a switch. Pinned by
        /// their labels because that is the only thing a player sees: the bindables behind them
        /// deliberately did not move (Realm keys stored rows by enum member name), so nothing else
        /// here would notice a control quietly going missing.
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

                Assert.That(controls.OfType<SettingsCheckbox>().Select(c => c.LabelText.ToString()), Is.EqualTo(new[]
                {
                    "Space to skip current word",
                    "Use space error dot",
                    "Show syllable markers",
                    "Show sync metric",
                    "Use local auto-aligner",
                }));

                // The one that has to be OFF here: the whole point of the toggle is that the metric
                // is gone unless a player goes looking for it, so a checkbox that came up ticked
                // would ship the thing backlog 251 removed.
                var syncCheckbox = controls.OfType<SettingsCheckbox>().Single(c => c.LabelText.ToString() == "Show sync metric");

                Assert.That(syncCheckbox.Current.Value, Is.False);

                // The aligner checkbox is meaningless without the installer, so the pair moved together.
                var installButton = controls.OfType<SettingsButton>().Single();

                Assert.That(installButton.Text.ToString(), Is.EqualTo("Install local auto-aligner (~2 GB)"));

                // ILocalAlignerManager is resolved CanBeNull and is absent here, exactly as it is in a
                // headless scene: the button must go dead rather than throw on a click nothing services.
                Assert.That(installButton.Enabled.Value, Is.False);
            }
        }
    }
}
