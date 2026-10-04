// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// AUTHORED SYLLABLES ONLY (backlog 363): bit 2 of the SECOND CONFIG flags word. Live, a word the map
// did not subdivide is ONE syllable group over its unit; with the bit clear (every replay stored
// before it) the gameplay-time syllabifier's natural groups are re-derived. This file pins the
// carrier (the bit, Apply and ClearExtendedEras, the live factory), the stored-era re-derivation
// through the scorer, and autoplay carrying the era across the watch path's attach. The grouping
// itself is pinned in SyllableTimingTest and SyllableMarkerTest, the import pass in
// ImportSyllablesTest.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.ControlPoints;
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
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class AuthoredSyllablesEraTest
    {
        #region Fixture builders

        /// <summary>
        /// "never" on [1000, 2000] with nothing subdivided: targets 1000/1200/1400/1600/1800. The
        /// syllabifier cuts it ne|ver, so the stored era has groups [1000, 1400] and [1400, 2000]; the
        /// live grouping has one, [1000, 2000].
        /// </summary>
        private static LyricLine neverLine() => new LyricLine
        {
            RawText = "never",
            StartTime = 1000,
            EndTime = 60000,
            SingEndTime = 2000,
            Units = new[] { new TimedUnit { Text = "never", StartTime = 1000, EndTime = 2000 } },
        };

        private static LyricBeatmap lyricMap(LyricLine line) => new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata { Artist = "Test", Title = "Song", FolderPath = @"X:\nowhere", AudioFileName = "a.mp3" },
            Lines = new[] { line },
            Granularity = TimingGranularity.Word,
        };

        private static TypeBeatBeatmap typeBeatBeatmap(params LyricLine[] lines)
        {
            var map = new TypeBeatBeatmap();
            map.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;

            for (int i = 0; i < lines.Length; i++)
                map.HitObjects.Add(new TypeBeatHitObject { StartTime = lines[i].StartTime, LineIndex = i, Line = lines[i], Granularity = TimingGranularity.Word });

            foreach (var hitObject in map.HitObjects)
                hitObject.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty(), CancellationToken.None);

            return map;
        }

        /// <summary>
        /// The keystream of the measured case: a CONFIG frame with the live span rule and its two
        /// narrowings, the extended header when <paramref name="authoredSyllablesOnly"/> is not null,
        /// then all five letters at t = 1600.
        /// </summary>
        private static Replay neverReplay(bool? authoredSyllablesOnly)
        {
            var replay = new Replay();
            replay.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(1000, allowWrongInput: true, syllableTiming: true, charTimedStretch: true, firstCharTiming: true));

            if (authoredSyllablesOnly is bool authored)
                replay.Frames.Add(TypeBeatReplayFrame.CreateExtendedConfigFrame(1000, rushCapCostsAccuracy: true, inputEra2: true, authoredSyllablesOnly: authored));

            foreach (char c in "never")
                replay.Frames.Add(new TypeBeatReplayFrame(1600, c));

            return replay;
        }

        private static TypingEngine liveEngine(TypeBeatBeatmap map)
            => new DrawableTypeBeatRuleset(new TypeBeatRuleset(), map, Array.Empty<Mod>()).Engine;

        #endregion

        #region The flag and where it is set

        [Test]
        public void ABareEngineIsInTheStoredEraAndTheLiveFactorySetsIt()
        {
            Assert.IsFalse(new TypingEngine(lyricMap(neverLine())).AuthoredSyllablesOnly, "the default is the grouping every stored replay was played on");

            Assert.IsTrue(liveEngine(typeBeatBeatmap(neverLine())).AuthoredSyllablesOnly, "every new live run plays authored syllables only");
            Assert.IsTrue(new DrawableTypeBeatRuleset(new TypeBeatRuleset(), typeBeatBeatmap(neverLine()), new Mod[] { new TypeBeatModHardRock() }).Engine.AuthoredSyllablesOnly,
                "set for every mod stack, like every era flag");

            var map = typeBeatBeatmap(neverLine());
            Assert.IsFalse(TypeBeatReplayScorer.CreateEngine(map, map.HitObjects.OfType<TypeBeatHitObject>().ToList(), Array.Empty<Mod>(), RateWindowRule.ScaledByRate).AuthoredSyllablesOnly,
                "the scorer builds neither arm: the replay's own header selects it");
        }

        [Test]
        public void TheRecorderWritesTheBitTheEngineCarries()
        {
            var frame = TypeBeatReplayFrame.CreateExtendedConfigFrame(0, authoredSyllablesOnly: true);

            Assert.AreEqual(4, (int)frame.ToLegacy(new Beatmap()).MouseY!.Value, "bit 2 of the second word, and nothing else");
            Assert.AreEqual(0, (int)TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: false).ToLegacy(new Beatmap()).MouseY!.Value,
                "and no first-word bit carries it");
        }

        /// <summary>
        /// A second-word bit this build has never heard of decodes to nothing: the known bits read as
        /// written and the unknown one is not mistaken for any of them, so a later client's header
        /// replays here under the rules this build knows.
        /// </summary>
        [Test]
        public void AnUnknownSecondWordBitDecodesToNothing()
        {
            var frame = new TypeBeatReplayFrame();
            frame.FromLegacy(new LegacyReplayFrame(0, TypeBeatReplayFrame.CONFIG_EXTENDED, 256 | 4, ReplayButtonState.None), new Beatmap());

            Assert.IsTrue(frame.AuthoredSyllablesOnly);
            Assert.IsFalse(frame.RushCapCostsAccuracy);
            Assert.IsFalse(frame.InputEra2);
            Assert.IsFalse(frame.JapaneseWordTiming);
            Assert.IsFalse(frame.JapaneseInputEra2);
            Assert.AreEqual(4, (int)frame.ToLegacy(new Beatmap()).MouseY!.Value, "re-encoding writes the bits it knows");
        }

        [Test]
        public void TheConfigFrameClearsTheBitAndTheExtendedFrameSetsIt()
        {
            var typing = new TypingEngine(lyricMap(neverLine())) { AuthoredSyllablesOnly = true };
            int flips = 0;
            typing.AuthoredSyllablesOnlyChanged += () => flips++;

            ReplayEngineFeed.Apply(typing, TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true));
            Assert.IsFalse(typing.AuthoredSyllablesOnly, "a CONFIG frame alone is a replay recorded before the era");

            ReplayEngineFeed.Apply(typing, TypeBeatReplayFrame.CreateExtendedConfigFrame(0, authoredSyllablesOnly: true));
            Assert.IsTrue(typing.AuthoredSyllablesOnly);

            ReplayEngineFeed.Apply(typing, TypeBeatReplayFrame.CreateExtendedConfigFrame(0, authoredSyllablesOnly: true));
            Assert.AreEqual(2, flips, "the change event fires on a real change only");

            ReplayEngineFeed.ClearExtendedEras(typing);
            Assert.IsFalse(typing.AuthoredSyllablesOnly);
            Assert.AreEqual(0, typing.CaretIndex, "a header types nothing");
        }

        [Test]
        public void TheEngineReadsTheGroupingItsFlagSelects()
        {
            var typing = new TypingEngine(lyricMap(neverLine()));
            var line = typing.Lines[0];

            Assert.AreNotSame(line.AuthoredGrouping, line.NaturalGrouping, "the fixture's two groupings differ");
            Assert.AreSame(line.NaturalGrouping, typing.GroupingOf(line));

            typing.AuthoredSyllablesOnly = true;
            Assert.AreSame(line.AuthoredGrouping, typing.GroupingOf(line));
        }

        [Test]
        public void ALineWithNothingToSplitSharesOneGrouping()
        {
            var line = TypingLine.FromLyricLine(new LyricLine
            {
                RawText = "go now",
                StartTime = 0,
                EndTime = 3000,
                SingEndTime = 2000,
                Units = new[] { new TimedUnit { Text = "go", StartTime = 0, EndTime = 1000 }, new TimedUnit { Text = "now", StartTime = 1000, EndTime = 2000 } },
            });

            Assert.AreSame(line.AuthoredGrouping, line.NaturalGrouping);
        }

        #endregion

        #region Stored and live runs through the scorer

        /// <summary>
        /// THE STORED-ERA PIN through the production scorer: the same five presses judge on the natural
        /// groups with no extended header (or with the bit clear) and on the one authored group with it,
        /// and the stored arms are exactly what a bare engine on the natural grouping makes of them.
        /// </summary>
        [Test]
        public void TheScorerTakesTheGroupingFromTheReplay()
        {
            var map = typeBeatBeatmap(neverLine());

            TypeBeatReplayAccount score(Replay replay) => TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), replay, TypoRule.Deferred, ComboRestoreRule.OnFix);

            var old = score(neverReplay(null));
            var clear = score(neverReplay(false));
            var live = score(neverReplay(true));

            Assert.AreEqual(0, old.UnconsumedFrames);
            Assert.AreEqual(1, old.Statistics.GetValueOrDefault(HitResult.Meh), "n, 600 past its group's start");
            Assert.AreEqual(2, old.Statistics.GetValueOrDefault(HitResult.Ok), "e past the invented 'ne' group, v anchored to 'ver'");
            Assert.AreEqual(2, old.Statistics.GetValueOrDefault(HitResult.Great));
            Assert.AreEqual(old.TotalScore, clear.TotalScore, "a clear bit is the stored era too");

            Assert.AreEqual(1, live.Statistics.GetValueOrDefault(HitResult.Meh));
            Assert.AreEqual(0, live.Statistics.GetValueOrDefault(HitResult.Ok));
            Assert.AreEqual(4, live.Statistics.GetValueOrDefault(HitResult.Great), "one group over the word: four Greats");
            Assert.Greater(live.TotalScore, old.TotalScore);
        }

        /// <summary>
        /// Fed on an engine the LIVE factory built (the watch path), a stored replay re-derives the
        /// natural groups bit for bit: every cell's delta and state equals a bare natural-era engine
        /// driven with the same presses.
        /// </summary>
        [Test]
        public void AStoredReplayFedOnALiveEngineReDerivesTheNaturalGroupsBitForBit()
        {
            var live = liveEngine(typeBeatBeatmap(neverLine()));
            Assert.IsTrue(live.AuthoredSyllablesOnly);

            foreach (var frame in neverReplay(null).Frames.Cast<TypeBeatReplayFrame>())
                ReplayEngineFeed.Apply(live, frame);

            var bare = new TypingEngine(lyricMap(neverLine())) { AllowWrongInput = true, SyllableTiming = true, CharTimedStretch = true, FirstCharTiming = true };
            bare.Update(1600);

            foreach (char c in "never")
                Assert.IsTrue(bare.ProcessKey(c, 1600));

            Assert.IsFalse(live.AuthoredSyllablesOnly, "the header put the run back on its own era");

            for (int i = 0; i < bare.Lines[0].Cells.Count; i++)
            {
                Assert.AreEqual(bare.Lines[0].Cells[i].State, live.Lines[0].Cells[i].State, $"cell {i} state");
                Assert.AreEqual(bare.Lines[0].Cells[i].JudgedDelta, live.Lines[0].Cells[i].JudgedDelta, $"cell {i} delta");
            }

            Assert.AreEqual(new double?[] { 600, 200, 200, 0, 0 }, live.Lines[0].Cells.Select(c => c.JudgedDelta).ToArray());
        }

        #endregion

        #region Autoplay carries the era

        /// <summary>
        /// "never gonna give you up", nothing subdivided: autoplay under the live stack presses for one
        /// group per word, and because it carries the era in its own header it stays PERFECT after the
        /// watch path's attach has cleared every second-word era off the live engine. The same presses
        /// with the header stripped are judged on the natural groups and are not.
        /// </summary>
        [Test]
        public void AutoplayIsPerfectOnAnUnsubdividedWordAcrossTheAttach()
        {
            var line = new LyricLine
            {
                RawText = "never gonna give you up",
                StartTime = 1000,
                EndTime = 60000,
                SingEndTime = 5000,
                Units = new[]
                {
                    new TimedUnit { Text = "never", StartTime = 1000, EndTime = 2000 },
                    new TimedUnit { Text = "gonna", StartTime = 2000, EndTime = 3000 },
                    new TimedUnit { Text = "give", StartTime = 3000, EndTime = 3600 },
                    new TimedUnit { Text = "you", StartTime = 3600, EndTime = 4200 },
                    new TimedUnit { Text = "up", StartTime = 4200, EndTime = 5000 },
                },
            };

            var map = typeBeatBeatmap(line);
            var frames = new TypeBeatModAutoplay().CreateReplayData(map, Array.Empty<Mod>()).Replay.Frames.Cast<TypeBeatReplayFrame>().ToList();

            TypingEngine attachAndPlay(IEnumerable<TypeBeatReplayFrame> stream)
            {
                var engine = liveEngine(typeBeatBeatmap(line));

                // What TypeBeatPlayfield does on attach, before priming from the first header.
                ReplayEngineFeed.ClearExtendedEras(engine);

                foreach (var frame in stream)
                    ReplayEngineFeed.Apply(engine, frame);

                return engine;
            }

            var carried = attachAndPlay(frames);
            Assert.IsTrue(carried.AuthoredSyllablesOnly);

            foreach (var cell in carried.Lines[0].Cells.Where(c => c.IsTypeable))
            {
                Assert.AreEqual(CellState.Correct, cell.State);
                Assert.AreEqual(0, cell.JudgedDelta!.Value, 0.5, $"cell '{cell.Expected}' @ {cell.TargetTime}");
            }

            // Without the header the attach leaves the engine on the NATURAL grouping, which is what
            // the lyric stack then marks and lights: the phantom syllables the change removed.
            var stripped = attachAndPlay(frames.Where(f => !f.IsConfigExtended));
            Assert.IsFalse(stripped.AuthoredSyllablesOnly);
            Assert.IsNotEmpty(stripped.GroupingOf(stripped.Lines[0]).MarkerCells.ToArray(), "natural marks on never|gonna");
            Assert.IsEmpty(carried.GroupingOf(carried.Lines[0]).MarkerCells.ToArray(), "and none with the era carried");
        }

        /// <summary>
        /// The editor's autoplay toggle drops every frame at or before the playhead before attaching.
        /// Whatever survives still carries the header the watch path primes from, so a toggle part way
        /// through still presses for the grouping it is judged on.
        /// </summary>
        [Test]
        public void AnAutoplayTrimmedByTheEditorToggleStillCarriesTheEra()
        {
            var lines = new[]
            {
                neverLine(),
            };

            var frames = new TypeBeatModAutoplay().CreateReplayData(typeBeatBeatmap(lines), Array.Empty<Mod>()).Replay.Frames.Cast<TypeBeatReplayFrame>().ToList();
            var presses = frames.Where(f => !f.IsConfigExtended).ToList();

            foreach (double playhead in presses.Take(presses.Count - 1).Select(p => p.Time))
            {
                var survivors = frames.Where(f => f.Time > playhead).ToList();

                if (survivors.Count == 0)
                    continue;

                Assert.IsTrue(survivors.Any(f => f.IsConfigExtended && f.AuthoredSyllablesOnly), $"a header survives a toggle at {playhead}");
            }
        }

        #endregion
    }
}
