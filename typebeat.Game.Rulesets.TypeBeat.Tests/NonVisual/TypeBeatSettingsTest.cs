// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Overlays.Settings;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.UI;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Pins the contents of Settings &gt; type!beat: the settled set, which the four typing behaviours
    /// that used to be on trial in Settings &gt; Experimental joined - space to skip a word, manual
    /// newlines, the space error dot and the syllable markers.
    ///
    /// <para>What the move must not have changed is the SETTINGS. Realm keys the stored rows by enum
    /// member name, so the four keep their values across the move, and this asserts the wiring rather
    /// than trusting it: each checkbox has to read back what the config manager holds, which nothing
    /// but the same underlying setting can do.</para>
    ///
    /// <para>The controls are built through <c>BuildControls</c> rather than by loading the subsection:
    /// the dependency loader needs a game host, and all this needs to know is which controls exist, in
    /// which order.</para>
    /// </summary>
    [TestFixture]
    public class TypeBeatSettingsTest
    {
        [Test]
        public void TypeBeatAnswersTheSettingsHook()
        {
            var ruleset = new TypeBeatRuleset();

            Assert.That(ruleset.CreateSettings(), Is.InstanceOf<TypeBeatSettingsSubsection>(),
                "the type!beat section is empty unless the ruleset hands it a subsection");
        }

        /// <summary>
        /// The four moved controls, in source order, each bound to the setting it was always bound to.
        /// The labels are what a player sees, and the wiring is what makes the move invisible to them.
        /// </summary>
        [Test]
        public void TheSettledSettingsAreAllPresent()
        {
            var ruleset = new TypeBeatRuleset();
            var subsection = (TypeBeatSettingsSubsection)ruleset.CreateSettings()!;

            using (var config = new TypeBeatRulesetConfigManager(null, ruleset.RulesetInfo))
            {
                var controls = subsection.BuildControls(config);
                var checkboxes = controls.OfType<SettingsItemV2>().Select(c => c.Control).OfType<FormCheckBox>().ToList();

                Assert.That(checkboxes.Select(c => c.Caption.ToString()), Is.EqualTo(new[]
                {
                    "Space to skip current word",
                    "Manual newlines",
                    "Use map fonts",
                    "Use space error dot",
                    "Show syllable markers",
                    "Show word pace colours",
                }));

                // THE WIRING, not a copy of it: flip each setting at the config manager and the
                // checkbox has to follow, which is exactly what a stored value reading correctly
                // depends on.
                var settings = new[]
                {
                    TypeBeatRulesetSetting.SpaceSkipsWord,
                    TypeBeatRulesetSetting.ManualNewlines,
                    TypeBeatRulesetSetting.UseMapFonts,
                    TypeBeatRulesetSetting.UseSpaceErrorDot,
                    TypeBeatRulesetSetting.ShowSyllableMarkers,
                    TypeBeatRulesetSetting.ShowPaceColours,
                };

                for (int i = 0; i < settings.Length; i++)
                {
                    var setting = config.GetBindable<bool>(settings[i]);

                    Assert.That(checkboxes[i].Current.Value, Is.EqualTo(setting.Value), $"{settings[i]} must start on the stored value");

                    setting.Value = !setting.Value;

                    Assert.That(checkboxes[i].Current.Value, Is.EqualTo(setting.Value), $"{settings[i]} must read the setting it is bound to");

                    setting.Value = !setting.Value;
                }

                // And the defaults the game ships with, which the move must not have reset either.
                // Manual newlines, map fonts, the space error dot, syllable markers and pace colours
                // ship on by default.
                Assert.That(checkboxes.Select(c => c.Current.Value), Is.EqualTo(new[] { true, true, true, true, true, true }));
            }
        }

        /// <summary>
        /// The cosmetic and input controls the section always had are still there, in order, with the
        /// settled typing, lyric, caret, and feedback controls. Pinned
        /// as a whole so a control that silently left the section would be noticed here.
        /// </summary>
        [Test]
        public void TheSettledSectionKeepsItsOwnControlsToo()
        {
            var ruleset = new TypeBeatRuleset();
            var subsection = (TypeBeatSettingsSubsection)ruleset.CreateSettings()!;

            using (var config = new TypeBeatRulesetConfigManager(null, ruleset.RulesetInfo))
            {
                var controls = subsection.BuildControls(config);

                Assert.That(controls.OfType<SettingsItemV2>().Select(c => c.Control switch
                {
                    FormCheckBox checkbox => checkbox.Caption.ToString(),
                    FormSliderBar<float> slider => slider.Caption.ToString(),
                    FormDropdown<string> font => font.Caption.ToString(),
                    FormDropdown<KeyboardLayout> layout => layout.Caption.ToString(),
                    FormDropdown<CaretStyle> caret => caret.Caption.ToString(),
                    _ => "?",
                }), Is.EqualTo(new[]
                {
                    "Keyboard layout",
                    "Space to skip current word",
                    "Manual newlines",


                    "Use map fonts",
                    "Typing font",
                    "Gameplay font size",
                    "Lyric line spacing",
                    "Typing caret style",
                    "Song playhead style",
                    "Caret smoothing",
                    "Use space error dot",
                    "Sung syllable brightness",
                    "Show syllable markers",
                    "Show word pace colours",
                    "Pace colour maximum change",
                }));
            }
        }

        /// <summary>
        /// Backlog 334: the lyric font size slider sits directly above line spacing, steps by 1, and
        /// is bound to its own setting (flip the setting and the slider follows).
        /// </summary>
        [Test]
        public void TheLyricFontSizeSliderIsBoundToItsSetting()
        {
            var ruleset = new TypeBeatRuleset();
            var subsection = (TypeBeatSettingsSubsection)ruleset.CreateSettings()!;

            using (var config = new TypeBeatRulesetConfigManager(null, ruleset.RulesetInfo))
            {
                var sliders = subsection.BuildControls(config).OfType<SettingsSlider<float>>().ToList();
                var size = sliders.Single(s => s.LabelText.ToString() == "Lyric font size");

                Assert.That(sliders.IndexOf(size) + 1, Is.EqualTo(sliders.FindIndex(s => s.LabelText.ToString() == "Lyric line spacing")));
                Assert.That(size.KeyboardStep, Is.EqualTo(1f));
                Assert.That(size.TooltipText.ToString(), Is.EqualTo("Size of the lyric text during gameplay. Applies from the next play."));

                var setting = config.GetBindable<float>(TypeBeatRulesetSetting.LyricFontSize);
                Assert.That(size.Current.Value, Is.EqualTo(setting.Value));

                setting.Value = 60f;
                Assert.That(size.Current.Value, Is.EqualTo(60f));
            }
        }
    }
}
