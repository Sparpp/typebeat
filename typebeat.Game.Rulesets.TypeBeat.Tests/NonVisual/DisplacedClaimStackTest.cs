// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Backlog 422: A CORRECTION REFUNDS ONLY THE RUN ITS OWN BREAK BROKE.
//
// The report: miss on an earlier line, then miss on the current line, and correcting the latter put
// the combo back to what it was before the FORMER miss. Backlog 262's fold (FoldsDisplacedClaim,
// CONFIG bit 12) moved the displaced claim's streak into the displacing break's own claim whether or
// not the older cell was ever corrected, and a cell left WRONG is not a miss at seal (backlog 124),
// so nothing ever clawed it back.
//
// The rule (TypingEngine.StacksDisplacedClaims, bit 14 of the SECOND CONFIG word): the displaced
// claim is kept below the new one. Correcting the newest cell refunds its own run; correcting an
// older one joins its run to the next newer claim; a sealed line's claims (and everything older than
// the newest of them) are dropped. Two accidents both corrected still cost nothing, in either order.
//
// Every pin is a pair: the live arm, and the bit-clear arm every stored replay re-derives under,
// which must reproduce the fold's numbers exactly.

using System;
using System.Collections.Generic;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.ControlPoints;
using typebeat.Game.Replays;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class DisplacedClaimStackTest
    {
        #region Fixture

        private static TimedUnit unit(string text, double start, double end)
            => new TimedUnit { Text = text, StartTime = start, EndTime = end };

        private static LyricLine line(string text, double start, double end, double singEnd, params TimedUnit[] units)
            => new LyricLine
            {
                RawText = text,
                StartTime = start,
                EndTime = end,
                SingEndTime = singEnd,
                Units = units,
            };

        private static LyricBeatmap map(params LyricLine[] lines) => new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata
            {
                Artist = "Test",
                Title = "Song",
                FolderPath = @"X:\nowhere",
                AudioFileName = "a.mp3",
            },
            Lines = lines,
            Granularity = TimingGranularity.Line,
        };

        /// <summary>
        /// DisplacedClaimFoldTest's own report map: "abcde fg hi" on one line that runs to 60000.
        /// Eleven cells: 0:a 1:b 2:c 3:d 4:e 5:' ' 6:f 7:g 8:' ' 9:h 10:i.
        /// </summary>
        private static LyricLine[] oneLine() =>
        [
            line("abcde fg hi", 1000, 60000, 1900,
                unit("abcde", 1000, 1500),
                unit("fg", 1500, 1700),
                unit("hi", 1700, 1900)),
        ];

        /// <summary>
        /// Two lines for the reported shape. L0 "abcd" [1000, 4000), a = 1000, b = 1500, c = 2000,
        /// d = 2500. L1 "efgh" [4000, 8000), e = 4000, f = 4500, g = 5000, h = 5500. A finished L0
        /// hands the caret to L1 when L1's entry bound opens (4000 - 1500 = 2500), so the player can
        /// be typing L1 while L0 is still open, which is the only way an L0 claim is still outstanding
        /// when an L1 break displaces it.
        /// </summary>
        private static LyricLine[] twoLines() =>
        [
            line("abcd", 1000, 4000, 3000, unit("abcd", 1000, 3000)),
            line("efgh", 4000, 8000, 6000, unit("efgh", 4000, 6000)),
        ];

        /// <summary>
        /// The live stack as far as this file is concerned: the spacebar as the word boundary, gap
        /// typos typed through, the unpinned and bounded caret, the back-dated seal, 260's passive
        /// fold and 262's fold. The one thing separating the two arms of every pin is
        /// <paramref name="stacks"/>.
        /// </summary>
        private static TypingEngine started(LyricLine[] lines, bool stacks)
        {
            var engine = new TypingEngine(map(lines))
            {
                SpaceSkipsWord = true,
                StrictSpaces = true,
                WrongInputOnWordGaps = true,
                FletcherEnabled = true,
                FlexibleLineSnap = true,
                BoundedRush = true,
                BackDatedSealBreak = true,
                LosslessSkipReclaim = true,
                FoldsDisplacedClaim = true,
                StacksDisplacedClaims = stacks,
            };

            engine.Update(1000);
            Assert.That(engine.ActiveLineIndex, Is.Zero);
            return engine;
        }

        /// <summary>One keystroke of a script: a key, a backspace (<c>'\b'</c>) or a bare clock tick (<c>'\0'</c>).</summary>
        private readonly record struct Step(double Time, char Key);

        private static Step key(char c, double t) => new Step(t, c);

        private static Step backspace(double t) => new Step(t, TypeBeatReplayFrame.BACKSPACE);

        private static Step tick(double t) => new Step(t, '\0');

        /// <summary>
        /// Play <paramref name="script"/> through a started engine, ticking the clock to each step's
        /// time first, which is what playback does per frame.
        /// </summary>
        private static (TypingEngine engine, List<int> restored) play(LyricLine[] lines, bool stacks, IEnumerable<Step> script)
        {
            var engine = started(lines, stacks);
            var restored = new List<int>();

            engine.ComboRestored += restored.Add;

            foreach (var step in script)
            {
                engine.Update(step.Time);

                if (step.Key == '\0')
                    continue;

                if (step.Key == TypeBeatReplayFrame.BACKSPACE)
                    Assert.That(engine.ProcessBackspace(), Is.True, $"backspace at {step.Time}");
                else
                    Assert.That(engine.ProcessKey(step.Key, step.Time), Is.True, $"'{step.Key}' at {step.Time}");
            }

            return (engine, restored);
        }

        /// <summary>The same script as a replay, through the headless scorer, with the era bit as the frame carries it.</summary>
        private static TypeBeatReplayAccount score(LyricLine[] lines, bool stacks, IEnumerable<Step> script)
        {
            var beatmap = OriginalTextFormatTest.BuildBeatmap(lines, TimingGranularity.Line);

            foreach (var hit in beatmap.HitObjects)
                hit.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty());

            var replay = new Replay();

            replay.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true, spaceSkipsWord: true, wrongInputOnWordGaps: true, strictSpaces: true,
                flexibleLines: true, boundedRush: true, backDatedSealBreak: true, losslessSkipReclaim: true, foldsDisplacedClaim: true));
            replay.Frames.Add(TypeBeatReplayFrame.CreateExtendedConfigFrame(0, stacksDisplacedClaims: stacks));

            foreach (var step in script)
            {
                if (step.Key != '\0')
                    replay.Frames.Add(new TypeBeatReplayFrame(step.Time, step.Key));
            }

            return TypeBeatReplayScorer.Score(beatmap, Array.Empty<Mod>(), replay, TypoRule.Deferred, ComboRestoreRule.OnFix);
        }

        #endregion

        #region The report

        /// <summary>
        /// THE REPORTED SHAPE, before the earlier line seals. a, b (a run of 2), a typo on 'c' (claim:
        /// 2), 'd' (L0 finished, a run of 1), then on L1 'e', 'f' (the run is 3) and a typo on 'g',
        /// which owns that 3 and displaces L0's claim. 'c' is never corrected.
        /// </summary>
        private static Step[] reportedShape(bool correctBeforeTheSeal)
        {
            var steps = new List<Step>
            {
                key('a', 1000), key('b', 1500), key('z', 2000), key('d', 2500),
                tick(2600), key('e', 2700), key('f', 2800), key('z', 2900),
            };

            if (correctBeforeTheSeal)
                steps.AddRange(new[] { backspace(3000), key('g', 3100), tick(4100), key('h', 5500), tick(9000) });
            else
                steps.AddRange(new[] { tick(4100), backspace(5000), key('g', 5000), key('h', 5500), tick(9000) });

            return steps.ToArray();
        }

        /// <summary>
        /// The owner's report, with the correction made both before and after L0 seals: correcting
        /// 'g' refunds the 3 its own break took, so the run stands at 4 with the fix (the value when
        /// that miss happened, plus the fix itself), not at 6 (the run as if the uncorrected 'c' had
        /// never happened). The stored arm reproduces the fold: 5 refunded, the run at 6.
        /// </summary>
        [TestCase(true)]
        [TestCase(false)]
        public void CorrectingTheLaterMissRefundsOnlyItsOwnRun(bool correctBeforeTheSeal)
        {
            var live = play(twoLines(), stacks: true, reportedShape(correctBeforeTheSeal));
            var stored = play(twoLines(), stacks: false, reportedShape(correctBeforeTheSeal));

            Assert.Multiple(() =>
            {
                foreach (var arm in new[] { live, stored })
                {
                    Assert.That(arm.engine.Lines[0].Cells[2].State, Is.EqualTo(CellState.Wrong), "'c' was never corrected");
                    Assert.That(arm.engine.Lines[1].Cells[2].State, Is.EqualTo(CellState.Correct), "'g' was");
                    Assert.That(arm.engine.BuildResults().Counts[JudgementType.Miss], Is.Zero, "nothing was missed, so no seal claws anything back");
                    Assert.That(arm.engine.Mistypes, Is.EqualTo(2));
                }

                Assert.That(live.restored, Is.EqualTo(new[] { 3 }), "the run of 3 the 'g' typo broke, and nothing owed to 'c'");
                Assert.That(live.engine.Combo, Is.EqualTo(5), "3 refunded, the fix, and 'h'");
                Assert.That(live.engine.MaxCombo, Is.EqualTo(5));

                Assert.That(stored.restored, Is.EqualTo(new[] { 5 }), "the fold: L0's 2 came back with L1's 3");
                Assert.That(stored.engine.Combo, Is.EqualTo(7));
                Assert.That(stored.engine.MaxCombo, Is.EqualTo(7));
            });
        }

        /// <summary>
        /// THE STORED ERA THROUGH THE REPLAY: the same keystrokes through
        /// <see cref="TypeBeatReplayScorer"/>, the bit carried on the extended header. With it clear
        /// the account is exactly the fold's (today's numbers for every stored row); with it set it is
        /// the live engine's. The era moves combo and therefore score, and nothing else.
        /// </summary>
        [Test]
        public void TheReplayReDerivesUnderItsOwnBit()
        {
            var script = reportedShape(correctBeforeTheSeal: false);

            var live = score(twoLines(), stacks: true, script);
            var stored = score(twoLines(), stacks: false, script);

            Assert.Multiple(() =>
            {
                Assert.That(live.UnconsumedFrames, Is.Zero);
                Assert.That(stored.UnconsumedFrames, Is.Zero);

                Assert.That(live.MaxCombo, Is.EqualTo(play(twoLines(), true, script).engine.MaxCombo), "the bit set re-derives the live engine");
                Assert.That(stored.MaxCombo, Is.EqualTo(play(twoLines(), false, script).engine.MaxCombo), "the bit clear re-derives the fold");
                Assert.That(live.MaxCombo, Is.EqualTo(5));
                Assert.That(stored.MaxCombo, Is.EqualTo(7));

                Assert.That(live.TotalScore, Is.LessThan(stored.TotalScore), "combo weights the judgements after the refund");
                Assert.That(live.Statistics, Is.EquivalentTo(stored.Statistics), "the era moves no judgement");
                Assert.That(live.Accuracy, Is.EqualTo(stored.Accuracy));
                Assert.That(live.Completion, Is.EqualTo(stored.Completion));
            });
        }

        /// <summary>
        /// The wire: bit 14 of the SECOND word, value 16384, mistaken for no other bit, and absent
        /// from every replay recorded before it (a CONFIG frame with no extended frame after it clears
        /// it even on an engine the live factory set it on, the watch path).
        /// </summary>
        [Test]
        public void TheEraIsBitFourteenOfTheSecondWord()
        {
            var beatmap = OriginalTextFormatTest.BuildBeatmap(oneLine(), TimingGranularity.Line);
            var legacy = TypeBeatReplayFrame.CreateExtendedConfigFrame(0, stacksDisplacedClaims: true).ToLegacy(beatmap);

            var decoded = new TypeBeatReplayFrame();
            decoded.FromLegacy(legacy, beatmap);

            var engine = new TypingEngine(map(oneLine())) { StacksDisplacedClaims = true };
            ReplayEngineFeed.Apply(engine, TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true, foldsDisplacedClaim: true));

            Assert.Multiple(() =>
            {
                Assert.That(legacy.MouseY, Is.EqualTo(16384f));
                Assert.That(decoded.IsConfigExtended && decoded.StacksDisplacedClaims, Is.True);
                Assert.That(decoded.BestRetypeJudgement || decoded.InputEra2 || decoded.JapaneseKanaInput, Is.False, "it is mistaken for no other bit");
                Assert.That(engine.StacksDisplacedClaims, Is.False, "a CONFIG frame alone is a replay recorded before the era");
                Assert.That(engine.FoldsDisplacedClaim, Is.True, "which re-derives with the fold");
            });

            ReplayEngineFeed.Apply(engine, decoded);
            Assert.That(engine.StacksDisplacedClaims, Is.True);
        }

        #endregion

        #region One line

        /// <summary>
        /// The same law with no seal in it at all: a typo on the head of "fg" left WRONG, the 'g', a
        /// typo on the gap, and only the gap corrected (one backspace clears a parked gap typo in
        /// place). The refund is the 1 the gap typo broke; the 6 the first typo broke stays lost, and
        /// sealing the line with 'f' still wrong takes nothing from either arm.
        /// </summary>
        [Test]
        public void AnOlderTypoLeftWrongOnTheSameLineIsNotRefunded()
        {
            var script = new[]
            {
                key('a', 1000), key('b', 1100), key('c', 1200), key('d', 1300), key('e', 1400), key(' ', 1500),
                key('z', 1500), key('g', 1600), key('z', 1700),
                backspace(1700), key(' ', 1700), key('h', 1700), key('i', 1800),
                tick(61600),
            };

            var live = play(oneLine(), stacks: true, script);
            var stored = play(oneLine(), stacks: false, script);

            Assert.Multiple(() =>
            {
                Assert.That(live.restored, Is.EqualTo(new[] { 1 }));
                Assert.That(live.engine.Combo, Is.EqualTo(4), "'g', the gap, 'h', 'i'");
                Assert.That(live.engine.MaxCombo, Is.EqualTo(6), "the clean run before the first typo");

                Assert.That(stored.restored, Is.EqualTo(new[] { 7 }));
                Assert.That(stored.engine.Combo, Is.EqualTo(10), "the fold refunded the 6 owed to a cell left wrong");
                Assert.That(stored.engine.MaxCombo, Is.EqualTo(10));

                foreach (var arm in new[] { live, stored })
                    Assert.That(arm.engine.Lines[0].Cells[6].State, Is.EqualTo(CellState.Wrong));
            });
        }

        /// <summary>
        /// BOTH CORRECTED, backlog 262's own report, unchanged: three backspaces and a perfect retype
        /// of letter, letter, space. The older cell is corrected first, so its claim joins the newer
        /// one and the gap redeems both together, exactly the fold's single restore of 7, and the run
        /// reaches the clean run's 11 under both arms.
        /// </summary>
        [Test]
        public void BothCorrectedOlderFirstIsTheFoldUnchanged()
        {
            var script = new[]
            {
                key('a', 1000), key('b', 1100), key('c', 1200), key('d', 1300), key('e', 1400), key(' ', 1500),
                key('z', 1500), key('g', 1600), key('z', 1700),
                backspace(1700), backspace(1700), backspace(1700),
                key('f', 1700), key('g', 1700), key(' ', 1700), key('h', 1700), key('i', 1800),
            };

            var live = play(oneLine(), stacks: true, script);
            var stored = play(oneLine(), stacks: false, script);

            Assert.Multiple(() =>
            {
                foreach (var arm in new[] { live, stored })
                {
                    Assert.That(arm.restored, Is.EqualTo(new[] { 7 }), "one refund, the 6 and the 1 together");
                    Assert.That(arm.engine.Combo, Is.EqualTo(11), "two accidents, both fully corrected, cost the run nothing");
                    Assert.That(arm.engine.MaxCombo, Is.EqualTo(11));
                }
            });
        }

        /// <summary>
        /// BOTH CORRECTED, the NEWER first: the gap typo fixed in place (a refund of its own 1), then
        /// three backspaces back to the head of "fg" and a retype. With the gap claim spent, the head's
        /// claim is the one on top, so the retyped 'f' refunds its 6 there and then. Both arms finish
        /// on the clean run's 11; only the order the combo comes back in differs.
        /// </summary>
        [Test]
        public void BothCorrectedNewerFirstStillCostsNothing()
        {
            var script = new[]
            {
                key('a', 1000), key('b', 1100), key('c', 1200), key('d', 1300), key('e', 1400), key(' ', 1500),
                key('z', 1500), key('g', 1600), key('z', 1700),
                backspace(1700), key(' ', 1700),
                backspace(1700), backspace(1700), backspace(1700),
                key('f', 1700), key('g', 1700), key(' ', 1700), key('h', 1700), key('i', 1800),
            };

            var live = play(oneLine(), stacks: true, script);
            var stored = play(oneLine(), stacks: false, script);

            Assert.Multiple(() =>
            {
                Assert.That(live.restored, Is.EqualTo(new[] { 1, 6 }), "each break's own run, each when its own cell is fixed");
                Assert.That(stored.restored, Is.EqualTo(new[] { 7 }), "the fold paid both on the first fix");

                foreach (var arm in new[] { live, stored })
                {
                    Assert.That(arm.engine.Combo, Is.EqualTo(11));
                    Assert.That(arm.engine.MaxCombo, Is.EqualTo(11));
                }
            });
        }

        #endregion

        #region A third break

        /// <summary>
        /// THE CHAIN, three breaks with one correct character between each pair (DisplacedClaimFoldTest's
        /// own script): a typo on 'c' (claim 2), 'd', a typo on 'e' (claim 1), the gap, a typo on 'f'
        /// (claim 1).
        /// </summary>
        private static readonly Step[] three_breaks =
        {
            key('a', 1000), key('b', 1100), key('z', 1200), key('d', 1300), key('z', 1400), key(' ', 1500), key('z', 1500),
        };

        /// <summary>
        /// All three corrected, oldest first: each correction joins its run to the next newer claim,
        /// and the last one refunds the lot, 2 + 1 + 1, which is the fold's single restore and the clean
        /// run's 11 under both arms.
        /// </summary>
        [Test]
        public void ThreeBreaksAllCorrectedChainIntoOneRefund()
        {
            var script = new List<Step>(three_breaks);

            for (int i = 0; i < 5; i++)
                script.Add(backspace(1500));

            script.AddRange(new[] { key('c', 1500), key('d', 1500), key('e', 1500), key(' ', 1500), key('f', 1500), key('g', 1600), key(' ', 1700), key('h', 1700), key('i', 1800) });

            var live = play(oneLine(), stacks: true, script);
            var stored = play(oneLine(), stacks: false, script);

            Assert.Multiple(() =>
            {
                foreach (var arm in new[] { live, stored })
                {
                    Assert.That(arm.restored, Is.EqualTo(new[] { 4 }));
                    Assert.That(arm.engine.Combo, Is.EqualTo(11));
                    Assert.That(arm.engine.MaxCombo, Is.EqualTo(11));
                }
            });
        }

        /// <summary>
        /// The newer two corrected and the OLDEST left wrong: the middle one joins the newest, the
        /// newest refunds the two of them (1 + 1), and the 2 owed to 'c' stays lost. The run finishes
        /// at 8, the clean run less 'c' itself and the 2 it broke; the stored fold finishes at 10,
        /// having refunded 'c''s 2 as well.
        /// </summary>
        [Test]
        public void ThreeBreaksWithTheOldestLeftWrongRefundOnlyTheNewerTwo()
        {
            var script = new List<Step>(three_breaks)
            {
                backspace(1500), backspace(1500), backspace(1500),
                key('e', 1500), key(' ', 1500), key('f', 1500), key('g', 1600), key(' ', 1700), key('h', 1700), key('i', 1800),
            };

            var live = play(oneLine(), stacks: true, script);
            var stored = play(oneLine(), stacks: false, script);

            Assert.Multiple(() =>
            {
                Assert.That(live.restored, Is.EqualTo(new[] { 2 }), "the middle break's 1 joined the newest's 1");
                Assert.That(live.engine.Combo, Is.EqualTo(8));

                Assert.That(stored.restored, Is.EqualTo(new[] { 4 }));
                Assert.That(stored.engine.Combo, Is.EqualTo(10));

                foreach (var arm in new[] { live, stored })
                    Assert.That(arm.engine.Lines[0].Cells[2].State, Is.EqualTo(CellState.Wrong));
            });
        }

        #endregion
    }
}
