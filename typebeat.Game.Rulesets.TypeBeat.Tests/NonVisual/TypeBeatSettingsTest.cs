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
                    "Syllable fade-in",
                    "Text pop-in",
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
                    TypeBeatRulesetSetting.SyllableFadeIn,
                    TypeBeatRulesetSetting.TextPopIn,
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
                Assert.That(checkboxes.Select(c => c.Current.Value), Is.EqualTo(new[] { true, true, true, true, false, false, true, true }));
            }
        }

        /// <summary>
        /// The cosmetic and input controls the section always had are still there, in order, with the
        /// settled typing, lyric, caret, and feedback controls, and without the keyboard layout
        /// dropdown backlog 383 retired (typing and shortcuts both follow the OS layout now). Pinned
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
                    FormDropdown<CaretStyle> caret => caret.Caption.ToString(),
                    FormDropdown<PaceColourMode> mode => mode.Caption.ToString(),
                    _ => "?",
                }), Is.EqualTo(new[]
                {
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
                    "Syllable fade-in",
                    "Text pop-in",
                    "Pop-in amount",
                    "Show syllable markers",
                    "Show word pace colours",
                    "Pace colour mode",
                    "Pace colour maximum change",
                    "Pace colour opacity curve",
                }));

                var fadeIn = controls.OfType<SettingsItemV2>().Select(c => c.Control).OfType<FormCheckBox>().Single(c => c.Caption.ToString() == "Syllable fade-in");
                fadeIn.Current.Value = true;
                Assert.That(config.Get<bool>(TypeBeatRulesetSetting.SyllableFadeIn), Is.True);
                config.SetValue(TypeBeatRulesetSetting.SyllableFadeIn, false);
                Assert.That(fadeIn.Current.Value, Is.False);

                var popIn = controls.OfType<SettingsItemV2>().Select(c => c.Control).OfType<FormCheckBox>().Single(c => c.Caption.ToString() == "Text pop-in");
                Assert.That(popIn.Current.Value, Is.False);
                popIn.Current.Value = true;
                Assert.That(config.Get<bool>(TypeBeatRulesetSetting.TextPopIn), Is.True);
                config.SetValue(TypeBeatRulesetSetting.TextPopIn, false);
                Assert.That(popIn.Current.Value, Is.False);

                var amount = controls.OfType<SettingsItemV2>().Select(c => c.Control).OfType<FormSliderBar<float>>().Single(c => c.Caption.ToString() == "Pop-in amount");
                Assert.That(amount.Current.Value, Is.EqualTo(5f));
                amount.Current.Value = 12f;
                Assert.That(config.Get<float>(TypeBeatRulesetSetting.TextPopInAmount), Is.EqualTo(12f));
                config.SetValue(TypeBeatRulesetSetting.TextPopInAmount, 0f);
                Assert.That(amount.Current.Value, Is.Zero);
                config.SetValue(TypeBeatRulesetSetting.TextPopInAmount, 100f);
                Assert.That(amount.Current.Value, Is.EqualTo(20f), "amount is bounded to the slider range");

                var mode = controls.OfType<SettingsItemV2>().Select(c => c.Control).OfType<FormDropdown<PaceColourMode>>().Single();
                Assert.That(mode.Current.Value, Is.EqualTo(PaceColourMode.MapRelative));
                Assert.That(mode.Items, Is.EqualTo(new[] { PaceColourMode.MapRelative, PaceColourMode.AccelerationBased }));
                mode.Current.Value = PaceColourMode.AccelerationBased;
                Assert.That(config.Get<PaceColourMode>(TypeBeatRulesetSetting.PaceColourMode), Is.EqualTo(PaceColourMode.AccelerationBased));
                config.SetValue(TypeBeatRulesetSetting.PaceColourMode, PaceColourMode.MapRelative);
                Assert.That(mode.Current.Value, Is.EqualTo(PaceColourMode.MapRelative));

                var pace = controls.OfType<SettingsItemV2>().Select(c => c.Control).OfType<FormSliderBar<float>>().Single(c => c.Caption.ToString() == "Pace colour maximum change");
                Assert.That(pace.Current.Value, Is.EqualTo(100f));
                Assert.That(pace.HintText.ToString(), Does.Contain("Defaults to 100%"));
                var curve = controls.OfType<SettingsItemV2>().Select(c => c.Control).OfType<FormSliderBar<float>>().Single(c => c.Caption.ToString() == "Pace colour opacity curve");
                Assert.That(curve.Current.Value, Is.Zero);
                Assert.That(curve.LabelFormat!(0).ToString(), Is.EqualTo("Linear"));
                Assert.That(curve.LabelFormat!(100).ToString(), Is.EqualTo("Exponential"));
                curve.Current.Value = 65f;
                Assert.That(config.Get<float>(TypeBeatRulesetSetting.PaceColourOpacityCurve), Is.EqualTo(65f));
                config.SetValue(TypeBeatRulesetSetting.PaceColourOpacityCurve, 1000f);
                Assert.That(curve.Current.Value, Is.EqualTo(100f));

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
                var sliders = subsection.BuildControls(config).OfType<SettingsItemV2>().Select(c => c.Control).OfType<FormSliderBar<float>>().ToList();
                var size = sliders.Single(s => s.Caption.ToString() == "Gameplay font size");

                Assert.That(sliders.IndexOf(size) + 1, Is.EqualTo(sliders.FindIndex(s => s.Caption.ToString() == "Lyric line spacing")));
                Assert.That(size.KeyboardStep, Is.EqualTo(1f));
                Assert.That(size.HintText.ToString(), Is.EqualTo("Preferred lyric text size during play. Long lines shrink individually to fit the screen. Applies from the next play."));

                var setting = config.GetBindable<float>(TypeBeatRulesetSetting.LyricFontSize);
                Assert.That(size.Current.Value, Is.EqualTo(setting.Value));

                setting.Value = 60f;
                Assert.That(size.Current.Value, Is.EqualTo(60f));
            }
        }
    }
}
