// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// THE SECOND INPUT ERA (TypingEngine.InputEra2): bit 1 of the SECOND CONFIG flags word, carried on
// the extended header frame (TypeBeatReplayFrame.CONFIG_EXTENDED) backlog 347 introduced. One bit for
// four rules that changed together: no rush cap, a backspace that undoes a word skip, Space to Skip
// only under wrong input, and the refined retype anchor. This file pins the CARRIER (the bit, its
// decode, the .osr round trip, the scorer taking the arm from the replay) and the rush-cap half; the
// input-layer halves are pinned beside the tests of the old rule they replace, in SpaceSkipWordTest,
// WordInputTest, LosslessSkipReclaimTest, SpaceDisciplineTest, ComboRestoreTest,
// TypeBeatModDyslexiaTest, TypeBeatHealthTest, ReplayRewindTest and TypeBeatReplayScorerTest, as the
// "...UnderInputEra2" siblings. The old-rule tests themselves are unmodified: they are the oracle
// that every stored replay still re-derives as it was played.

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
using typebeat.Game.Rulesets.Replays;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Scoring;
using typebeat.Game.Scoring.Legacy;
using typebeat.Game.Tests.Beatmaps;
using Assert = NUnit.Framework.Legacy.ClassicAssert;
using CollectionAssert = NUnit.Framework.Legacy.CollectionAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class InputEra2Test
    {
        #region Fixture builders

        /// <summary>Twenty letters, no spaces (the rush-cap fixture of <see cref="RushCapCostsAccuracyTest"/>).</summary>
        private const string tight_chars = "abcdefghijklmnopqrst";

        /// <summary>
        /// One line, one unit [1000, 1200] over <see cref="tight_chars"/>: step 10 ms, so cell i
        /// targets 1000 + 10i, and every press at t = 1000 is a Great by the clock.
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

        private static TypeBeatBeatmap typeBeatBeatmap()
        {
            var map = new TypeBeatBeatmap();
            map.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            map.HitObjects.Add(new TypeBeatHitObject { StartTime = 1000, LineIndex = 0, Line = tightLine(), Granularity = TimingGranularity.Line });

            foreach (var hitObject in map.HitObjects)
                hitObject.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty(), CancellationToken.None);

            return map;
        }

        private static readonly char[] burst = "abcdefghij".ToCharArray();

        /// <summary>
        /// Ten presses at t = 1000 (the last few far out past either cap), then five more on time.
        /// <paramref name="extended"/> is the second flags word to write, or null for a replay recorded
        /// before the extended header existed.
        /// </summary>
        private static List<TypeBeatReplayFrame> burstFrames((bool rushCapCostsAccuracy, bool inputEra2)? extended)
        {
            var frames = new List<TypeBeatReplayFrame>
            {
                TypeBeatReplayFrame.CreateConfigFrame(1000, allowWrongInput: true, flexibleLines: true, boundedRush: true),
            };

            if (extended is var (rushCapCostsAccuracy, inputEra2))
                frames.Add(TypeBeatReplayFrame.CreateExtendedConfigFrame(1000, rushCapCostsAccuracy: rushCapCostsAccuracy, inputEra2: inputEra2));

            foreach (char c in burst)
                frames.Add(new TypeBeatReplayFrame(1000, c));

            for (int i = burst.Length; i < 15; i++)
                frames.Add(new TypeBeatReplayFrame(1000 + (10 * i), tight_chars[i]));

            return frames;
        }

        /// <summary>
        /// Frames fed through <see cref="ReplayEngineFeed.Apply"/> into an engine with every era flag
        /// the live factory sets ON, which is the watch path's situation: the headers alone have to
        /// put a stored run back on the rules it was played under.
        /// </summary>
        private static TypingEngine fedOnALiveEngine(IEnumerable<TypeBeatReplayFrame> frames)
        {
            var typing = new TypingEngine(tightMap())
            {
                FletcherEnabled = true,
                FlexibleLineSnap = true,
                BoundedRush = true,
                RushCapCostsAccuracy = true,
                InputEra2 = true,
            };

            foreach (var frame in frames)
                ReplayEngineFeed.Apply(typing, frame);

            return typing;
        }

        /// <summary>The same keystrokes driven straight into a bare engine with the given second-word flags.</summary>
        private static TypingEngine drivenDirectly(bool rushCapCostsAccuracy, bool inputEra2)
        {
            var typing = new TypingEngine(tightMap())
            {
                FletcherEnabled = true,
                FlexibleLineSnap = true,
                BoundedRush = true,
                RushCapCostsAccuracy = rushCapCostsAccuracy,
                InputEra2 = inputEra2,
            };

            foreach (var frame in burstFrames(null).Where(f => !f.IsConfig))
            {
                typing.Update(frame.Time);
                Assert.IsTrue(typing.ProcessKey(frame.Character, frame.Time));
            }

            return typing;
        }

        private static void assertSameAccount(TypingEngine expected, TypingEngine actual, string because)
        {
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

        #region The flag and where it is set

        [Test]
        public void ABareEngineIsNotInTheEraAndTheLiveFactorySetsIt()
        {
            Assert.IsFalse(new TypingEngine(tightMap()).InputEra2, "the default is the era every stored replay was played under");

            var live = new DrawableTypeBeatRuleset(new TypeBeatRuleset(), typeBeatBeatmap(), Array.Empty<Mod>()).Engine;

            Assert.IsTrue(live.InputEra2, "every new live run plays in the second input era");
            Assert.IsTrue(live.RushCapCostsAccuracy, "and still records backlog 347's bit beside it");
            Assert.IsFalse(live.RushCapExempt, "the Puppeteer exemption stays a mod flag, not the era's mechanism");

            Assert.IsTrue(new DrawableTypeBeatRuleset(new TypeBeatRuleset(), typeBeatBeatmap(), new Mod[] { new TypeBeatModGatekeeper() }).Engine.InputEra2,
                "set for every mod stack, like every era flag");
        }

        [Test]
        public void TheScorerBuildsNeitherArmItself()
        {
            var map = typeBeatBeatmap();
            var engine = TypeBeatReplayScorer.CreateEngine(map, map.HitObjects.OfType<TypeBeatHitObject>().ToList(), Array.Empty<Mod>(), RateWindowRule.ScaledByRate);

            Assert.IsFalse(engine.InputEra2, "the replay's own header selects the era");
        }

        #endregion

        #region The carrier

        /// <summary>
        /// Bit 1 of the second word decodes to <see cref="TypeBeatReplayFrame.InputEra2"/> and to nothing
        /// else, bit 0 still to <see cref="TypeBeatReplayFrame.RushCapCostsAccuracy"/>, bit 2 (backlog
        /// 363) to <see cref="TypeBeatReplayFrame.AuthoredSyllablesOnly"/>, and none is read as a
        /// first-word bit. The PR fork's bare 0x01 marker (a zero word) decodes as every era off.
        /// </summary>
        [TestCase(0, false, false, false)]
        [TestCase(1, true, false, false)]
        [TestCase(2, false, true, false)]
        [TestCase(3, true, true, false)]
        [TestCase(4, false, false, true)]
        [TestCase(5, true, false, true)]
        [TestCase(6, false, true, true)]
        [TestCase(7, true, true, true)]
        public void TheSecondWordDecodesBitByBit(int word, bool rushCapCostsAccuracy, bool inputEra2, bool authoredSyllablesOnly)
        {
            var frame = new TypeBeatReplayFrame();
            frame.FromLegacy(new LegacyReplayFrame(1000, TypeBeatReplayFrame.CONFIG_EXTENDED, word, ReplayButtonState.None), new Beatmap());

            Assert.IsTrue(frame.IsConfigExtended);
            Assert.AreEqual(rushCapCostsAccuracy, frame.RushCapCostsAccuracy);
            Assert.AreEqual(inputEra2, frame.InputEra2);
            Assert.AreEqual(authoredSyllablesOnly, frame.AuthoredSyllablesOnly);
            Assert.IsFalse(frame.AllowWrongInput || frame.SpaceSkipsWord || frame.SyllableTiming, "the second word is not read as first-word bits");

            var encoded = TypeBeatReplayFrame.CreateExtendedConfigFrame(1000, rushCapCostsAccuracy, inputEra2, authoredSyllablesOnly).ToLegacy(new Beatmap());
            Assert.AreEqual(word, (int)encoded.MouseY!.Value, "and it encodes back to the same word");
            Assert.AreEqual(TypeBeatReplayFrame.CONFIG_EXTENDED, (char)(int)encoded.MouseX!.Value);
        }

        [Test]
        public void TheConfigFrameClearsTheEraAndTheExtendedFrameSetsIt()
        {
            var typing = new TypingEngine(tightMap()) { InputEra2 = true, RushCapCostsAccuracy = true };

            ReplayEngineFeed.Apply(typing, TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true));
            Assert.IsFalse(typing.InputEra2, "a CONFIG frame alone is a replay recorded before the era");
            Assert.IsFalse(typing.RushCapCostsAccuracy);

            ReplayEngineFeed.Apply(typing, TypeBeatReplayFrame.CreateExtendedConfigFrame(0, rushCapCostsAccuracy: true, inputEra2: true));
            Assert.IsTrue(typing.InputEra2);
            Assert.IsTrue(typing.RushCapCostsAccuracy);
            Assert.AreEqual(0, typing.CaretIndex, "a header types nothing");
        }

        /// <summary>
        /// The extended frame with bit 1 through the real <see cref="LegacyScoreEncoder"/> and
        /// <see cref="LegacyScoreDecoder"/>: the word survives, and the decoded replay scores exactly as
        /// the original, on the second input era's rule.
        /// </summary>
        [Test]
        public void TheEraRoundTripsThroughTheOsrFormat()
        {
            var map = typeBeatBeatmap();

            var replay = new Replay();
            replay.Frames.AddRange(burstFrames((true, true)));

            var decoded = encodeAndDecode(map, replay);
            var frames = decoded.Replay.Frames.Cast<TypeBeatReplayFrame>().ToList();

            Assert.AreEqual(replay.Frames.Count, frames.Count);
            Assert.IsTrue(frames[1].IsConfigExtended);
            Assert.IsTrue(frames[1].RushCapCostsAccuracy);
            Assert.IsTrue(frames[1].InputEra2);

            var original = TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), replay, TypoRule.Deferred, ComboRestoreRule.OnFix);
            var roundTripped = TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), decoded.Replay, TypoRule.Deferred, ComboRestoreRule.OnFix);

            Assert.AreEqual(original.MaxCombo, roundTripped.MaxCombo);
            Assert.AreEqual(original.TotalScore, roundTripped.TotalScore);
            CollectionAssert.AreEquivalent(original.Statistics, roundTripped.Statistics);
            Assert.AreEqual(0, roundTripped.UnconsumedFrames);
        }

        #endregion

        #region Rule 1: no rush cap

        /// <summary>
        /// The same burst under all three rush-cap eras, re-derived through the feed on an engine the
        /// live factory built. With no extended header it is the combo break at five (every replay
        /// before backlog 347); with bit 0 only the Meh at six (backlog 347's replays); with bit 1 the
        /// cap is gone and every press is judged by the clock alone. Each equals the same keystrokes
        /// driven straight into a bare engine on that arm, so the stored eras re-derive bit for bit.
        /// </summary>
        [Test]
        public void EachStoredEraReDerivesUnderItsOwnRushCap()
        {
            var oldFed = fedOnALiveEngine(burstFrames(null));
            var mehFed = fedOnALiveEngine(burstFrames((true, false)));
            var era2Fed = fedOnALiveEngine(burstFrames((true, true)));

            assertSameAccount(drivenDirectly(false, false), oldFed, "a replay with no extended header");
            assertSameAccount(drivenDirectly(true, false), mehFed, "an extended header with bit 0 only");
            assertSameAccount(drivenDirectly(true, true), era2Fed, "an extended header with bit 1");

            Assert.AreEqual(6, oldFed.MaxCombo, "the old arm breaks on the seventh press");
            Assert.AreEqual(3, mehFed.BuildResults().Counts.GetValueOrDefault(JudgementType.Meh), "backlog 347's arm Mehs the three past six");

            Assert.AreEqual(15, era2Fed.MaxCombo, "no cap: nothing breaks");
            Assert.AreEqual(15, era2Fed.BuildResults().Counts.GetValueOrDefault(JudgementType.Great), "and nothing is lowered to Meh");
            Assert.AreEqual(0, era2Fed.BuildResults().Counts.GetValueOrDefault(JudgementType.Meh));
            Assert.IsFalse(era2Fed.Lines[0].Cells.Any(c => c.JudgedPastRushCap), "no cell is marked as judged past a cap");
        }

        /// <summary>
        /// Bit 1 wins over a clear bit 0 as well: the era removes the cap whatever the accuracy bit says.
        /// </summary>
        [Test]
        public void TheEraRemovesTheCapWhateverBitZeroSays()
        {
            assertSameAccount(fedOnALiveEngine(burstFrames((true, true))), fedOnALiveEngine(burstFrames((false, true))), "bit 1 with and without bit 0");
        }

        /// <summary>The scorer, all three arms, from the replay alone.</summary>
        [Test]
        public void TheScorerTakesTheRushCapArmFromTheReplay()
        {
            var map = typeBeatBeatmap();

            TypeBeatReplayAccount score(List<TypeBeatReplayFrame> frames)
            {
                var replay = new Replay();
                replay.Frames.AddRange(frames);
                return TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), replay, TypoRule.Deferred, ComboRestoreRule.OnFix);
            }

            var old = score(burstFrames(null));
            var meh = score(burstFrames((true, false)));
            var era2 = score(burstFrames((true, true)));

            Assert.AreEqual(0, old.UnconsumedFrames);
            Assert.AreEqual(0, era2.UnconsumedFrames);

            // The two stored arms exactly as RushCapCostsAccuracyTest.TheScorerTakesTheArmFromTheReplay pins them.
            Assert.AreEqual(7, old.MaxCombo);
            Assert.AreEqual(15, meh.MaxCombo);
            Assert.AreEqual(3, meh.Statistics.GetValueOrDefault(HitResult.Meh));

            Assert.AreEqual(15, era2.MaxCombo);
            Assert.AreEqual(0, era2.Statistics.GetValueOrDefault(HitResult.Meh));
            Assert.AreEqual(15, era2.Statistics.GetValueOrDefault(HitResult.Great));
            Assert.Greater(era2.TotalScore, meh.TotalScore, "the era's Greats are worth more than 347's Mehs");
        }

        #endregion

        #region Rules 2 to 4, re-derived from stored frames

        /// <summary>"cat dog" (see <see cref="SpaceSkipWordTest"/>): c = 1000, gap = 3000, d = 3000.</summary>
        private static LyricBeatmap catDog() => new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata { Artist = "Test", Title = "Song", FolderPath = @"X:\nowhere", AudioFileName = "a.mp3" },
            Lines = new[]
            {
                new LyricLine
                {
                    RawText = "cat dog",
                    StartTime = 1000,
                    EndTime = 6000,
                    SingEndTime = 5000,
                    Units = new[]
                    {
                        new TimedUnit { Text = "cat", StartTime = 1000, EndTime = 3000 },
                        new TimedUnit { Text = "dog", StartTime = 3000, EndTime = 5000 },
                    },
                },
            },
            Granularity = TimingGranularity.Line,
        };

        /// <summary>
        /// A stored skip-and-reclaim, as the era before this one recorded it: 'c', a space inside the
        /// word (abandoning "at"), then ONE backspace. Fed on a live-flagged engine with no extended
        /// header, the backspace takes the gap and nothing else, exactly as it was played; with bit 1
        /// the same frame undoes the whole skip. The two differ, which is why the era needs its bit.
        /// </summary>
        [Test]
        public void AStoredBackspaceAfterASkipReDerivesOnTheRuleItWasPlayedWith()
        {
            TypingEngine feed(bool era2)
            {
                var typing = new TypingEngine(catDog()) { InputEra2 = true, RushCapCostsAccuracy = true };
                var frames = new List<TypeBeatReplayFrame> { TypeBeatReplayFrame.CreateConfigFrame(1000, allowWrongInput: true, spaceSkipsWord: true) };

                if (era2)
                    frames.Add(TypeBeatReplayFrame.CreateExtendedConfigFrame(1000, rushCapCostsAccuracy: true, inputEra2: true));

                frames.Add(new TypeBeatReplayFrame(1000, 'c'));
                frames.Add(new TypeBeatReplayFrame(2600, ' '));
                frames.Add(new TypeBeatReplayFrame(2700, TypeBeatReplayFrame.BACKSPACE));

                foreach (var frame in frames)
                    ReplayEngineFeed.Apply(typing, frame);

                return typing;
            }

            var stored = feed(era2: false);
            var live = feed(era2: true);

            var storedCells = stored.Lines[0].Cells;
            Assert.AreEqual(3, stored.CaretIndex, "the old backspace took the typed gap and stopped");
            Assert.AreEqual(CellState.Abandoned, storedCells[1].State, "the skipped word is still abandoned");
            Assert.AreEqual(CellState.Untyped, storedCells[3].State);

            var liveCells = live.Lines[0].Cells;
            Assert.AreEqual(1, live.CaretIndex, "the era's backspace undoes the skip onto the first abandoned cell");
            Assert.AreEqual(CellState.Untyped, liveCells[1].State);
            Assert.AreEqual(CellState.Correct, liveCells[0].State, "keeping the typed prefix");
        }

        /// <summary>
        /// A stored Gatekeeper run with Space to Skip on skipped the word; the second input era refuses
        /// the same space as a wrong key.
        /// </summary>
        [Test]
        public void AStoredGatekeeperSkipReDerivesAsASkip()
        {
            TypingEngine feed(bool era2)
            {
                var typing = new TypingEngine(catDog()) { InputEra2 = true };
                var frames = new List<TypeBeatReplayFrame> { TypeBeatReplayFrame.CreateConfigFrame(1000, allowWrongInput: false, spaceSkipsWord: true) };

                if (era2)
                    frames.Add(TypeBeatReplayFrame.CreateExtendedConfigFrame(1000, rushCapCostsAccuracy: true, inputEra2: true));

                frames.Add(new TypeBeatReplayFrame(1000, 'c'));
                frames.Add(new TypeBeatReplayFrame(2600, ' '));

                foreach (var frame in frames)
                    ReplayEngineFeed.Apply(typing, frame);

                return typing;
            }

            var stored = feed(era2: false);
            var live = feed(era2: true);

            Assert.AreEqual(4, stored.CaretIndex, "the stored run skipped \"at\" and typed the gap");
            Assert.AreEqual(CellState.Abandoned, stored.Lines[0].Cells[1].State);
            Assert.AreEqual(0, stored.ConsecutiveWrongKeys);

            Assert.AreEqual(1, live.CaretIndex, "the era refuses a mid-word space under Gatekeeper");
            Assert.AreEqual(CellState.Untyped, live.Lines[0].Cells[1].State);
            Assert.AreEqual(1, live.ConsecutiveWrongKeys);
        }

        /// <summary>The retype anchor follows the era, so it always agrees with the backspace it composes.</summary>
        [Test]
        public void TheRetypeAnchorFollowsTheEra()
        {
            TypingEngine skipped(bool era2)
            {
                var typing = new TypingEngine(catDog()) { SpaceSkipsWord = true, InputEra2 = era2 };
                typing.Update(1000);

                foreach (var cell in typing.Lines[0].Cells.Take(4))
                    Assert.IsTrue(typing.ProcessKey(cell.Expected, cell.TargetTime)); // "cat " on target

                Assert.IsTrue(typing.ProcessKey(' ', 3100)); // the whole of "dog" given up
                return typing;
            }

            Assert.AreEqual(3, skipped(era2: false).RetypeSelectionAnchor, "backlog 260 widened onto the gap in front of the word");
            Assert.AreEqual(4, skipped(era2: true).RetypeSelectionAnchor, "the era's backspace stops on the word's own head");

            Assert.IsFalse(skipped(era2: false).CanUndoWordSkip, "only the era's backspace undoes a skip");
            Assert.IsTrue(skipped(era2: true).CanUndoWordSkip);
        }

        #endregion
    }
}
