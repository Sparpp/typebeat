// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Backlog 174 stage 2: syllable-span judgement. Characters belong to a SYLLABLE
// (TypingLine.Syllables, built always, mode or no mode), and under TypingEngine.SyllableTiming a
// keypress on a cell of a syllable is perfectly timed anywhere inside that syllable's sung span:
// judged delta 0 in [StartTime, EndTime] (edge-inclusive), signed distance to the nearer edge
// outside it, graded through the unchanged SyncWindows ladder and stored in JudgedDelta so
// points, combo, sync and results all keep working unmodified. Flag off (the default of a bare
// engine, and the era every replay recorded before backlog 179 re-derives under) is byte-identical
// to the classic point-target rule; backlog 179 made flag ON the live rule in every build, carried
// per replay by the CONFIG frame's flags bit 2.
//
// The hand-computed deltas, points and sync figures in this fixture are CROSS-CHECKS, worked out
// independently from the window constants (Line tier: Great [-250, 400], Ok [-600, 1000],
// Meh [-1200, 2000]) and the scoring rules (base 300/150/50, multiplier 1 + min(combo, 50) / 50
// read before the increment).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using NUnit.Framework;
using typebeat.Game.IO;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class SyllableTimingTest
    {
        #region Fixture builders

        private static TimedUnit unit(string text, double start, double end, params double[] syllables)
            => new TimedUnit { Text = text, StartTime = start, EndTime = end, SyllableBoundaries = syllables };

        private static LyricLine line(string text, double start, double end, double singEnd, params TimedUnit[] units)
            => new LyricLine { RawText = text, StartTime = start, EndTime = end, SingEndTime = singEnd, Units = units };

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
        /// "open door", flat ramps: o=1000, p=1250, e=1500, n=1750, ' '=2000, d=2000, o=2250,
        /// o=2500, r=2750. Natural syllables: o|pen (split at 1) and door (one). Groups therefore
        /// span [1000, 1250], [1250, 2000] (unit end) and [2000, 3000].
        /// </summary>
        private static LyricBeatmap openDoor() => map(line("open door", 1000, 60000, 3000,
            unit("open", 1000, 2000), unit("door", 2000, 3000)));

        /// <summary>
        /// "probably" with 2 mapper boundaries at 1250 and 1500 inside [1000, 1800]. The even
        /// index spread times the 8 chars 3|3|2 across the segments (j = 0..7: 1000, 1093.75,
        /// 1187.5, 1281.25, 1375, 1468.75, 1575, 1687.5), while the syllabifier says pro|ba|bly,
        /// 3|2|3. The whole point of the item is that the mapper's times win and the syllabifier's
        /// characters win.
        /// </summary>
        private static LyricBeatmap probably() => map(line("probably", 1000, 60000, 1800,
            unit("probably", 1000, 1800, 1250, 1500)));

        /// <summary>
        /// "say wooooooords now" (backlog 178): an ordinary word, a STYLISED one, an ordinary word.
        /// Cells s0 a1 y2 _3 w4 o5..o11 r12 d13 s14 _15 n16 o17 w18, units [1000, 2000],
        /// [2000, 4000], [4000, 5000]. "say" and "now" are one syllable each; the middle token has
        /// seven consecutive o's, so the syllabifier refuses it and it gets NO groups unless the
        /// caller passes mapper boundaries for it.
        /// </summary>
        private static LyricBeatmap sayStylisedNow(params double[] stylisedBoundaries) => map(line("say wooooooords now", 1000, 60000, 5000,
            unit("say", 1000, 2000), unit("wooooooords", 2000, 4000, stylisedBoundaries), unit("now", 4000, 5000)));

        private static TypingEngine started(LyricBeatmap beatmap, bool syllableTiming)
        {
            var engine = new TypingEngine(beatmap) { SyllableTiming = syllableTiming };
            engine.Update(1000);
            Assert.AreEqual(0, engine.ActiveLineIndex);
            return engine;
        }

        private static List<CharJudgement> record(TypingEngine engine)
        {
            var judged = new List<CharJudgement>();
            engine.CharJudged += j => judged.Add(j);
            return judged;
        }

        #endregion

        #region Syllable structure

        [Test]
        public void NoSubtimingsGroupsTheCharsYouAlreadyTimed()
        {
            var tl = TypingLine.FromLyricLine(openDoor().Lines[0]);

            Assert.AreEqual(3, tl.Syllables.Count);

            // Edges are asserted against the cells' OWN targets, not recomputed values: this case
            // is exactly "group the chars you already timed", so nothing may move.
            Assert.AreEqual(new SyllableGroup(0, 1, tl.Cells[0].TargetTime, tl.Cells[1].TargetTime), tl.Syllables[0]);
            Assert.AreEqual(new SyllableGroup(1, 4, tl.Cells[1].TargetTime, 2000), tl.Syllables[1]);
            Assert.AreEqual(new SyllableGroup(5, 9, tl.Cells[5].TargetTime, 3000), tl.Syllables[2]);

            // A syllable ending a word runs to the unit's end, which is also the space cell's target.
            Assert.AreEqual(tl.Cells[4].TargetTime, tl.Syllables[1].EndTime, 1e-9);

            Assert.AreEqual(0, tl.SyllableIndexOf(0));
            Assert.AreEqual(1, tl.SyllableIndexOf(1));
            Assert.AreEqual(1, tl.SyllableIndexOf(3));
            Assert.AreEqual(-1, tl.SyllableIndexOf(4), "the inter-word space cell is in no syllable");
            Assert.AreEqual(2, tl.SyllableIndexOf(5));
            Assert.AreEqual(2, tl.SyllableIndexOf(8));
            Assert.AreEqual(-1, tl.SyllableIndexOf(-1));
            Assert.AreEqual(-1, tl.SyllableIndexOf(9));

            assertLineInvariants(tl);
            assertEveryTypeableCellIsGrouped(tl);
        }

        [Test]
        public void MapperSubtimingsTakeTheSyllabifierCharSplit()
        {
            var tl = TypingLine.FromLyricLine(probably().Lines[0]);

            // The flat targets themselves have NOT moved (pinned against the current
            // syllableCharTarget spread: stage 2 adds groups, it does not retime cells).
            double[] expectedTargets = { 1000, 1093.75, 1187.5, 1281.25, 1375, 1468.75, 1575, 1687.5 };

            for (int i = 0; i < expectedTargets.Length; i++)
                Assert.AreEqual(expectedTargets[i], tl.Cells[i].TargetTime, 1e-9, $"cell {i} target must not move");

            // 2 boundaries = 3 groups whose edges are EXACTLY the boundary times, and whose char
            // assignment is the syllabifier's pro|ba|bly (3|2|3), NOT the old even 3|3|2 spread.
            Assert.AreEqual(3, tl.Syllables.Count);
            Assert.AreEqual(new SyllableGroup(0, 3, 1000, 1250), tl.Syllables[0]);
            Assert.AreEqual(new SyllableGroup(3, 5, 1250, 1500), tl.Syllables[1]);
            Assert.AreEqual(new SyllableGroup(5, 8, 1500, 1800), tl.Syllables[2]);

            Assert.AreEqual(2, tl.SyllableIndexOf(5), "cell 5 ('b' of |bly) belongs to the THIRD syllable under the syllabifier split");

            assertLineInvariants(tl);
            assertEveryTypeableCellIsGrouped(tl);
        }

        [Test]
        public void OverForcedShortWordDegradesToFewerGroups()
        {
            // "go" cannot carry 4 syllables (3 boundaries): the syllabifier degrades to 2 groups,
            // so the first G - 1 = 1 boundary time is the interior edge and the last group runs to
            // the unit's end. The 2nd and 3rd boundary times are simply unused.
            var tl = TypingLine.FromLyricLine(map(line("go", 1000, 60000, 2000,
                unit("go", 1000, 2000, 1200, 1400, 1600))).Lines[0]);

            Assert.AreEqual(2, tl.Syllables.Count);
            Assert.AreEqual(new SyllableGroup(0, 1, 1000, 1200), tl.Syllables[0]);
            Assert.AreEqual(new SyllableGroup(1, 2, 1200, 2000), tl.Syllables[1]);

            assertLineInvariants(tl);
            assertEveryTypeableCellIsGrouped(tl);
        }

        [Test]
        public void AStylisedWordGetsNoGroupsWhileItsNeighboursKeepTheirs()
        {
            var tl = TypingLine.FromLyricLine(sayStylisedNow().Lines[0]);

            // Two groups for a nineteen-cell line: "say" and "now". The stylised token contributes
            // nothing, so the groups are not even adjacent in cell terms.
            Assert.AreEqual(2, tl.Syllables.Count);
            Assert.AreEqual(new SyllableGroup(0, 3, 1000, 2000), tl.Syllables[0]);
            Assert.AreEqual(new SyllableGroup(16, 19, 4000, 5000), tl.Syllables[1]);

            for (int i = 0; i < 3; i++)
                Assert.AreEqual(0, tl.SyllableIndexOf(i), $"cell {i} of say");

            // Every cell of the stylised word, and both spaces around it, are in no group at all.
            for (int i = 3; i <= 15; i++)
                Assert.AreEqual(-1, tl.SyllableIndexOf(i), $"cell {i} must be ungrouped");

            for (int i = 16; i <= 18; i++)
                Assert.AreEqual(1, tl.SyllableIndexOf(i), $"cell {i} of now");

            // The gap is in TIME as well as in cells: between 2000 and 4000 no group is being sung,
            // which is exactly what makes LyricStage light nothing while the word is on screen.
            Assert.IsFalse(tl.Syllables.Any(g => g.StartTime <= 3000 && 3000 <= g.EndTime),
                "nothing is sung while the stylised word is");

            assertLineInvariants(tl);
        }

        [Test]
        public void MapperSubtimingsOverrideTheStylisedGate()
        {
            // The mapper hand-authored two boundaries inside "wooooooords", so it is subtimed and
            // the gate does not apply: 174's rule is that an authored count is authoritative,
            // whatever the word looks like. The syllabifier is force-split to 3 groups,
            // w|ooooooo|rds (the added splits land on the C-to-V and V-to-C edges).
            var tl = TypingLine.FromLyricLine(sayStylisedNow(2500, 3000).Lines[0]);

            Assert.AreEqual(5, tl.Syllables.Count);
            Assert.AreEqual(new SyllableGroup(4, 5, 2000, 2500), tl.Syllables[1]);
            Assert.AreEqual(new SyllableGroup(5, 12, 2500, 3000), tl.Syllables[2]);
            Assert.AreEqual(new SyllableGroup(12, 15, 3000, 4000), tl.Syllables[3]);

            Assert.AreEqual(1, tl.SyllableIndexOf(4));
            Assert.AreEqual(2, tl.SyllableIndexOf(11));
            Assert.AreEqual(3, tl.SyllableIndexOf(14));

            assertLineInvariants(tl);
            assertEveryTypeableCellIsGrouped(tl);
        }

        #endregion

        #region Flag off: byte-identical to the classic rule

        /// <summary>
        /// Every expected value here is the CURRENT (pre-change) engine's behaviour, hand-computed
        /// from the point deltas: this test would have passed before stage 2 landed.
        /// </summary>
        [Test]
        public void FlagOffJudgesExactlyAsBeforeWithoutSubtimings()
        {
            var engine = started(openDoor(), syllableTiming: false);
            var judged = record(engine);

            Assert.IsTrue(engine.ProcessKey('o', 1000)); // delta 0      -> Great, 300 * 1.00 = 300
            Assert.IsTrue(engine.ProcessKey('p', 1900)); // delta 650    -> Ok,    150 * 1.02 = 153
            Assert.IsTrue(engine.ProcessKey('e', 1900)); // delta 400    -> Great, 300 * 1.04 = 312
            Assert.IsTrue(engine.ProcessKey('n', 2700)); // delta 950    -> Ok,    150 * 1.06 = 159
            Assert.IsTrue(engine.ProcessKey(' ', 2700)); // untimed      -> Great, 300 * 1.08 = 324
            Assert.IsTrue(engine.ProcessKey('d', 900));  // delta -1100  -> Meh,    50 * 1.10 = 55

            Assert.AreEqual(new[] { JudgementType.Great, JudgementType.Ok, JudgementType.Great, JudgementType.Ok, JudgementType.Great, JudgementType.Meh },
                judged.Select(j => j.Type).ToArray());
            Assert.AreEqual(new double[] { 0, 650, 400, 950, 0, -1100 }, judged.Select(j => j.Delta).ToArray());

            Assert.AreEqual(1303, engine.Score);
            Assert.AreEqual(6, engine.MaxCombo);
            Assert.AreEqual(6, engine.Combo);

            var results = engine.BuildResults();
            Assert.AreEqual(3, results.Counts[JudgementType.Great]);
            Assert.AreEqual(2, results.Counts[JudgementType.Ok]);
            Assert.AreEqual(1, results.Counts[JudgementType.Meh]);

            // 8 timed cells (the space is out under the default Untimed rule; 3 are still untyped
            // and contribute 0): q = 1, 1 - 650/2000, 1 - 400/2000, 1 - 950/2000, 1 - 1100/1200.
            double expectedSync = 100 * (1 + (1 - 650 / 2000.0) + (1 - 400 / 2000.0) + (1 - 950 / 2000.0) + (1 - 1100 / 1200.0)) / 8;
            Assert.AreEqual(expectedSync, results.SyncPercent, 1e-9);
        }

        /// <summary>
        /// Same guarantee over a map WITH mapper subtimings: the piecewise char spread is
        /// untouched, and flag-off judgement still reads the point targets it always read.
        /// </summary>
        [Test]
        public void FlagOffJudgesExactlyAsBeforeWithSubtimings()
        {
            var engine = started(probably(), syllableTiming: false);
            var judged = record(engine);

            Assert.IsTrue(engine.ProcessKey('p', 1000));    // delta 0      -> Great, 300
            Assert.IsTrue(engine.ProcessKey('r', 1500));    // delta 406.25 -> Ok,    153
            Assert.IsTrue(engine.ProcessKey('o', 1187.5));  // Great, 312
            Assert.IsTrue(engine.ProcessKey('b', 1281.25)); // Great, 318
            Assert.IsTrue(engine.ProcessKey('a', 1375));    // Great, 324
            Assert.IsTrue(engine.ProcessKey('b', 1468.75)); // Great, 330
            Assert.IsTrue(engine.ProcessKey('l', 1575));    // Great, 336
            Assert.IsTrue(engine.ProcessKey('y', 1687.5));  // Great, 342

            Assert.AreEqual(406.25, judged[1].Delta, 1e-9);
            Assert.AreEqual(JudgementType.Ok, judged[1].Type);
            Assert.AreEqual(7, judged.Count(j => j.Type == JudgementType.Great));

            Assert.AreEqual(2415, engine.Score);
            Assert.AreEqual(8, engine.MaxCombo);
        }

        #endregion

        #region Flag on: the syllable span is the window

        [Test]
        public void AnyCharOfASyllableIsPerfectWhileItIsSung()
        {
            var engine = started(openDoor(), syllableTiming: true);
            var judged = record(engine);

            Assert.IsTrue(engine.ProcessKey('o', 1000)); // in [1000, 1250]: delta 0, Great, 300
            // TWO DIFFERENT CHARS of the "pen" syllable pressed at the same in-span time: both
            // delta 0, both Great, where the point rule would have graded p 650 late and e 400 late.
            Assert.IsTrue(engine.ProcessKey('p', 1900)); // in [1250, 2000]: delta 0, Great, 306
            Assert.IsTrue(engine.ProcessKey('e', 1900)); // in [1250, 2000]: delta 0, Great, 312
            // Outside the span the ladder grades DISTANCE FROM THE EDGE. Cross-checks:
            // n at 2700 is 700 past the syllable's end 2000 -> Ok (400 < 700 <= 1000), 159.
            Assert.IsTrue(engine.ProcessKey('n', 2700));
            Assert.IsTrue(engine.ProcessKey(' ', 2700)); // no group; untimed space, delta 0, Great, 324
            // d at 900 is 1100 before the "door" syllable's start 2000 -> Meh (600 < 1100 <= 1200), 55.
            Assert.IsTrue(engine.ProcessKey('d', 900));

            Assert.AreEqual(new[] { JudgementType.Great, JudgementType.Great, JudgementType.Great, JudgementType.Ok, JudgementType.Great, JudgementType.Meh },
                judged.Select(j => j.Type).ToArray());
            Assert.AreEqual(new double[] { 0, 0, 0, 700, 0, -1100 }, judged.Select(j => j.Delta).ToArray());

            // The span delta is what is STORED, so every readout that re-reads JudgedDelta (sync
            // tint, sync percent, results) agrees with the judgement it was handed.
            Assert.AreEqual(0, engine.Lines[0].Cells[1].JudgedDelta!.Value, 1e-9);
            Assert.AreEqual(0, engine.Lines[0].Cells[2].JudgedDelta!.Value, 1e-9);
            Assert.AreEqual(700, engine.Lines[0].Cells[3].JudgedDelta!.Value, 1e-9);
            Assert.AreEqual(-1100, engine.Lines[0].Cells[5].JudgedDelta!.Value, 1e-9);

            Assert.AreEqual(300 + 306 + 312 + 159 + 324 + 55, engine.Score);
            Assert.AreEqual(6, engine.MaxCombo);
        }

        [Test]
        public void SpanEdgesAreInclusive()
        {
            var engine = started(openDoor(), syllableTiming: true);
            var judged = record(engine);

            Assert.IsTrue(engine.ProcessKey('o', 1250)); // exactly ON the end edge of [1000, 1250]
            Assert.IsTrue(engine.ProcessKey('p', 1250)); // exactly ON the start edge of [1250, 2000]

            Assert.AreEqual(0, judged[0].Delta, 1e-9);
            Assert.AreEqual(0, judged[1].Delta, 1e-9);
            Assert.AreEqual(JudgementType.Great, judged[0].Type);
            Assert.AreEqual(JudgementType.Great, judged[1].Type);
        }

        /// <summary>
        /// A wrong (typed-through) key on a syllable cell carries the same SPAN delta in its
        /// CharJudgement, so the stage's feedback agrees with what a correct press there would
        /// have been judged on.
        /// </summary>
        [Test]
        public void WrongCharCarriesTheSpanDelta()
        {
            var engine = started(openDoor(), syllableTiming: true);
            var judged = record(engine);

            Assert.IsTrue(engine.ProcessKey('x', 1900)); // wrong char on cell 0; span [1000, 1250], 650 past the end

            Assert.AreEqual(JudgementType.WrongChar, judged[0].Type);
            Assert.AreEqual(650, judged[0].Delta, 1e-9);
        }

        /// <summary>
        /// Under mapper subtimings a cell's flat-ramp target can sit OUTSIDE its syllable's span
        /// when the syllabifier's char split disagrees with the old even spread ("probably": cell 5
        /// is timed at 1468.75 inside segment 2 but belongs to syllable 3, [1500, 1800]). A press
        /// at the target then judges on the 31.25 ms distance to the edge, which is still
        /// comfortably Great.
        ///
        /// <para>That is an ENGINE fact and it still holds. What it no longer justifies is the
        /// generator: backlog 179 read this 31.25 ms as evidence that autoplay stays perfect with
        /// no generator change, but the distance is a FRACTION of the mapper's segment duration
        /// ("probably" is sung in 800 ms), so a slower or more heavily held word scales it straight
        /// past the Great window. Real subtimings did exactly that, autoplay scored 99.23% with 24
        /// Oks, and backlog 181 taught the generator to clamp each press into its cell's span (see
        /// <see cref="AutoplayPressesTheSpanEdgeWhereASubtimedTargetFallsOutsideIt"/> and the
        /// fixture beside it, which prices the same disagreement at 400 ms and 555.56 ms).</para>
        /// </summary>
        [Test]
        public void TargetTimePressesStayGreatWhenTargetSitsOutsideTheSpan()
        {
            var engine = started(probably(), syllableTiming: true);
            var judged = record(engine);

            foreach (var cell in engine.Lines[0].Cells)
                Assert.IsTrue(engine.ProcessKey(cell.Expected, cell.TargetTime));

            Assert.IsTrue(judged.All(j => j.Type == JudgementType.Great), "every target-time press judges Great");
            Assert.AreEqual(-31.25, judged[5].Delta, 1e-9, "cell 5's target is 31.25 ms before its syllable starts");
            Assert.AreEqual(0, judged[4].Delta, 1e-9);
        }

        [Test]
        public void SpaceCellKeepsThePointDeltaUnderTheTimedEra()
        {
            var engine = started(openDoor(), syllableTiming: true);
            engine.SpaceTiming = SpaceTimingRule.Timed;
            var judged = record(engine);

            Assert.IsTrue(engine.ProcessKey('o', 1000));
            Assert.IsTrue(engine.ProcessKey('p', 1300));
            Assert.IsTrue(engine.ProcessKey('e', 1500));
            Assert.IsTrue(engine.ProcessKey('n', 1750));
            // The space cell is in NO syllable, so with spaces back inside the timing challenge
            // (the pre-148 era, replay re-derivation only) it is judged on its classic POINT
            // delta: 2500 - 2000 = 500 -> Ok.
            Assert.IsTrue(engine.ProcessKey(' ', 2500));

            Assert.AreEqual(-1, engine.Lines[0].SyllableIndexOf(4));
            Assert.AreEqual(JudgementType.Ok, judged[4].Type);
            Assert.AreEqual(500, judged[4].Delta, 1e-9);
            Assert.AreEqual(500, engine.Lines[0].Cells[4].JudgedDelta!.Value, 1e-9);
        }

        /// <summary>
        /// The judgement half of backlog 178: an ungrouped cell falls through the existing "cell in
        /// no group" arm of <c>TypingEngine.judgedDeltaFor</c> and keeps the classic POINT delta
        /// even with the flag on, while its grouped neighbours do not. No new code path, which is
        /// the point of leaving the word ungrouped rather than special-casing the engine.
        /// </summary>
        [Test]
        public void AnUngroupedStylisedWordIsJudgedOnPointDeltasUnderTheFlag()
        {
            var engine = started(sayStylisedNow(), syllableTiming: true);
            var judged = record(engine);

            // "say" IS grouped, span [1000, 2000]: three different chars pressed at the same
            // in-span time are all delta 0, the syllable rule.
            Assert.IsTrue(engine.ProcessKey('s', 1900));
            Assert.IsTrue(engine.ProcessKey('a', 1900));
            Assert.IsTrue(engine.ProcessKey('y', 1900));
            Assert.IsTrue(engine.ProcessKey(' ', 1900)); // untimed space, delta 0

            // The stylised word is not. Its 11 chars carry the flat ramp over [2000, 4000], so
            // char j is targeted at 2000 + j * 2000 / 11. Cross-checks: 'w' (j = 0) at 2600 is 600
            // late -> Ok; the first 'o' (j = 1, target 2181.8181...) at the same 2600 is 418.1818...
            // late -> Ok as well but a DIFFERENT delta, which is the per-character rule showing.
            Assert.IsTrue(engine.ProcessKey('w', 2600));
            Assert.IsTrue(engine.ProcessKey('o', 2600));

            Assert.AreEqual(new[] { JudgementType.Great, JudgementType.Great, JudgementType.Great, JudgementType.Great, JudgementType.Ok, JudgementType.Ok },
                judged.Select(j => j.Type).ToArray());

            Assert.AreEqual(0, judged[0].Delta, 1e-9);
            Assert.AreEqual(0, judged[2].Delta, 1e-9);
            Assert.AreEqual(600, judged[4].Delta, 1e-9);
            Assert.AreEqual(600 - 2000.0 / 11, judged[5].Delta, 1e-9);

            // Had the word been grouped, both of the last two would have been delta 0 Great.
            Assert.AreNotEqual(judged[4].Delta, judged[5].Delta, "two chars of an ungrouped word cannot share a delta");

            // And the stored deltas agree, so every readout that re-reads them agrees too.
            Assert.AreEqual(600, engine.Lines[0].Cells[4].JudgedDelta!.Value, 1e-9);
            Assert.AreEqual(600 - 2000.0 / 11, engine.Lines[0].Cells[5].JudgedDelta!.Value, 1e-9);
        }

        /// <summary>
        /// Flag OFF over the same line, for completeness: the gate is a GROUPING decision, so it
        /// cannot have moved a single target time or judgement on the rule Release actually ships.
        /// </summary>
        [Test]
        public void TheStylisedGateMovesNothingUnderClassicJudgement()
        {
            var gated = TypingLine.FromLyricLine(sayStylisedNow().Lines[0]);
            var subtimed = TypingLine.FromLyricLine(sayStylisedNow(2500, 3000).Lines[0]);

            Assert.AreEqual("say wooooooords now", gated.DisplayText);

            // The un-subtimed line's targets are the flat ramp, gate or no gate.
            for (int j = 0; j < 11; j++)
                Assert.AreEqual(2000 + j * 2000.0 / 11, gated.Cells[4 + j].TargetTime, 1e-9, $"cell {4 + j}");

            // ...and the SUBTIMED one's are the piecewise spread, which the gate never reaches.
            Assert.AreNotEqual(gated.Cells[8].TargetTime, subtimed.Cells[8].TargetTime);

            var engine = started(sayStylisedNow(), syllableTiming: false);
            var judged = record(engine);

            Assert.IsTrue(engine.ProcessKey('s', 1900)); // point target 1000, delta 900 -> Ok
            Assert.IsTrue(engine.ProcessKey('a', 1900)); // point target 1333.33..., delta 566.66... -> Ok

            Assert.AreEqual(900, judged[0].Delta, 1e-9);
            Assert.AreEqual(1900 - (1000 + 1000.0 / 3), judged[1].Delta, 1e-9);
            Assert.AreEqual(new[] { JudgementType.Ok, JudgementType.Ok }, judged.Select(j => j.Type).ToArray());
        }

        #endregion

        #region Line-level invariants, incl. real maps

        private static void assertLineInvariants(TypingLine tl)
        {
            var groups = tl.Syllables;
            double prevEnd = double.NegativeInfinity;
            int prevCellEnd = 0;

            for (int g = 0; g < groups.Count; g++)
            {
                var grp = groups[g];

                Assert.Less(grp.StartCell, grp.EndCellExclusive, $"group {g} owns at least one cell");
                Assert.GreaterOrEqual(grp.StartCell, prevCellEnd, $"group {g} cell range must not overlap its predecessor");
                Assert.LessOrEqual(grp.StartTime, grp.EndTime, $"group {g} span must not be inverted");
                Assert.GreaterOrEqual(grp.StartTime, prevEnd, $"group {g} times must be monotonic across the line");

                prevEnd = grp.EndTime;
                prevCellEnd = grp.EndCellExclusive;
            }

            // Group coverage is PARTIAL since backlog 178: the cells of a token the syllabifier
            // refuses (a stylised spelling) are in no group at all. So the invariant is no longer
            // "every typeable non-space cell is grouped"; it is that a cell is either correctly
            // inside its own group's range, or part of a whole run the gate would refuse. Runs are
            // accumulated and checked at each break, which also pins that an ungrouped run is a
            // WHOLE token: grouping is all-or-nothing per token, so a half-grouped word would show
            // up here as a run the gate happily accepts.
            var ungrouped = new StringBuilder();

            for (int i = 0; i < tl.Cells.Count; i++)
            {
                var cell = tl.Cells[i];
                int s = tl.SyllableIndexOf(i);

                if (cell.IsTypeable && cell.Expected != ' ')
                {
                    if (s < 0)
                    {
                        ungrouped.Append(cell.Expected);
                        continue;
                    }

                    Assert.GreaterOrEqual(i, groups[s].StartCell, $"cell {i} inside its group's range");
                    Assert.Less(i, groups[s].EndCellExclusive, $"cell {i} inside its group's range");
                }
                else
                    Assert.AreEqual(-1, s, $"cell {i} ('{cell.Expected}') must be in no group");

                assertRefusedByTheGate(ungrouped);
            }

            assertRefusedByTheGate(ungrouped);
        }

        /// <summary>
        /// A completed run of ungrouped typeable non-space cells is only legal when the syllabifier
        /// would refuse that word outright. Clears the run.
        /// </summary>
        private static void assertRefusedByTheGate(StringBuilder run)
        {
            if (run.Length == 0)
                return;

            string word = run.ToString();
            run.Clear();

            Assert.IsFalse(Syllabifier.IsSyllabifiable(word),
                $"cell run \"{word}\" is in no syllable group, which is only allowed for a word the syllabifier refuses");
        }

        /// <summary>
        /// The STRONGER coverage claim, for the fixture lines whose every token is ordinary English:
        /// nothing is left ungrouped there. Kept separate from the general invariants so that the
        /// backlog 178 gate cannot quietly start dropping groups on normal words.
        /// </summary>
        private static void assertEveryTypeableCellIsGrouped(TypingLine tl)
        {
            for (int i = 0; i < tl.Cells.Count; i++)
            {
                var cell = tl.Cells[i];

                if (cell.IsTypeable && cell.Expected != ' ')
                    Assert.GreaterOrEqual(tl.SyllableIndexOf(i), 0, $"typeable non-space cell {i} ('{cell.Expected}') must be in a group");
            }
        }

        private static string requireOsu(string fileName)
        {
            string dir = Environment.GetEnvironmentVariable("TYPEBEAT_GAP_OSU_DIR") ?? string.Empty;

            if (string.IsNullOrEmpty(dir))
                Assert.Ignore("TYPEBEAT_GAP_OSU_DIR is not set; skipping real-map syllable pin.");

            string path = Path.Combine(dir, fileName);

            if (!File.Exists(path))
                Assert.Ignore($"Real map file not present (expected {path}); skipping pin.");

            return path;
        }

        private static IReadOnlyList<TypeBeatHitObject> realPipelineLineObjects(string osuPath)
        {
            LyricBeatmapDecoder.Register();

            typebeat.Game.Beatmaps.Beatmap decoded;

            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(File.ReadAllText(osuPath))))
            using (var reader = new LineBufferedReader(stream))
                decoded = typebeat.Game.Beatmaps.Formats.Decoder.GetDecoder<typebeat.Game.Beatmaps.Beatmap>(reader).Decode(reader);

            var converter = new TypeBeatBeatmapConverter(decoded, new TypeBeatRuleset());
            Assert.That(converter.CanConvert(), Is.True);
            var converted = converter.Convert(CancellationToken.None);

            var lineObjects = converted.HitObjects.OfType<TypeBeatHitObject>().OrderBy(h => h.LineIndex).ToList();
            Assert.That(lineObjects, Is.Not.Empty, "converted map has typebeat objects");

            for (int i = 0; i < lineObjects.Count; i++)
                lineObjects[i].LineIndex = i;

            return lineObjects;
        }

        /// <summary>
        /// Every line of a real packaged map (wii-shop carries authored subtimings at Syllable
        /// granularity, the other two have none) satisfies the structural invariants, in both the
        /// default and the Literate flattening.
        /// </summary>
        [TestCase("wii-shop.osu")]
        [TestCase("immortal-flame.osu")]
        [TestCase("neon-rain.osu")]
        public void SyllableInvariantsHoldOnRealMap(string fileName)
        {
            var lineObjects = realPipelineLineObjects(requireOsu(fileName));
            var granularity = lineObjects[0].Granularity;

            foreach (bool literate in new[] { false, true })
            {
                foreach (var lineObject in lineObjects)
                    assertLineInvariants(TypingLine.FromLyricLine(lineObject.Line, granularity, literate));
            }
        }

        /// <summary>
        /// Runs the whole autoplay path exactly the way the playfield's engine ticker does:
        /// generator to frames, then per-display-frame Updates with each due frame applied as
        /// Update(frameTime) plus the keystroke. Asserts only that the play MECHANICALLY works
        /// (every press reaches a live cell, every frame is consumed, the map finishes); what the
        /// presses are worth is left to the caller.
        ///
        /// <para>The two eras are separate parameters on purpose, so a test can mismatch them:
        /// <paramref name="generatorEra"/> is the rule the frames are aimed at,
        /// <paramref name="engineEra"/> the rule that grades them. Backlog 181 is exactly that
        /// mismatch, classic frames into a span engine, and the fixture below reproduces it.</para>
        ///
        /// <para><paramref name="charTimed"/> is backlog 209's narrowing, and it is deliberately
        /// ONE parameter feeding both sides: generator and engine have to agree about it or a
        /// stretch cell is pressed on one rule and graded on the other. It defaults to the LIVE
        /// value, so every autoplay pin below runs under the rule players actually get;
        /// <see cref="CharTimedStretchTest"/> holds the rule itself.</para>
        /// </summary>
        private static (TypingEngine engine, IReadOnlyList<TypeBeatReplayFrame> frames) autoplay(
            IReadOnlyList<TypeBeatHitObject> lineObjects, string title, bool generatorEra, bool engineEra, double windowScale = 1, bool charTimed = true)
        {
            const double frame_ms = 1000.0 / 60;

            var beatmap = new TypeBeatBeatmap();

            foreach (var lineObject in lineObjects)
                beatmap.HitObjects.Add(lineObject);

            var lyricBeatmap = new LyricBeatmap
            {
                Metadata = new LyricBeatmapMetadata { Artist = "a", Title = title, FolderPath = string.Empty, AudioFileName = "a.mp3" },
                Lines = lineObjects.Select(h => h.Line).ToList(),
                Granularity = lineObjects[0].Granularity,
            };

            var frames = new TypeBeatAutoGenerator(beatmap, syllableTiming: generatorEra, charTimedStretch: charTimed).Generate().Frames.Cast<TypeBeatReplayFrame>().ToList();
            Assert.That(frames, Is.Not.Empty);

            var engine = new TypingEngine(lyricBeatmap) { SyllableTiming = engineEra, CharTimedStretch = charTimed, WindowScale = windowScale };

            int next = 0;
            double end = lyricBeatmap.LastLineEnd + 10000;

            for (double now = 0; now <= end; now += frame_ms)
            {
                while (next < frames.Count && frames[next].Time <= now)
                {
                    var frame = frames[next];
                    engine.Update(frame.Time);

                    Assert.IsTrue(engine.ProcessKey(frame.Character, frame.Time),
                        $"'{frame.Character}' @ {frame.Time} must reach a live cell (line {engine.ActiveLineIndex}, caret {engine.CaretIndex})");

                    next++;
                }

                engine.Update(now);
            }

            Assert.AreEqual(frames.Count, next, "every generated frame must be consumed before the map ends");
            Assert.IsTrue(engine.IsFinished);

            return (engine, frames);
        }

        /// <summary>
        /// Every press of a run judged Great, nothing missed, and every delta zero to within
        /// <paramref name="tolerance"/>. The delta claim is the strong half: "inside the Great
        /// window" leaves 250 ms of room to rot in, "zero give or take the integral rounding of a
        /// fractional edge" does not.
        /// </summary>
        private static void assertPerfectRun(TypingEngine engine, int pressCount, double tolerance)
        {
            var results = engine.BuildResults();
            Assert.AreEqual(pressCount, results.Counts[JudgementType.Great], "every autoplay press judges Great");
            Assert.AreEqual(0, results.Counts[JudgementType.Ok]);
            Assert.AreEqual(0, results.Counts[JudgementType.Meh]);
            Assert.AreEqual(0, results.Counts[JudgementType.Premature]);
            Assert.AreEqual(0, results.Counts[JudgementType.Lagging]);
            Assert.AreEqual(0, engine.Lines.Sum(l => l.Cells.Count(c => c.State == CellState.Missed)));

            foreach (var l in engine.Lines)
            {
                foreach (var cell in l.Cells)
                {
                    if (cell.IsTypeable)
                        Assert.AreEqual(0, cell.JudgedDelta!.Value, tolerance, $"cell '{cell.Expected}' @ {cell.TargetTime} in \"{l.DisplayText}\"");
                }
            }
        }

        /// <summary>
        /// Autoplay perfects a real map under the era it is generated for. Since backlog 181 the
        /// claim is stronger than "every press judges Great": every press is judged on a delta of
        /// ZERO, because a grouped cell is pressed inside its own syllable's span.
        ///
        /// <para>The one millisecond of slack is the generator's two integral-time steps, both of
        /// which real (fractional) map data exercises: rounding a press to whole milliseconds can
        /// step off a fractional span edge by up to half a millisecond, and rounding a line's
        /// activation UP to the millisecond it genuinely opens (the backlog 51 fix) can push the
        /// line's first press up to one millisecond past a span that opens with it. Neither is
        /// worth fighting against a 112.5 ms Great window, and neither may grow: this pin is what
        /// says so.</para>
        /// </summary>
        [TestCase("wii-shop.osu")]
        [TestCase("immortal-flame.osu")]
        [TestCase("neon-rain.osu")]
        public void AutoplayIsAllGreatUnderSyllableTiming(string fileName)
        {
            var (engine, frames) = autoplay(realPipelineLineObjects(requireOsu(fileName)), fileName, generatorEra: true, engineEra: true);

            assertPerfectRun(engine, frames.Count, 1.0);
        }

        /// <summary>
        /// The backlog 181 fixture: "aviation seventeen", both words mapper-subtimed so the
        /// syllabifier's character split disagrees with the even-by-index target spread far enough
        /// to throw a cell clean out of its own syllable's span, once in each direction.
        ///
        /// <para>"aviation" is sung over [1000, 4000] with boundaries at 1800 and 2600, and the
        /// syllabifier cuts the forced three groups a|via|tion at "avi" / "a" / "tion", so cell 4
        /// ('t', the first char of the last group) is timed at 2200 by the even spread while its
        /// group is not sung until 2600: 400 ms EARLY, outside the 250 ms Great window and inside
        /// the 600 ms Ok one. "seventeen" is sung over [4000, 11000] with boundaries at 4500, 5000
        /// and 6000, a mapper holding the last syllable for five seconds, and the same
        /// disagreement runs the other way: cell 16 (the second 'e', last char of the "tee" group,
        /// span [5000, 6000]) is timed at 6555.56, which is 555.56 ms LATE against the 400 ms
        /// Great window.</para>
        ///
        /// <para>Two more cells fall just outside their spans the same way and still judge Great
        /// (cell 5, 100 ms early, and cell 11, 55.56 ms early), which is the backlog 179 situation
        /// that the old comment generalised from. They are pinned too: the fix has to move them as
        /// well, because "inside the Great window" is not the claim autoplay makes.</para>
        /// </summary>
        private static IReadOnlyList<TypeBeatHitObject> subtimedDisagreement()
        {
            var lyric = line("aviation seventeen", 1000, 20000, 11000,
                unit("aviation", 1000, 4000, 1800, 2600),
                unit("seventeen", 4000, 11000, 4500, 5000, 6000));

            return new[] { new TypeBeatHitObject { StartTime = lyric.StartTime, LineIndex = 0, Line = lyric, Granularity = TimingGranularity.Line } };
        }

        /// <summary>Cells of <see cref="subtimedDisagreement"/> whose target is outside their own span, and by how far.</summary>
        private static readonly (int cell, double distance)[] out_of_span = { (4, -400), (5, -100), (11, -55.555555555555557), (16, 555.55555555555557) };

        /// <summary>
        /// The fixture's structure, asserted before anything is played, so the tests below are
        /// pinned to a real disagreement rather than to whatever the syllabifier happens to do.
        /// </summary>
        [Test]
        public void TheSubtimedFixtureTimesFourCellsOutsideTheirSpans()
        {
            var tl = TypingLine.FromLyricLine(subtimedDisagreement()[0].Line);

            Assert.AreEqual(18, tl.Cells.Count);
            Assert.AreEqual("aviation seventeen", tl.DisplayText);

            // Early: 't' of "aviation", timed 400 ms before its group is sung.
            Assert.AreEqual(2200, tl.Cells[4].TargetTime, 1e-9);
            Assert.AreEqual(new SyllableGroup(4, 8, 2600, 4000), tl.Syllables[tl.SyllableIndexOf(4)]);

            // Late: second 'e' of "seventeen", timed 555.56 ms after its group has finished.
            Assert.AreEqual(6555.5555555555557, tl.Cells[16].TargetTime, 1e-6);
            Assert.AreEqual(new SyllableGroup(14, 17, 5000, 6000), tl.Syllables[tl.SyllableIndexOf(16)]);

            // Exactly those four cells are out of span, by exactly those distances; the rest are
            // timed inside their own group (the inter-word space is in no group at all).
            for (int i = 0; i < tl.Cells.Count; i++)
            {
                int s = tl.SyllableIndexOf(i);

                if (s < 0)
                {
                    Assert.AreEqual(8, i, "the inter-word space is the only ungrouped cell");
                    continue;
                }

                double target = tl.Cells[i].TargetTime;
                var group = tl.Syllables[s];

                double distance = target < group.StartTime ? target - group.StartTime
                    : target > group.EndTime ? target - group.EndTime
                    : 0;

                Assert.AreEqual(out_of_span.FirstOrDefault(o => o.cell == i).distance, distance, 1e-6, $"cell {i}");
            }
        }

        /// <summary>
        /// THE BUG (backlog 181): classic point-target frames fed to the live span engine. Backlog
        /// 179 shipped the span rule without touching the generator, on the strength of a 31.25 ms
        /// worked example (see <see cref="TargetTimePressesStayGreatWhenTargetSitsOutsideTheSpan"/>),
        /// but the gap scales with the mapper's segment durations and real subtimings blow straight
        /// past the Great window. On the user's map that cost 24 Oks and 99.23% accuracy; here it
        /// costs exactly the two presses the fixture is built to price.
        /// </summary>
        [Test]
        public void TargetPressesLoseGreatsWhereASubtimedTargetFallsOutsideItsSpan()
        {
            var (engine, frames) = autoplay(subtimedDisagreement(), "subtimed", generatorEra: false, engineEra: true);

            var results = engine.BuildResults();
            Assert.AreEqual(2, results.Counts[JudgementType.Ok], "the two out-of-span targets drop to Ok");
            Assert.AreEqual(frames.Count - 2, results.Counts[JudgementType.Great]);

            // Pressed at the rounded target, judged against the span edge it never reached.
            Assert.AreEqual(2200, frames[4].Time, 1e-9);
            Assert.AreEqual(-400, engine.Lines[0].Cells[4].JudgedDelta!.Value, 1e-9);

            Assert.AreEqual(6556, frames[16].Time, 1e-9);
            Assert.AreEqual(556, engine.Lines[0].Cells[16].JudgedDelta!.Value, 1e-9);

            // The other two out-of-span targets survive as Greats, which is precisely why the
            // 31.25 ms worked example read as reassuring: the rule only breaks once the mapper's
            // segments are long enough for the same fraction to clear the window.
            Assert.AreEqual(-100, engine.Lines[0].Cells[5].JudgedDelta!.Value, 1e-9);
            Assert.AreEqual(-56, engine.Lines[0].Cells[11].JudgedDelta!.Value, 1e-9);
        }

        /// <summary>
        /// THE FIX: told which era it is generating for, the generator clamps each grouped cell's
        /// press into that cell's syllable span, so all four out-of-span presses land on the nearer
        /// edge, nothing else moves, and the whole line judges on a delta of exactly zero.
        /// </summary>
        [Test]
        public void AutoplayPressesTheSpanEdgeWhereASubtimedTargetFallsOutsideIt()
        {
            var (engine, frames) = autoplay(subtimedDisagreement(), "subtimed", generatorEra: true, engineEra: true);

            // The early press waits for its span to open, the late one is taken before it closes.
            Assert.AreEqual(2600, frames[4].Time, 1e-9);
            Assert.AreEqual(6000, frames[16].Time, 1e-9);

            // The two Great-but-not-zero presses are pulled onto their edges too.
            Assert.AreEqual(2600, frames[5].Time, 1e-9);
            Assert.AreEqual(4500, frames[11].Time, 1e-9);

            // Every other press is unmoved: the clamp only touches a target outside its span.
            var tl = TypingLine.FromLyricLine(subtimedDisagreement()[0].Line);
            double[] targets = tl.Cells.Where(c => c.IsTypeable).Select(c => Math.Round(c.TargetTime)).ToArray();

            for (int i = 0; i < frames.Count; i++)
            {
                if (out_of_span.All(o => o.cell != i))
                    Assert.AreEqual(targets[i], frames[i].Time, 1e-9, $"frame {i} must not move");
            }

            assertPerfectRun(engine, frames.Count, 1e-9);
        }

        /// <summary>
        /// The classic arm, which is what Hard Rock plays under since backlog 180: the era flag off
        /// keeps the point-target presses byte for byte, and a classic engine at HR's halved
        /// windows still judges every one of them Great on a delta of zero (bar the integral
        /// rounding of a fractional target). Autoplay must be perfect in BOTH eras, not just the
        /// one it was last taught.
        /// </summary>
        [Test]
        public void HardRockEraKeepsTheTargetPressesAndStillPerfects()
        {
            var lineObjects = subtimedDisagreement();
            var tl = TypingLine.FromLyricLine(lineObjects[0].Line);

            double[] targets = tl.Cells.Where(c => c.IsTypeable).Select(c => Math.Round(c.TargetTime)).ToArray();

            var (engine, frames) = autoplay(lineObjects, "subtimed", generatorEra: false, engineEra: false,
                windowScale: Mods.TypeBeatModHardRock.WINDOW_SCALE);

            Assert.That(frames.Select(f => f.Time).ToArray(), Is.EqualTo(targets), "the classic era presses the point targets");

            assertPerfectRun(engine, frames.Count, 0.5);
        }

        #endregion
    }
}
