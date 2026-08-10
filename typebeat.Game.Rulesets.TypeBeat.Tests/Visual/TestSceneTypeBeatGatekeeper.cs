// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Gatekeeper end to end through the real mod pipeline (backlog 107), and the SUDDEN DEATH
    /// question the default flip raises.
    ///
    /// <para>Sudden Death fails a play on <c>TypingEngine.WrongKeyRejected</c>, an event that after
    /// the flip fires only under Gatekeeper. The code's claim is that it does not need it in default
    /// play, because a typed-through wrong char is judged <c>WrongChar</c>, which
    /// <c>DrawableTypeBeatHitObject.toHitResult</c> maps to <c>HitResult.Miss</c>, which the
    /// INHERITED <c>ModSuddenDeath.FailCondition</c> already fires on. This scene tests that claim
    /// against the real health processor rather than trusting the comment, in both models.</para>
    /// </summary>
    public partial class TestSceneTypeBeatGatekeeper : PlayerTestScene
    {
        protected override bool HasCustomSteps => true;

        protected override Ruleset CreatePlayerRuleset() => new TypeBeatRuleset();

        // These tests are about failing, so the base must not auto-append NoFail.
        protected override bool AllowFail => true;

        private TypeBeatPlayfield playfield => (TypeBeatPlayfield)Player.DrawableRuleset.Playfield;
        private TypingEngine engine => playfield.Engine;

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var line = new LyricLine
            {
                RawText = "ab",
                StartTime = 0,
                EndTime = 600000,
                SingEndTime = 300000,
                Units = new[] { new TimedUnit { Text = "ab", StartTime = 0, EndTime = 300000 } },
            };

            var beatmap = new Beatmap
            {
                HitObjects = new List<Rulesets.Objects.HitObject>
                {
                    new TypeBeatHitObject
                    {
                        StartTime = 0,
                        LineIndex = 0,
                        Line = line,
                        Granularity = TimingGranularity.Line,
                    },
                },
            };

            beatmap.BeatmapInfo.Ruleset = ruleset;
            return beatmap;
        }

        private void loadWith(params Mod[] mods)
        {
            AddStep($"load player with {(mods.Length == 0 ? "no mods" : string.Join(" + ", System.Linq.Enumerable.Select(mods, m => m.Acronym)))}",
                () => LoadPlayer(mods));
            AddUntilStep("player loaded", () => Player.IsLoaded && Player.Alpha == 1);
            AddUntilStep("line active", () => engine.ActiveLineIndex == 0);
        }

        /// <summary>
        /// The mod's whole implementation, proven where it actually runs: selected in the mod list,
        /// carried through Player, and landing on the engine the playfield judges with.
        /// </summary>
        [Test]
        public void TestModPutsTheEngineInStrictMode()
        {
            loadWith(new TypeBeatModGatekeeper());

            AddAssert("engine rejects wrong input", () => !engine.AllowWrongInput);

            AddStep("press a wrong key", () => engine.ProcessKey('z', 1000));
            AddAssert("cell untouched, caret held, streak grown", () =>
                engine.Lines[0].Cells[0].State == CellState.Untyped
                && engine.CaretIndex == 0
                && engine.ConsecutiveWrongKeys == 1);
        }

        /// <summary>
        /// The same play with no mods at all: the default model types the char through, resolves the
        /// cell as a Miss, and leaves the mash streak alone.
        /// </summary>
        [Test]
        public void TestDefaultModelTypesWrongCharsThrough()
        {
            loadWith();

            AddAssert("engine allows wrong input", () => engine.AllowWrongInput);

            AddStep("press a wrong key", () => engine.ProcessKey('z', 1000));
            AddAssert("cell taken, caret advanced, no streak", () =>
                engine.Lines[0].Cells[0].State == CellState.Wrong
                && engine.Lines[0].Cells[0].TypedChar == 'z'
                && engine.CaretIndex == 1
                && engine.ConsecutiveWrongKeys == 0);

            AddAssert("the keypress is still a persisted mistype", () =>
                Player.ScoreProcessor.Statistics.GetValueOrDefault(Rulesets.Scoring.HitResult.ComboBreak) == 1);

            // The cell reached the score processor as a Miss, which is the whole reason Sudden
            // Death still works below. The line's other cell is untouched, so this is unambiguous.
            AddAssert("the cell was judged a Miss", () =>
                Player.ScoreProcessor.Statistics.GetValueOrDefault(Rulesets.Scoring.HitResult.Miss) == 1);

            // Nothing failed the play: no Sudden Death, and the mash guard is Gatekeeper-only.
            AddAssert("play still alive", () => !Player.GameplayState.HasFailed);
        }

        /// <summary>
        /// THE REGRESSION CHECK. Sudden Death alone, no Gatekeeper: the wrong key raises no
        /// <c>WrongKeyRejected</c>, so if the Miss judgement did not exist the play would sail on and
        /// Sudden Death would have silently stopped meaning anything.
        /// </summary>
        [Test]
        public void TestSuddenDeathStillFailsOnAWrongKeyWithoutGatekeeper()
        {
            loadWith(new TypeBeatModSuddenDeath());

            AddAssert("still the default model", () => engine.AllowWrongInput);

            AddStep("press a wrong key", () => engine.ProcessKey('z', 1000));

            AddUntilStep("player failed", () => Player.GameplayState.HasFailed);
        }

        /// <summary>The old route, still intact: a REJECTED key fails a Sudden Death play too.</summary>
        [Test]
        public void TestSuddenDeathFailsOnARejectedKeyUnderGatekeeper()
        {
            loadWith(new TypeBeatModSuddenDeath(), new TypeBeatModGatekeeper());

            AddAssert("strict model", () => !engine.AllowWrongInput);

            AddStep("press a wrong key", () => engine.ProcessKey('z', 1000));

            AddUntilStep("player failed", () => Player.GameplayState.HasFailed);
        }
    }
}
