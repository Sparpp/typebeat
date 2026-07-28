// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Tests.Visual;
using osuTK.Input;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Song-paced held-key repeat (backlog 32) driven through the REAL input stack: physically
    /// holding a character key down must re-fire it through the whole
    /// RulesetInputManager -> playfield key handler -> engine path, and a discrete keystroke must
    /// still be exactly one keystroke. The hand-computed pacing, punishment and replay behaviour
    /// live in <c>HeldKeyRepeaterTest</c>; this scene pins the wiring.
    /// </summary>
    public partial class TestSceneTypeBeatHeldKeyRepeat : OsuManualInputManagerTestScene
    {
        /// <summary>Forty 'a' cells spread over 20s: one cell target every 500ms.</summary>
        private const string text = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        private const double cadence_ms = 500;

        private DrawableTypeBeatRuleset drawableRuleset = null!;

        protected override Ruleset CreateRuleset() => new TypeBeatRuleset();

        private TypingEngine engine => ((TypeBeatPlayfield)drawableRuleset.Playfield).Engine;

        // The same cached-per-ShortName manager instance the playfield resolves through the
        // drawable ruleset's dependencies, so SetValue here drives the live gameplay binding.
        private TypeBeatRulesetConfigManager config => (TypeBeatRulesetConfigManager)RulesetConfigs.GetConfigFor(new TypeBeatRuleset())!;

        [SetUpSteps]
        public void SetUpSteps()
        {
            // The feature ships OFF by default (backlog 57); this scene pins the ENABLED wiring, so
            // turn it on up front. The off-by-default path has its own test below.
            AddStep("enable held-key repeat", () => config.SetValue(TypeBeatRulesetSetting.HeldKeyRepeat, true));

            AddStep("create drawable ruleset", () =>
            {
                var ruleset = new TypeBeatRuleset();

                var line = new LyricLine
                {
                    RawText = text,
                    StartTime = 0,
                    EndTime = 600000,
                    SingEndTime = 20000,
                    Units = new[] { new TimedUnit { Text = text, StartTime = 0, EndTime = text.Length * cadence_ms } },
                };

                var beatmap = new Beatmap
                {
                    HitObjects = new List<Rulesets.Objects.HitObject>
                    {
                        new TypeBeatHitObject
                        {
                            StartTime = line.StartTime,
                            LineIndex = 0,
                            Line = line,
                            Granularity = TimingGranularity.Word,
                        },
                    },
                };
                beatmap.BeatmapInfo.Ruleset = ruleset.RulesetInfo;

                var playable = CreateWorkingBeatmap(beatmap).GetPlayableBeatmap(ruleset.RulesetInfo, Array.Empty<Mod>());

                Child = drawableRuleset = (DrawableTypeBeatRuleset)ruleset.CreateDrawableRulesetWith(playable);
            });

            AddUntilStep("first line active", () => engine.ActiveLineIndex == 0);
        }

        [Test]
        public void TestHoldingAKeyKeepsTypingAndReleasingStops()
        {
            AddStep("press and hold A", () => InputManager.PressKey(Key.A));

            // Nothing but song-paced repeats can move the caret past the single physical press.
            AddUntilStep("repeats keep filling cells while held", () => engine.CaretIndex >= 4);

            double releasedAt = 0;
            int caretAtRelease = 0;

            AddStep("release A", () =>
            {
                InputManager.ReleaseKey(Key.A);
                releasedAt = Clock.CurrentTime;
                caretAtRelease = engine.CaretIndex;
            });

            AddUntilStep("let two cadences pass", () => Clock.CurrentTime - releasedAt > 2 * cadence_ms);
            AddAssert("no repeat survives the release", () => engine.CaretIndex == caretAtRelease);

            AddAssert("every filled cell holds the held char", () =>
                engine.Lines[0].Cells.Take(caretAtRelease).All(c => c.State == CellState.Correct && c.TypedChar == 'a'));
        }

        [Test]
        public void TestDiscreteKeystrokeStaysOneKeystroke()
        {
            double tappedAt = 0;

            AddStep("tap A", () =>
            {
                InputManager.Key(Key.A);
                tappedAt = Clock.CurrentTime;
            });

            AddUntilStep("let two cadences pass", () => Clock.CurrentTime - tappedAt > 2 * cadence_ms);

            AddAssert("exactly one cell typed", () => engine.CaretIndex == 1);
        }

        /// <summary>
        /// The setting gates the feature through the live config binding: turned off (the shipped
        /// default), physically holding a key produces exactly the one keystroke of its initial
        /// press, as if the repeater did not exist.
        /// </summary>
        [Test]
        public void TestSettingOffMeansHoldingStaysOneKeystroke()
        {
            AddStep("turn held-key repeat off", () => config.SetValue(TypeBeatRulesetSetting.HeldKeyRepeat, false));

            double pressedAt = 0;

            AddStep("press and hold A", () =>
            {
                InputManager.PressKey(Key.A);
                pressedAt = Clock.CurrentTime;
            });

            AddUntilStep("let two cadences pass", () => Clock.CurrentTime - pressedAt > 2 * cadence_ms);

            AddAssert("exactly one cell typed", () => engine.CaretIndex == 1);
            AddAssert("the initial press judged normally", () =>
                engine.Lines[0].Cells[0].State == CellState.Correct && engine.Lines[0].Cells[0].TypedChar == 'a');

            AddStep("release A", () => InputManager.ReleaseKey(Key.A));
        }
    }
}
