// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Scoring;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Backlog 109: a typo is not a missed character. A MISS is a cell the line ran out of time on;
    /// a typo is a typo, the two are weighted differently, and in the default input model the player
    /// can still backspace and get the cell right. So a typed-through wrong char no longer resolves
    /// its cell at all: the cell's one osu result is DEFERRED until the play says which of the two it
    /// was.
    ///
    /// <para>This scene proves it where it actually matters, against the real
    /// <c>TypeBeatScoreProcessor</c> a <c>Player</c> caches, because the engine's own live combo is
    /// NOT what gets submitted. The trap the deferral opens is exactly there: osu's combo is
    /// maintained incrementally off judgement RESULTS, so removing the wrong char's result without
    /// replacing the break would leave <c>HighestCombo</c> counting straight on through the rest of
    /// the line after a break the engine has already taken. The break is mirrored by hand on
    /// <c>TypingEngine.Mistyped</c> instead (<c>TypeBeatPlayfield.onMistyped</c>), which is the seam
    /// a Gatekeeper-rejected key has always used.</para>
    /// </summary>
    public partial class TestSceneTypeBeatTypoDeferral : PlayerTestScene
    {
        protected override bool HasCustomSteps => true;

        protected override Ruleset CreatePlayerRuleset() => new TypeBeatRuleset();

        // The submitted numbers are the subject here, so the play must carry no mods at all: the
        // base would otherwise auto-append NoFail.
        protected override bool AllowFail => true;

        private TypeBeatPlayfield playfield => (TypeBeatPlayfield)Player.DrawableRuleset.Playfield;
        private TypingEngine engine => playfield.Engine;

        private IReadOnlyDictionary<HitResult, int> statistics => Player.ScoreProcessor.Statistics;

        /// <summary>
        /// Line 0 is a single twelve-letter word over [0, 240000], so its cells sit on 0, 20000,
        /// ... 220000 and every one of them can be struck dead on its target. Twelve is the smallest
        /// count that makes the two combo readings differ by an unmistakable margin (a break at cell
        /// 2 caps the submitted combo at 9 rather than 11) and gives completion figures that land
        /// clear of a rank cutoff: 11/12 = 0.9166 is an A, 12/12 an X.
        ///
        /// <para>Line 1 exists only so sealing line 0 does not finish the play out from under the
        /// assertions.</para>
        /// </summary>
        private const string word = "abcdefghijkl";

        private const double line_zero_end = 300000;

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var first = new LyricLine
            {
                RawText = word,
                StartTime = 0,
                EndTime = line_zero_end,
                SingEndTime = 240000,
                Units = new[] { new TimedUnit { Text = word, StartTime = 0, EndTime = 240000 } },
            };

            var second = new LyricLine
            {
                RawText = "z",
                StartTime = line_zero_end,
                EndTime = 600000,
                SingEndTime = 400000,
                Units = new[] { new TimedUnit { Text = "z", StartTime = line_zero_end, EndTime = 400000 } },
            };

            var beatmap = new Beatmap
            {
                HitObjects = new List<Rulesets.Objects.HitObject>
                {
                    new TypeBeatHitObject { StartTime = 0, LineIndex = 0, Line = first, Granularity = TimingGranularity.Line },
                    new TypeBeatHitObject { StartTime = line_zero_end, LineIndex = 1, Line = second, Granularity = TimingGranularity.Line },
                },
            };

            beatmap.BeatmapInfo.Ruleset = ruleset;
            return beatmap;
        }

        private void loadPlayer()
        {
            AddStep("load player", () => LoadPlayer());
            AddUntilStep("player loaded", () => Player.IsLoaded && Player.Alpha == 1);
            AddUntilStep("line active", () => engine.ActiveLineIndex == 0);
            AddAssert("the default wrong-key model", () => engine.AllowWrongInput);
        }

        /// <summary>Type cells [from, to) of line 0 correctly, each dead on its own target.</summary>
        private void typeCorrectly(int from, int to) => AddStep($"type cells {from}..{to - 1}", () =>
        {
            for (int i = from; i < to; i++)
            {
                var cell = engine.Lines[0].Cells[i];
                engine.ProcessKey(cell.Expected, cell.TargetTime);
            }
        });

        /// <summary>'q' is in neither line, so it is reliably wrong wherever the caret is.</summary>
        private void typeTypoOnCell(int index) =>
            AddStep($"type a wrong char onto cell {index}", () => engine.ProcessKey('q', engine.Lines[0].Cells[index].TargetTime));

        private void sealLineZero() => AddStep("run line 0 out of time", () => engine.Update(line_zero_end + 1));

        /// <summary>
        /// The typo left uncorrected. It costs a mistype at the keypress and a miss at the seal, and
        /// the two land at the right MOMENTS: the combo break with the keypress, the cell's Miss with
        /// the seal. That the play ends up paying for both is the deliberate trade of this design
        /// (see the mod docs): it keeps the judgement count equal to the real cell count, so accuracy,
        /// the combo ratio and the pp length term stay honest.
        /// </summary>
        [Test]
        public void TestAnUncorrectedTypoBreaksComboAtTheKeypressAndMissesAtTheSeal()
        {
            loadPlayer();

            typeCorrectly(0, 2);
            AddAssert("combo 2", () => Player.ScoreProcessor.Combo.Value == 2 && Player.ScoreProcessor.HighestCombo.Value == 2);

            typeTypoOnCell(2);
            AddAssert("the submitted combo broke on the keypress", () => Player.ScoreProcessor.Combo.Value == 0);
            AddAssert("...with no result behind it", () =>
                Player.ScoreProcessor.JudgedHits == 2
                && statistics.GetValueOrDefault(HitResult.Miss) == 0
                && statistics.GetValueOrDefault(HitResult.ComboBreak) == 1);

            typeCorrectly(3, 12);

            // THE assertion this whole scene exists for. Nine cells after the typo, so the submitted
            // max_combo is 9. Had the break not been mirrored by hand it would have run 1..11 through
            // a break the engine had already taken, and the play would submit 11.
            AddAssert("submitted max_combo counts from the typo", () => Player.ScoreProcessor.HighestCombo.Value == 9);
            AddAssert("the engine's own live combo agrees", () => engine.MaxCombo == 9);

            sealLineZero();

            AddUntilStep("the abandoned cell takes its miss at the seal", () =>
                statistics.GetValueOrDefault(HitResult.Miss) == 1 && statistics.GetValueOrDefault(HitResult.Great) == 11);
            AddAssert("the engine counts the same miss", () =>
                engine.BuildResults().Counts[JudgementType.Miss] == 1
                && engine.Lines[0].Cells[2].State == CellState.Wrong);
            AddAssert("max_combo did not move at the seal", () => Player.ScoreProcessor.HighestCombo.Value == 9);

            AddAssert("completion and rank cost exactly one cell", () =>
                TypeBeatScoreProcessor.ComputeCompletion(statistics) == 11 / 12.0
                && Player.ScoreProcessor.Rank.Value == ScoreRank.A);
        }

        /// <summary>
        /// The same typo, fixed. This is the point of the change: backspace and type it right and the
        /// cell genuinely recovers, judged Great by its own window, so completion and rank recover
        /// with it. Before backlog 109 the cell had already spent its result on a Miss the moment the
        /// wrong key landed, and <c>ApplyEngineResult</c> drops every later result, so the fix went
        /// green on screen while the statistics kept the miss for ever.
        /// </summary>
        [Test]
        public void TestAFixedTypoRecoversTheCellCompletionAndRank()
        {
            loadPlayer();

            typeCorrectly(0, 2);
            typeTypoOnCell(2);

            AddStep("backspace and type it right", () =>
            {
                engine.ProcessBackspace();
                var cell = engine.Lines[0].Cells[2];
                engine.ProcessKey(cell.Expected, cell.TargetTime);
            });

            AddAssert("the fix is the cell's first and only result", () =>
                Player.ScoreProcessor.JudgedHits == 3
                && statistics.GetValueOrDefault(HitResult.Great) == 3
                && statistics.GetValueOrDefault(HitResult.Miss) == 0);

            typeCorrectly(3, 12);
            sealLineZero();

            AddUntilStep("every cell is a Great", () =>
                statistics.GetValueOrDefault(HitResult.Great) == 12 && statistics.GetValueOrDefault(HitResult.Miss) == 0);

            AddAssert("completion is whole and the rank is an X", () =>
                TypeBeatScoreProcessor.ComputeCompletion(statistics) == 1
                && Player.ScoreProcessor.Rank.Value == ScoreRank.X);

            // The typo is still on the record, and still cost the combo it broke: the recovery is of
            // the CELL, not of the mistake.
            AddAssert("the mistype and its combo break survive the fix", () =>
                statistics.GetValueOrDefault(HitResult.ComboBreak) == 1
                && Player.ScoreProcessor.HighestCombo.Value == 10);
        }
    }
}
