// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Backlog 347: typing too far ahead no longer breaks combo, the penalty is the lowest hit tier, and
// the cap loosens from five countable characters to six. An ERA, carried by the first bit of the
// SECOND CONFIG flags word (TypeBeatReplayFrame.CONFIG_EXTENDED), so every rush-cap test that was
// written for the combo break stays where it was (FletcherEngineTest, on an engine that leaves the
// flag clear) as the pin for the era every stored replay re-derives under, and this file pins the
// live arm and the carrier that selects between them.
//
// Every expected value is hand-computed beside its assert. The TIGHT map below is dense enough that
// every press made at t = 1000 is a Great by the clock (the eighth is 70 ms early against a 150 ms
// Great window), so any Meh it produces is the cap's and not the clock's.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.ControlPoints;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Replays;
using typebeat.Game.Replays.Legacy;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Scoring;
using typebeat.Game.Scoring.Legacy;
using typebeat.Game.Tests.Beatmaps;
using Assert = NUnit.Framework.Legacy.ClassicAssert;
using CollectionAssert = NUnit.Framework.Legacy.CollectionAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class RushCapCostsAccuracyTest
    {
        #region Fixture builders

        /// <summary>Twenty letters, no spaces.</summary>
        private const string tight_chars = "abcdefghijklmnopqrst";

        /// <summary>
        /// One line, one unit [1000, 1200] over <see cref="tight_chars"/>: step 10 ms, so cell i
        /// targets 1000 + 10i. At t = 1000 the playhead has reached exactly one countable char ('a').
        /// </summary>
        private static LyricLine tightLine() => new LyricLine
        {
            RawText = tight_chars,
            StartTime = 1000,
            EndTime = 60000,
            SingEndTime = 3000,
            Units = new[] { new TimedUnit { Text = tight_chars, StartTime = 1000, EndTime = 1200 } },
        };

        private static LyricBeatmap tightMap() => new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata { Artist = "Test", Title = "Song", FolderPath = @"X:\nowhere", AudioFileName = "a.mp3" },
            Lines = new[] { tightLine() },
            Granularity = TimingGranularity.Line,
        };

        /// <summary>The unpinned caret, the line-start snap and the bounded rush, on one of the two arms.</summary>
        private static TypingEngine engine(bool rushCapCostsAccuracy) => new TypingEngine(tightMap())
        {
            FletcherEnabled = true,
            FlexibleLineSnap = true,
            BoundedRush = true,
            RushCapCostsAccuracy = rushCapCostsAccuracy,
        };

        private static TypingCell cell(TypingEngine typing, int index) => typing.Lines[0].Cells[index];

        #endregion

        #region The engine flag

        [Test]
        public void ABareEngineIsInThePre347EraAndTheCapFollowsTheFlag()
        {
            var bare = new TypingEngine(tightMap());

            Assert.IsFalse(bare.RushCapCostsAccuracy, "the default is the era every stored replay was played under");
            Assert.AreEqual(5, bare.RushCap, "...which is the combo break at five");
            Assert.AreEqual(TypingEngine.LEGACY_FLETCHER_MAX_CHARS_AHEAD, bare.RushCap);

            bare.RushCapCostsAccuracy = true;
            Assert.AreEqual(6, bare.RushCap, "the live rule loosens the cap to six");
            Assert.AreEqual(TypingEngine.FLETCHER_MAX_CHARS_AHEAD, bare.RushCap);
        }

        #endregion

        #region The live arm

        /// <summary>
        /// A run that crosses the cap keeps its combo, every over-cap press is a Meh with its TRUE
        /// delta recorded, and the first press back inside is judged on its timing again.
        /// </summary>
        [Test]
        public void ARunCrossingTheCapKeepsItsComboAndEveryOverCapCellIsMeh()
        {
            var typing = engine(rushCapCostsAccuracy: true);

            var judgements = new List<CharJudgement>();
            typing.CharJudged += judgements.Add;

            int comboBreaks = 0;
            typing.ComboBroken += () => comboBreaks++;

            typing.Update(1000);
            Assert.AreEqual(1, typing.PlayheadCountablePosition(1000));

            // Presses 1..7 at t = 1000 leave the caret 0..6 ahead: all inside the cap of six, and all
            // Greats by the clock (deltas 0, -10, ..., -60).
            for (int i = 0; i < 7; i++)
                Assert.IsTrue(typing.ProcessKey(tight_chars[i], 1000));

            Assert.AreEqual(6, typing.CharsAheadOfPlayhead(1000));
            Assert.AreEqual(7, typing.Combo);
            Assert.IsTrue(judgements.TrueForAll(j => j.Type == JudgementType.Great));

            // Press 8 ('h', target 1070) leaves the caret 7 ahead: over the cap. The clock says Great
            // (-70), the award is Meh, the delta is recorded as the clock saw it, and the combo is
            // CREDITED: 50 * (1 + 7/50) = 57 points at the pre-increment combo of 7.
            long before = typing.Score;
            Assert.IsTrue(typing.ProcessKey('h', 1000));

            Assert.AreEqual(JudgementType.Meh, judgements[^1].Type);
            Assert.AreEqual(-70, judgements[^1].Delta, "the announced delta is the press the player made");
            Assert.AreEqual(-70, cell(typing, 7).JudgedDelta);
            Assert.IsTrue(cell(typing, 7).JudgedPastRushCap);
            Assert.AreEqual(57, typing.Score - before);
            Assert.AreEqual(8, typing.Combo, "an over-cap press credits combo like any other hit");
            Assert.AreEqual(8, typing.MaxCombo);
            Assert.AreEqual(0, comboBreaks, "and breaks nothing");

            // Press 9: still out past the cap, so still a Meh (the cap re-evaluates per press).
            Assert.IsTrue(typing.ProcessKey('i', 1000));
            Assert.AreEqual(8, typing.CharsAheadOfPlayhead(1000));
            Assert.AreEqual(JudgementType.Meh, judgements[^1].Type);
            Assert.AreEqual(-80, cell(typing, 8).JudgedDelta);
            Assert.AreEqual(9, typing.Combo);

            // The song catches up: at t = 1100 it has reached 11 countable chars (targets 1000..1100)
            // and the caret sits at 9, two BEHIND. 'j' (target 1090, delta +10) is back inside the
            // cap and is judged on its timing, a Great, and it is not marked.
            typing.Update(1100);
            Assert.AreEqual(11, typing.PlayheadCountablePosition(1100));
            Assert.IsTrue(typing.ProcessKey('j', 1100));

            Assert.AreEqual(JudgementType.Great, judgements[^1].Type);
            Assert.IsFalse(cell(typing, 9).JudgedPastRushCap);
            Assert.AreEqual(10, typing.Combo);
            Assert.AreEqual(10, typing.MaxCombo);

            var counts = typing.BuildResults().Counts;
            Assert.AreEqual(8, counts[JudgementType.Great]);
            Assert.AreEqual(2, counts[JudgementType.Meh]);
            Assert.AreEqual(0, comboBreaks);
        }

        /// <summary>
        /// THE CAP IS SIX under the live rule and FIVE before it: a press six ahead is normal under
        /// the new arm and breaks the combo under the old one; a press seven ahead is the new arm's
        /// first Meh.
        /// </summary>
        [Test]
        public void TheCapIsSixUnderTheLiveRuleAndFiveInTheOldEra()
        {
            var live = engine(rushCapCostsAccuracy: true);
            var old = engine(rushCapCostsAccuracy: false);

            var liveTypes = new List<JudgementType>();
            live.CharJudged += j => liveTypes.Add(j.Type);

            int oldBreaks = 0;
            old.ComboBroken += () => oldBreaks++;

            live.Update(1000);
            old.Update(1000);

            // Six presses: 0..5 ahead. Inside both caps.
            for (int i = 0; i < 6; i++)
            {
                Assert.IsTrue(live.ProcessKey(tight_chars[i], 1000));
                Assert.IsTrue(old.ProcessKey(tight_chars[i], 1000));
            }

            Assert.AreEqual(6, live.Combo);
            Assert.AreEqual(6, old.Combo);

            // The seventh press: SIX ahead. Normal under the live cap, a break under the old one.
            Assert.IsTrue(live.ProcessKey('g', 1000));
            Assert.IsTrue(old.ProcessKey('g', 1000));

            Assert.AreEqual(6, live.CharsAheadOfPlayhead(1000));
            Assert.AreEqual(JudgementType.Great, liveTypes[^1], "six ahead is inside the live cap");
            Assert.IsFalse(cell(live, 6).JudgedPastRushCap);
            Assert.AreEqual(7, live.Combo);

            Assert.AreEqual(0, old.Combo, "six ahead breaks the pre-347 cap of five");
            Assert.AreEqual(1, oldBreaks);
            Assert.AreEqual(-60, cell(old, 6).JudgedDelta, "...and the old arm leaves the tier to the clock");
            Assert.IsFalse(cell(old, 6).JudgedPastRushCap, "the old arm never marks a cell");

            // The eighth press: SEVEN ahead, the live arm's first Meh.
            Assert.IsTrue(live.ProcessKey('h', 1000));
            Assert.AreEqual(JudgementType.Meh, liveTypes[^1]);
            Assert.AreEqual(8, live.Combo);
        }

        /// <summary>
        /// The restorable claim SURVIVES an over-cap press under the live rule (only a break discards
        /// one, and the press is no longer a break), so the fix redeems it; the old arm discards it on
        /// the over-cap press that breaks a live run. Same keystrokes, both arms.
        ///
        /// <para>'a'..'f' clean (6, combo 6), 'x' on 'g' (a typo: combo 0, claim of 6 on 'g'), 'h'
        /// (7 ahead), three backspaces back to 'g', then 'g' (6 ahead).</para>
        /// </summary>
        [TestCase(true)]
        [TestCase(false)]
        public void TheRestorableClaimSurvivesAnOverCapPressOnlyUnderTheLiveRule(bool live)
        {
            var typing = engine(live);

            int restored = 0;
            typing.ComboRestored += streak => restored += streak;

            int comboBreaks = 0;
            typing.ComboBroken += () => comboBreaks++;

            typing.Update(1000);

            for (int i = 0; i < 6; i++)
                Assert.IsTrue(typing.ProcessKey(tight_chars[i], 1000));

            Assert.AreEqual(6, typing.Combo);

            // The typo on 'g' breaks the run and leaves a claim for the streak it cost.
            Assert.IsTrue(typing.ProcessKey('x', 1000));
            Assert.AreEqual(CellState.Wrong, cell(typing, 6).State);
            Assert.AreEqual(0, typing.Combo);
            Assert.AreEqual(1, comboBreaks);

            // 'h' lands 7 ahead (the typo'd cell holds a character, so the caret counts it): over
            // BOTH caps. Live: a Meh that credits 1. Old: a break of a run of 0, which discards
            // nothing, so the claim survives this press in both eras.
            Assert.IsTrue(typing.ProcessKey('h', 1000));
            Assert.AreEqual(7, typing.CharsAheadOfPlayhead(1000));
            Assert.AreEqual(live ? 1 : 0, typing.Combo);

            // Back to the typo and fix it.
            Assert.IsTrue(typing.ProcessBackspace());
            Assert.IsTrue(typing.ProcessBackspace());
            Assert.AreEqual(6, typing.CaretIndex);

            Assert.IsTrue(typing.ProcessKey('g', 1000));
            Assert.AreEqual(6, typing.CharsAheadOfPlayhead(1000));

            // The fix redeems the claim of 6 in both eras (the restore runs before the press is
            // judged). Live: 1 + 6 = 7, then the press itself is six ahead (inside the live cap) and
            // credits 1 more: 8, no further break. Old: 0 + 6 = 6, then six ahead is OVER the old
            // cap and the press breaks the restored run on the spot (a second ComboBroken).
            Assert.AreEqual(6, restored);

            if (live)
            {
                Assert.AreEqual(8, typing.Combo);
                Assert.AreEqual(8, typing.MaxCombo);
                Assert.AreEqual(1, comboBreaks, "the typo is the only break the live run takes");
            }
            else
            {
                Assert.AreEqual(0, typing.Combo);
                Assert.AreEqual(6, typing.MaxCombo);
                Assert.AreEqual(2, comboBreaks);
            }
        }

        /// <summary>
        /// A combo run broken OUT PAST the cap: the live rule leaves an outstanding claim alone on an
        /// over-cap press of a live run, where the old rule discarded it.
        ///
        /// <para>'a'..'e' clean (combo 5), 'x' on 'f' (claim of 5 on 'f'), 'g' (6 ahead; live: in
        /// cap, combo 1; old: over the cap of 5 with combo 0, discards nothing), 'h' (7 ahead: live
        /// Meh, combo 2; old: combo 0 again). Then back to 'f' and fix it: live restores 5.</para>
        /// </summary>
        [Test]
        public void AnOverCapPressOfALiveRunKeepsTheClaim()
        {
            var typing = engine(rushCapCostsAccuracy: true);

            int restored = 0;
            typing.ComboRestored += streak => restored += streak;

            typing.Update(1000);

            for (int i = 0; i < 5; i++)
                Assert.IsTrue(typing.ProcessKey(tight_chars[i], 1000));

            Assert.IsTrue(typing.ProcessKey('x', 1000)); // typo on 'f'
            Assert.AreEqual(0, typing.Combo);

            Assert.IsTrue(typing.ProcessKey('g', 1000)); // 6 ahead: inside
            Assert.AreEqual(1, typing.Combo);

            Assert.IsTrue(typing.ProcessKey('h', 1000)); // 7 ahead, with a live run of 1: the old rule's discard case
            Assert.AreEqual(2, typing.Combo);
            Assert.IsTrue(cell(typing, 7).JudgedPastRushCap);

            for (int i = 0; i < 3; i++)
                Assert.IsTrue(typing.ProcessBackspace());

            Assert.AreEqual(5, typing.CaretIndex);
            Assert.IsTrue(typing.ProcessKey('f', 1000));

            Assert.AreEqual(5, restored, "the claim outlived the over-cap press");
            Assert.AreEqual(2 + 5 + 1, typing.Combo);
        }

        /// <summary>
        /// Earlier replays retain the original rush-cap award; new runs may improve it on re-entry.
        /// </summary>
        [TestCase(false)]
        [TestCase(true)]
        public void RetypingAnOverCapCellUsesRecordedRule(bool bestRetype)
        {
            var typing = engine(rushCapCostsAccuracy: true);
            typing.BestRetypeJudgement = bestRetype;

            var types = new List<JudgementType>();
            typing.CharJudged += j => types.Add(j.Type);

            typing.Update(1000);

            for (int i = 0; i < 8; i++)
                Assert.IsTrue(typing.ProcessKey(tight_chars[i], 1000));

            Assert.AreEqual(JudgementType.Meh, types[^1]);

            long score = typing.Score;

            // Let the song catch up so the retype is well inside the cap, then erase and retype 'h'.
            typing.Update(1100);
            Assert.IsTrue(typing.ProcessBackspace());
            Assert.IsTrue(typing.ProcessKey('h', 1100));

            Assert.AreEqual(bestRetype ? JudgementType.Great : JudgementType.Meh, types[^1], "the retype announces the award the cell holds");
            Assert.AreEqual(bestRetype ? 30 : -70, cell(typing, 7).JudgedDelta);
            if (bestRetype)
                Assert.Greater(typing.Score, score);
            else
                Assert.AreEqual(score, typing.Score, "the earlier rule keeps retypes scoring-inert");
        }

        /// <summary>An off-time press out past the cap keeps its off-time tier: the penalty never lifts a press.</summary>
        [Test]
        public void TheRushTierNeverLiftsAPress()
        {
            Assert.AreEqual(JudgementType.Meh, TypeBeatResultMapping.RushCapTier(JudgementType.Great));
            Assert.AreEqual(JudgementType.Meh, TypeBeatResultMapping.RushCapTier(JudgementType.Ok));
            Assert.AreEqual(JudgementType.Meh, TypeBeatResultMapping.RushCapTier(JudgementType.Meh));
            Assert.AreEqual(JudgementType.Premature, TypeBeatResultMapping.RushCapTier(JudgementType.Premature));
            Assert.AreEqual(JudgementType.Lagging, TypeBeatResultMapping.RushCapTier(JudgementType.Lagging));
        }

        /// <summary>The penalty belongs to the unpinned caret and, like the break before it, is lifted by <see cref="TypingEngine.RushCapExempt"/>.</summary>
        [TestCase(false, false)] // pinned: no cap at all
        [TestCase(true, true)] // unpinned but exempt (Puppeteer)
        public void NeitherThePinnedCaretNorAnExemptEngineIsPenalised(bool flexible, bool exempt)
        {
            var typing = new TypingEngine(tightMap())
            {
                FletcherEnabled = flexible,
                FlexibleLineSnap = flexible,
                BoundedRush = true,
                RushCapCostsAccuracy = true,
                RushCapExempt = exempt,
            };

            var types = new List<JudgementType>();
            typing.CharJudged += j => types.Add(j.Type);

            typing.Update(1000);

            for (int i = 0; i < 12; i++)
                Assert.IsTrue(typing.ProcessKey(tight_chars[i], 1000));

            Assert.IsTrue(types.TrueForAll(t => t == JudgementType.Great), "twelve Greats by the clock, nothing taken for the lead");
            Assert.AreEqual(12, typing.Combo);
            Assert.IsFalse(typing.Lines[0].Cells.Any(c => c.JudgedPastRushCap));
        }

        #endregion

        #region The era: the second carrier

        private static readonly char[] burst = "abcdefghij".ToCharArray();

        /// <summary>
        /// Ten presses at t = 1000 (3 and 4 out past the live and old caps respectively), then the
        /// song's catch-up and five more on time. Returned as a replay with the CONFIG header the live
        /// client writes, with or without the extended one.
        /// </summary>
        private static List<TypeBeatReplayFrame> burstFrames(bool withExtended, bool rushCapCostsAccuracy = true)
        {
            var frames = new List<TypeBeatReplayFrame>
            {
                TypeBeatReplayFrame.CreateConfigFrame(1000, allowWrongInput: true, flexibleLines: true, boundedRush: true),
            };

            if (withExtended)
                frames.Add(TypeBeatReplayFrame.CreateExtendedConfigFrame(1000, rushCapCostsAccuracy: rushCapCostsAccuracy));

            foreach (char c in burst)
                frames.Add(new TypeBeatReplayFrame(1000, c));

            for (int i = burst.Length; i < 15; i++)
                frames.Add(new TypeBeatReplayFrame(1000 + (10 * i), tight_chars[i]));

            return frames;
        }

        /// <summary>The same keystrokes driven straight into an engine on one arm.</summary>
        private static TypingEngine drivenDirectly(bool rushCapCostsAccuracy)
        {
            var typing = engine(rushCapCostsAccuracy);

            foreach (var frame in burstFrames(withExtended: false).Where(f => !f.IsConfig))
            {
                typing.Update(frame.Time);
                Assert.IsTrue(typing.ProcessKey(frame.Character, frame.Time));
            }

            return typing;
        }

        /// <summary>
        /// Frames fed through <see cref="ReplayEngineFeed.Apply"/> into an engine the LIVE factory
        /// would build (every era flag on, this one included), which is the watch path's situation:
        /// the CONFIG frame has to clear the flag for a replay that carries no extended header.
        /// </summary>
        private static TypingEngine fed(IEnumerable<TypeBeatReplayFrame> frames)
        {
            var typing = new TypingEngine(tightMap())
            {
                FletcherEnabled = true,
                FlexibleLineSnap = true,
                BoundedRush = true,
                RushCapCostsAccuracy = true,
            };

            foreach (var frame in frames)
                ReplayEngineFeed.Apply(typing, frame);

            return typing;
        }

        private static void assertSameAccount(TypingEngine expected, TypingEngine actual, string because)
        {
            Assert.AreEqual(expected.Score, actual.Score, because);
            Assert.AreEqual(expected.Combo, actual.Combo, because);
            Assert.AreEqual(expected.MaxCombo, actual.MaxCombo, because);
            Assert.AreEqual(expected.CaretIndex, actual.CaretIndex, because);
            CollectionAssert.AreEquivalent(expected.BuildResults().Counts, actual.BuildResults().Counts, because);

            for (int i = 0; i < expected.Lines[0].Cells.Count; i++)
            {
                var e = expected.Lines[0].Cells[i];
                var a = actual.Lines[0].Cells[i];

                Assert.AreEqual(e.State, a.State, $"{because}: cell {i} state");
                Assert.AreEqual(e.JudgedDelta, a.JudgedDelta, $"{because}: cell {i} delta");
                Assert.AreEqual(e.JudgedPastRushCap, a.JudgedPastRushCap, $"{because}: cell {i} mark");
            }
        }

        /// <summary>
        /// THE OLD RULE RE-DERIVES BIT FOR BIT from a replay without the second word, even on an engine
        /// that came up with the flag on; the new one from a replay that carries it. And the two
        /// accounts really do differ, or neither half would prove anything.
        /// </summary>
        [Test]
        public void EachReplayReDerivesUnderTheRuleItsHeaderNames()
        {
            var oldDirect = drivenDirectly(rushCapCostsAccuracy: false);
            var liveDirect = drivenDirectly(rushCapCostsAccuracy: true);

            var oldFed = fed(burstFrames(withExtended: false));
            var liveFed = fed(burstFrames(withExtended: true));
            var explicitlyClear = fed(burstFrames(withExtended: true, rushCapCostsAccuracy: false));

            Assert.IsFalse(oldFed.RushCapCostsAccuracy, "the CONFIG frame cleared the flag and nothing set it again");
            Assert.IsTrue(liveFed.RushCapCostsAccuracy);

            assertSameAccount(oldDirect, oldFed, "a replay with no extended header");
            assertSameAccount(oldDirect, explicitlyClear, "an extended header with the bit clear");
            assertSameAccount(liveDirect, liveFed, "a replay with the extended header");

            // Non-vacuity: ten presses at t = 1000 are 0..9 ahead, so the old arm breaks at the 7th
            // (six ahead, over five) and the live one Mehs the 8th, 9th and 10th (seven to nine ahead).
            Assert.AreEqual(15, oldDirect.BuildResults().Counts.GetValueOrDefault(JudgementType.Great), "the old arm leaves every tier to the clock");
            Assert.AreEqual(0, oldDirect.BuildResults().Counts.GetValueOrDefault(JudgementType.Meh));
            Assert.AreEqual(12, liveDirect.BuildResults().Counts.GetValueOrDefault(JudgementType.Great));
            Assert.AreEqual(3, liveDirect.BuildResults().Counts.GetValueOrDefault(JudgementType.Meh));
            Assert.AreEqual(6, oldDirect.MaxCombo, "the old arm's longest run is the six presses before the break");
            Assert.AreEqual(15, liveDirect.MaxCombo, "the live arm never breaks");
        }

        /// <summary>
        /// The whole carrier through the real <see cref="LegacyScoreEncoder"/> and
        /// <see cref="LegacyScoreDecoder"/>: the extended frame survives with its word, sits where it
        /// was written, and the decoded replay scores exactly as the original one through
        /// <see cref="TypeBeatReplayScorer"/>.
        /// </summary>
        [Test]
        public void TheExtendedHeaderRoundTripsThroughTheOsrFormat()
        {
            var map = typeBeatBeatmap();

            var replay = new Replay();
            replay.Frames.AddRange(burstFrames(withExtended: true));

            var decoded = encodeAndDecode(map, replay);
            var frames = decoded.Replay.Frames.Cast<TypeBeatReplayFrame>().ToList();

            Assert.AreEqual(replay.Frames.Count, frames.Count, "every frame survives, the extended header included");
            Assert.IsTrue(frames[0].IsConfig);
            Assert.IsTrue(frames[1].IsConfigExtended);
            Assert.IsTrue(frames[1].RushCapCostsAccuracy);
            Assert.AreEqual(frames[0].Time, frames[1].Time);
            Assert.IsFalse(frames[1].AllowWrongInput, "the second word is not read as first-word bits");
            Assert.IsFalse(frames[0].RushCapCostsAccuracy, "and the first word carries none of the second");

            var original = TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), replay, TypoRule.Deferred, ComboRestoreRule.OnFix);
            var roundTripped = TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), decoded.Replay, TypoRule.Deferred, ComboRestoreRule.OnFix);

            Assert.AreEqual(original.MaxCombo, roundTripped.MaxCombo);
            Assert.AreEqual(original.TotalScore, roundTripped.TotalScore);
            CollectionAssert.AreEquivalent(original.Statistics, roundTripped.Statistics);
            Assert.AreEqual(0, roundTripped.UnconsumedFrames);
            Assert.AreEqual(15, roundTripped.MaxCombo, "and it scores on the live rule: the burst never broke");
        }

        /// <summary>
        /// THE SCORER, both arms, from the replay alone: a replay without the extended header scores
        /// the combo break at five, one with it the Meh at six.
        /// </summary>
        [Test]
        public void TheScorerTakesTheArmFromTheReplay()
        {
            var map = typeBeatBeatmap();

            var oldReplay = new Replay();
            oldReplay.Frames.AddRange(burstFrames(withExtended: false));

            var newReplay = new Replay();
            newReplay.Frames.AddRange(burstFrames(withExtended: true));

            var old = TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), oldReplay, TypoRule.Deferred, ComboRestoreRule.OnFix);
            var live = TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), newReplay, TypoRule.Deferred, ComboRestoreRule.OnFix);

            Assert.AreEqual(0, old.UnconsumedFrames);
            Assert.AreEqual(0, live.UnconsumedFrames);

            // Old: the break lands on the 7th burst press (six ahead, over five). The ENGINE's best
            // run is the 6 before it, but the score processor's is 7: the break is mirrored by hand
            // AFTER that press's Great has already incremented osu's combo (TypeBeatPlayfield
            // .onCharJudged, and its twin in the scorer), which is exactly the max_combo the live
            // client submitted for such a run, so the era re-derives it rather than correcting it.
            // The 3 presses still over the cap earn nothing past their own increment-and-reset, and
            // the 5 on-time presses rebuild only to 5.
            Assert.AreEqual(7, old.MaxCombo);
            Assert.AreEqual(15, live.MaxCombo);

            Assert.AreEqual(0, old.Statistics.GetValueOrDefault(HitResult.Meh), "the old arm leaves every burst press to the clock");
            Assert.AreEqual(3, live.Statistics.GetValueOrDefault(HitResult.Meh), "the live arm Mehs the three past six");
        }

        /// <summary>
        /// AN OLDER CLIENT reading a new replay: it has never heard of 0x01, so its feed ignores the
        /// frame (the guard every build since backlog 241 has for a sentinel below the space). That is
        /// exactly what THIS build does with a sentinel from a later build, so the simulation drives a
        /// 0x02 frame (the next carrier's code) through the real legacy decode and the real feed and
        /// asserts it typed nothing and changed nothing.
        /// </summary>
        [Test]
        public void AnUnknownHeaderFromALaterClientIsIgnoredNotTyped()
        {
            const char later_carrier = '\u0002';

            // Decoded the way LegacyScoreDecoder.convertFrame decodes it, with a non-zero word riding in MouseY.
            var unknown = new TypeBeatReplayFrame();
            unknown.FromLegacy(new LegacyReplayFrame(1000, later_carrier, 131071, ReplayButtonState.None), new Beatmap());
            unknown.Time = 1000;

            Assert.AreEqual(later_carrier, unknown.Character);
            Assert.IsFalse(unknown.IsConfig || unknown.IsConfigExtended || unknown.IsBackspace || unknown.IsEnter);

            var withoutIt = burstFrames(withExtended: true);
            var withIt = burstFrames(withExtended: true);
            withIt.Insert(2, unknown);

            var plain = fed(withoutIt);
            var carrying = fed(withIt);

            assertSameAccount(plain, carrying, "an unknown sentinel");
            Assert.AreEqual(0, carrying.Mistypes, "it was not judged as a wrong key");
            Assert.IsFalse(carrying.Lines[0].Cells.Any(c => c.TypedChar == later_carrier), "nothing was typed");

            // And the same frame on its own, before a single keystroke: nothing moves at all.
            var bare = fed(new[] { withIt[0], withIt[1], unknown });
            Assert.AreEqual(0, bare.CaretIndex);
            Assert.IsTrue(bare.Lines[0].Cells.All(c => c.State == CellState.Untyped));
        }

        private static TypeBeatBeatmap typeBeatBeatmap()
        {
            var map = new TypeBeatBeatmap();
            map.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            map.HitObjects.Add(new TypeBeatHitObject { StartTime = 1000, LineIndex = 0, Line = tightLine(), Granularity = TimingGranularity.Line });

            foreach (var hitObject in map.HitObjects)
                hitObject.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty(), CancellationToken.None);

            return map;
        }

        private static Score encodeAndDecode(TypeBeatBeatmap map, Replay replay)
        {
            var working = new TestWorkingBeatmap(map);

            var score = new Score
            {
                ScoreInfo = new ScoreInfo
                {
                    Ruleset = map.BeatmapInfo.Ruleset,
                    BeatmapInfo = map.BeatmapInfo,
                    User = new APIUser { Username = "Rusher" },
                    Date = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero),
                },
                Replay = replay,
            };

            using (var stream = new MemoryStream())
            {
                new LegacyScoreEncoder(score, map).Encode(stream, leaveOpen: true);
                stream.Position = 0;
                return new SingleBeatmapDecoder(working).Parse(stream);
            }
        }

        private sealed class SingleBeatmapDecoder(WorkingBeatmap beatmap) : LegacyScoreDecoder
        {
            protected override Ruleset GetRuleset(int rulesetId) => new TypeBeatRuleset();

            protected override WorkingBeatmap GetBeatmap(string md5Hash) => beatmap;
        }

        #endregion
    }
}
