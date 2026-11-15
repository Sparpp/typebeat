// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Graphics;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Overlays.Settings;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.UI;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Pins the contents of the ruleset's own settings (shown first in Settings &gt; Gameplay since
    /// backlog 479): the settled set, which settings that used to be on trial in Settings &gt;
    /// Experimental joined - first space to skip a word, manual newlines, the space error dot and the
    /// syllable markers, then the rest of visual feedback and the timing tint.
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
                "Settings > Gameplay shows no type!beat settings unless the ruleset hands it a subsection");
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
                    "Keyboard hitsounds",
                    "Use map fonts",
                    "Use typing font in Editor",
                    "Use space error dot",
                    "Approach bars",
                    "Text pop-in",
                    "Show syllable markers",
                    "Show backing vocals",
                    "Show word pace colours",
                    "Show sync metric",
                    "Colour typed-letter bars by judgement",
                    "Red screen edges before forced line changes",
                    TypeBeatSettingsSubsection.TIMING_TINT_CAPTION,
                }));

                // THE WIRING, not a copy of it: flip each setting at the config manager and the
                // checkbox has to follow, which is exactly what a stored value reading correctly
                // depends on.
                var settings = new[]
                {
                    TypeBeatRulesetSetting.SpaceSkipsWord,
                    TypeBeatRulesetSetting.ManualNewlines,
                    TypeBeatRulesetSetting.KeyboardHitsounds,
                    TypeBeatRulesetSetting.UseMapFonts,
                    TypeBeatRulesetSetting.UseTypingFontInEditor,
                    TypeBeatRulesetSetting.UseSpaceErrorDot,
                    TypeBeatRulesetSetting.ApproachBars,
                    TypeBeatRulesetSetting.TextPopIn,
                    TypeBeatRulesetSetting.ShowSyllableMarkers,
                    TypeBeatRulesetSetting.ShowBackingVocals,
                    TypeBeatRulesetSetting.ShowPaceColours,
                    TypeBeatRulesetSetting.ShowSyncMetric,
                    TypeBeatRulesetSetting.JudgementColouredBars,
                    TypeBeatRulesetSetting.LinePushWarningEdges,
                    TypeBeatRulesetSetting.ShowTimingTint,
                };

                for (int i = 0; i < settings.Length; i++)
                {
                    var setting = config.GetBindable<bool>(settings[i]);

                    Assert.That(checkboxes[i].Current.Value, Is.EqualTo(setting.Value), $"{settings[i]} must start on the stored value");

                    setting.Value = !setting.Value;

                    Assert.That(checkboxes[i].Current.Value, Is.EqualTo(setting.Value), $"{settings[i]} must read the setting it is bound to");

                    setting.Value = !setting.Value;
                }

                // And the defaults a NEW install ships with (2026-10-25): Space to skip, Approach bars
                // and word pace colours are off; the rest are on, backing vocals (backlog 445) included.
                // The four that came from Experimental (backlog 479) are all opt-in, so all off.
                Assert.That(checkboxes.Select(c => c.Current.Value), Is.EqualTo(new[] { false, true, false, true, true, true, false, true, true, true, false, false, false, false, false }));
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
                    FormDropdown<JudgementIndicatorMode> indicator => indicator.Caption.ToString(),
                    FormDropdown<PaceBarRevealMode> reveal => reveal.Caption.ToString(),
                    FormColourSwatch swatch => swatch.Caption.ToString(),
                    _ => "?",
                }), Is.EqualTo(new[]
                {
                    "Space to skip current word",
                    "Manual newlines",
                    "Keyboard hitsounds",


                    "Use map fonts",
                    "Typing font",
                    "Use typing font in Editor",
                    "Gameplay font size",
                    "Lyric line spacing",
                    "Typing caret style",
                    "Song playhead style",
                    "Caret smoothing",
                    "Use space error dot",
                    "Approach bars",
                    "Approach Bar Alignment",
                    "Judgement Indicator",
                    "Indicator Alignment",
                    "Indicator Lead-In",
                    "Indicator Brightness",
                    "Text pop-in",
                    "Pop-in amount",
                    "Show syllable markers",
                    "Show backing vocals",
                    "Show word pace colours",
                    "Pace colour mode",
                    "Pace colour maximum change",
                    "Pace colour opacity curve",
                    "Pace colour gradient",
                    "Show sync metric",
                    "Pace bar reveal",
                    "Colour typed-letter bars by judgement",
                    "Red screen edges before forced line changes",
                    TypeBeatSettingsSubsection.TIMING_TINT_CAPTION,
                    TypeBeatSettingsSubsection.TIMING_TINT_MIN_CAPTION,
                    TypeBeatSettingsSubsection.TIMING_TINT_MAX_CAPTION,
                    TypeBeatSettingsSubsection.TIMING_TINT_EARLY_COLOUR_CAPTION,
                    TypeBeatSettingsSubsection.TIMING_TINT_LATE_COLOUR_CAPTION,
                }));

                var indicator = controls.OfType<SettingsItemV2>().Select(c => c.Control).OfType<FormDropdown<JudgementIndicatorMode>>().Single();
                var indicatorModes = new[]
                {
                    JudgementIndicatorMode.None,
                    JudgementIndicatorMode.FadeIn,
                    JudgementIndicatorMode.BottomToTopFill,
                };
                Assert.That(indicator.Items, Is.EqualTo(indicatorModes));
                Assert.That(indicatorModes.Select(mode => mode.GetType().GetField(mode.ToString())!.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false).Cast<System.ComponentModel.DescriptionAttribute>().Single().Description), Is.EqualTo(new[]
                {
                    "None", "Fade-In", "Bottom-to-top fill",
                }));
                Assert.That(indicator.Current.Value, Is.EqualTo(JudgementIndicatorMode.BottomToTopFill));
                foreach (var indicatorMode in indicatorModes)
                {
                    indicator.Current.Value = indicatorMode;
                    Assert.That(config.Get<JudgementIndicatorMode>(TypeBeatRulesetSetting.JudgementIndicator), Is.EqualTo(indicatorMode));
                }
                config.SetValue(TypeBeatRulesetSetting.JudgementIndicator, JudgementIndicatorMode.FadeIn);
                Assert.That(indicator.Current.Value, Is.EqualTo(JudgementIndicatorMode.FadeIn));

                // Indicator Alignment (revived by backlog 475 on a new key) and the independent Approach
                // Bar Alignment: both start at the centre, share the old slider's labels, and moving one
                // never moves the other.
                var alignment = controls.OfType<SettingsItemV2>().Select(c => c.Control).OfType<FormSliderBar<float>>().Single(c => c.Caption.ToString() == "Indicator Alignment");
                var barAlignment = controls.OfType<SettingsItemV2>().Select(c => c.Control).OfType<FormSliderBar<float>>().Single(c => c.Caption.ToString() == "Approach Bar Alignment");
                foreach (var slider in new[] { alignment, barAlignment })
                {
                    Assert.That(slider.Current.Value, Is.EqualTo(100f), slider.Caption.ToString());
                    Assert.That(slider.LabelFormat!(0).ToString(), Is.EqualTo("Great window start"));
                    Assert.That(slider.LabelFormat!(100).ToString(), Is.EqualTo("Great window centre"));
                    Assert.That(slider.LabelFormat!(40).ToString(), Is.EqualTo("40% towards centre"));
                }

                Assert.That(alignment.Current.Disabled, Is.False, "the default fill indicator has an alignment to move");
                alignment.Current.Value = 30f;
                Assert.That(config.Get<float>(TypeBeatRulesetSetting.IndicatorAlignment), Is.EqualTo(30f));
                Assert.That(config.Get<float>(TypeBeatRulesetSetting.ApproachBarAlignment), Is.EqualTo(100f), "the bar alignment is independent");
                config.SetValue(TypeBeatRulesetSetting.IndicatorAlignment, 1000f);
                Assert.That(alignment.Current.Value, Is.EqualTo(100f));
                config.SetValue(TypeBeatRulesetSetting.IndicatorAlignment, -1f);
                Assert.That(alignment.Current.Value, Is.Zero);

                // Approach bars ship off, so their alignment starts greyed out like the gradient below.
                Assert.That(barAlignment.Current.Disabled, Is.True);
                config.SetValue(TypeBeatRulesetSetting.ApproachBars, true);
                Assert.That(barAlignment.Current.Disabled, Is.False);
                barAlignment.Current.Value = 70f;
                Assert.That(config.Get<float>(TypeBeatRulesetSetting.ApproachBarAlignment), Is.EqualTo(70f));
                Assert.That(config.Get<float>(TypeBeatRulesetSetting.IndicatorAlignment), Is.Zero, "the indicator alignment is independent");
                config.SetValue(TypeBeatRulesetSetting.ApproachBars, false);
                Assert.That(barAlignment.Current.Disabled, Is.True);
                Assert.That(barAlignment.Current.Value, Is.EqualTo(70f), "greying out keeps the chosen value");

                config.SetValue(TypeBeatRulesetSetting.JudgementIndicator, JudgementIndicatorMode.None);
                Assert.That(alignment.Current.Disabled, Is.False, "text pop-in still follows the alignment");
                config.SetValue(TypeBeatRulesetSetting.TextPopIn, false);
                Assert.That(alignment.Current.Disabled, Is.True);
                config.SetValue(TypeBeatRulesetSetting.TextPopIn, true);
                config.SetValue(TypeBeatRulesetSetting.JudgementIndicator, JudgementIndicatorMode.FadeIn);
                Assert.That(alignment.Current.Disabled, Is.False);

                Assert.That(alignment.HintText.ToString(), Does.Contain("Approach bars have their own alignment"));
                Assert.That(barAlignment.HintText.ToString(), Does.Contain("Independent of Indicator Alignment"));
                var approachBars = controls.OfType<SettingsItemV2>().Select(c => c.Control).OfType<FormCheckBox>().Single(c => c.Caption.ToString() == "Approach bars");
                Assert.That(approachBars.HintText.ToString(), Does.Not.Contain("centre of the Great window"), "the bars arrive where their own slider says");

                var duration = controls.OfType<SettingsItemV2>().Select(c => c.Control).OfType<FormSliderBar<float>>().Single(c => c.Caption.ToString() == "Indicator Lead-In");
                Assert.That(duration.Current.Value, Is.EqualTo(800f));
                duration.Current.Value = 250f;
                Assert.That(config.Get<float>(TypeBeatRulesetSetting.SyllableFadeInDuration), Is.EqualTo(250f));
                config.SetValue(TypeBeatRulesetSetting.SyllableFadeInDuration, 3000f);
                Assert.That(duration.Current.Value, Is.EqualTo(2000f));
                config.SetValue(TypeBeatRulesetSetting.SyllableFadeInDuration, -1f);
                Assert.That(duration.Current.Value, Is.Zero);

                var brightness = controls.OfType<SettingsItemV2>().Select(c => c.Control).OfType<FormSliderBar<float>>().Single(c => c.Caption.ToString() == "Indicator Brightness");
                Assert.That(brightness.Current.Value, Is.EqualTo(60f));

                var popIn = controls.OfType<SettingsItemV2>().Select(c => c.Control).OfType<FormCheckBox>().Single(c => c.Caption.ToString() == "Text pop-in");
                Assert.That(popIn.Current.Value, Is.True);
                popIn.Current.Value = false;
                Assert.That(config.Get<bool>(TypeBeatRulesetSetting.TextPopIn), Is.False);
                config.SetValue(TypeBeatRulesetSetting.TextPopIn, true);
                Assert.That(popIn.Current.Value, Is.True);

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

                var gradient = controls.OfType<SettingsItemV2>().Select(c => c.Control).OfType<FormSliderBar<float>>().Single(c => c.Caption.ToString() == "Pace colour gradient");
                var bars = controls.OfType<SettingsItemV2>().Select(c => c.Control).OfType<FormCheckBox>().Single(c => c.Caption.ToString() == "Approach bars");
                Assert.That(bars.Current.Value, Is.False);
                Assert.That(gradient.Current.Value, Is.Zero);
                Assert.That(gradient.Current.Disabled, Is.False);
                bars.Current.Value = true;
                Assert.That(gradient.Current.Disabled, Is.True);
                bars.Current.Value = false;
                Assert.That(gradient.Current.Disabled, Is.False);
                Assert.That(gradient.LabelFormat!(0).ToString(), Is.EqualTo("0%"));
                gradient.Current.Value = 42f;
                Assert.That(config.Get<float>(TypeBeatRulesetSetting.PaceColourGradient), Is.EqualTo(42f));
                config.SetValue(TypeBeatRulesetSetting.PaceColourGradient, 1000f);
                Assert.That(gradient.Current.Value, Is.EqualTo(100f), "gradient is bounded at the top of its range");
                config.SetValue(TypeBeatRulesetSetting.PaceColourGradient, -1f);
                Assert.That(gradient.Current.Value, Is.Zero, "gradient is bounded at the bottom of its range");
                bars.Current.Value = true;
                Assert.That(gradient.Current.Value, Is.Zero, "approach bars always use sharp pace colour bands");
                Assert.That(gradient.Current.Disabled, Is.True);
                indicator.Current.Value = JudgementIndicatorMode.BottomToTopFill;
                Assert.That(gradient.Current.Disabled, Is.True, "changing the text indicator leaves bars enabled");
                bars.Current.Value = false;
                Assert.That(gradient.Current.Disabled, Is.False);
                gradient.Current.Value = 81f;
                Assert.That(config.Get<float>(TypeBeatRulesetSetting.PaceColourGradient), Is.EqualTo(81f));

            }
        }

        /// <summary>
        /// Backlog 479 moved the rest of "Visual feedback" and the "Timing tint" group here from
        /// Experimental. The four feedback rows join the end of this subsection's OWN "Visual
        /// feedback" group (one heading, not two of the same name), and the timing tint keeps its
        /// heading as the last group; its captions leave the feature's name to the heading.
        /// </summary>
        [Test]
        public void TheMovedSettingsSitUnderVisualFeedbackAndTimingTint()
        {
            var ruleset = new TypeBeatRuleset();
            var subsection = (TypeBeatSettingsSubsection)ruleset.CreateSettings()!;

            using (var config = new TypeBeatRulesetConfigManager(null, ruleset.RulesetInfo))
            {
                var controls = subsection.BuildControls(config).ToList();

                int heading(string text) => controls.FindIndex(c => c is OsuSpriteText t && t.Text.ToString() == text);

                int captioned(string caption) => controls.FindIndex(c => c is SettingsItemV2 item && item.Control.FilterTerms.Any(t => t.ToString() == caption));

                Assert.That(controls.OfType<OsuSpriteText>().Select(t => t.Text.ToString()), Is.EqualTo(new[]
                {
                    "Typing",
                    "Lyric text",
                    "Carets",
                    "Visual feedback",
                    TypeBeatSettingsSubsection.TIMING_TINT_HEADING,
                }), "a single Visual feedback heading, then the timing tint last");

                int visualFeedback = heading("Visual feedback");
                int timingTint = heading(TypeBeatSettingsSubsection.TIMING_TINT_HEADING);

                foreach (string caption in new[] { "Show sync metric", "Pace bar reveal", "Colour typed-letter bars by judgement", "Red screen edges before forced line changes" })
                    Assert.That(captioned(caption), Is.InRange(visualFeedback + 1, timingTint - 1), $"'{caption}' sits under visual feedback");

                string[] tintCaptions =
                {
                    TypeBeatSettingsSubsection.TIMING_TINT_CAPTION,
                    TypeBeatSettingsSubsection.TIMING_TINT_MIN_CAPTION,
                    TypeBeatSettingsSubsection.TIMING_TINT_MAX_CAPTION,
                    TypeBeatSettingsSubsection.TIMING_TINT_EARLY_COLOUR_CAPTION,
                    TypeBeatSettingsSubsection.TIMING_TINT_LATE_COLOUR_CAPTION,
                };

                foreach (string caption in tintCaptions)
                {
                    Assert.That(captioned(caption), Is.GreaterThan(timingTint), $"'{caption}' sits under the timing tint heading");
                    Assert.That(caption, Does.Not.StartWith("Timing tint"), "the heading names the feature, so the rows do not repeat it");
                }
            }
        }

        /// <summary>
        /// The controls that came from Experimental (backlog 479) read and write the same stored
        /// settings as before: the move is of the controls, never of the bindables.
        /// </summary>
        [Test]
        public void TheSettingsMovedFromExperimentalKeepTheirWiring()
        {
            var ruleset = new TypeBeatRuleset();
            var subsection = (TypeBeatSettingsSubsection)ruleset.CreateSettings()!;

            using (var config = new TypeBeatRulesetConfigManager(null, ruleset.RulesetInfo))
            {
                var controls = subsection.BuildControls(config);
                var checkboxes = controls.OfType<SettingsItemV2>().Select(c => c.Control).OfType<FormCheckBox>().ToArray();

                var reveal = controls.OfType<SettingsItemV2>().Select(c => c.Control).OfType<FormDropdown<PaceBarRevealMode>>().Single();
                Assert.That(reveal.Current.Value, Is.EqualTo(PaceBarRevealMode.Off));
                Assert.That(reveal.Items, Is.EqualTo(new[] { PaceBarRevealMode.Off, PaceBarRevealMode.Build, PaceBarRevealMode.ColourIn }));
                reveal.Current.Value = PaceBarRevealMode.ColourIn;
                Assert.That(config.Get<PaceBarRevealMode>(TypeBeatRulesetSetting.PaceBarReveal), Is.EqualTo(PaceBarRevealMode.ColourIn));
                config.SetValue(TypeBeatRulesetSetting.PaceBarReveal, PaceBarRevealMode.Off);
                Assert.That(reveal.Current.Value, Is.EqualTo(PaceBarRevealMode.Off));

                // The timing tint's strength range.
                var sliders = controls.OfType<SettingsItemV2>().Select(c => c.Control).OfType<FormSliderBar<float>>().ToArray();
                var min = sliders.Single(c => c.Caption.ToString() == TypeBeatSettingsSubsection.TIMING_TINT_MIN_CAPTION);
                var max = sliders.Single(c => c.Caption.ToString() == TypeBeatSettingsSubsection.TIMING_TINT_MAX_CAPTION);

                Assert.That(min.Current.Value, Is.EqualTo(33f));
                Assert.That(max.Current.Value, Is.EqualTo(100f));
                min.Current.Value = 50f;
                Assert.That(config.Get<float>(TypeBeatRulesetSetting.TimingTintMinStrength), Is.EqualTo(50f), "bound to the stored setting, not a copy");

                // The two colour rows, which edit a colour over a stored #rrggbb string in both directions.
                var swatches = controls.OfType<SettingsItemV2>().Select(c => c.Control).OfType<FormColourSwatch>().ToArray();

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
                    TypeBeatSettingsSubsection.TIMING_TINT_EARLY_COLOUR_CAPTION,
                    TypeBeatSettingsSubsection.TIMING_TINT_LATE_COLOUR_CAPTION,
                }));

                // The timing tint is its own switch, not a mode of the sync metric.
                var timingCheckbox = checkboxes.Single(c => c.Caption.ToString() == TypeBeatSettingsSubsection.TIMING_TINT_CAPTION);
                timingCheckbox.Current.Value = true;
                Assert.That(config.Get<bool>(TypeBeatRulesetSetting.ShowTimingTint), Is.True);
                Assert.That(config.Get<bool>(TypeBeatRulesetSetting.ShowSyncMetric), Is.False, "a separate switch, not a mode of the sync metric");
            }
        }

        [Test]
        public void ApproachBarsBindingSurvivesGarbageCollection()
        {
            var ruleset = new TypeBeatRuleset();

            using var config = new TypeBeatRulesetConfigManager(null, ruleset.RulesetInfo);
            var gradient = config.GetBindable<float>(TypeBeatRulesetSetting.PaceColourGradient);
            config.SetValue(TypeBeatRulesetSetting.ApproachBars, false);
            gradient.Value = 73f;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var bars = config.GetBindable<bool>(TypeBeatRulesetSetting.ApproachBars);
            bars.Value = true;
            Assert.That(gradient.Value, Is.Zero);
            Assert.That(gradient.Disabled, Is.True);

            Assert.That(config.Get<JudgementIndicatorMode>(TypeBeatRulesetSetting.JudgementIndicator), Is.EqualTo(JudgementIndicatorMode.BottomToTopFill));
            bars.Value = false;
            Assert.That(gradient.Disabled, Is.False);
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
