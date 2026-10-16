// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// KEY-TIME JUDGEMENT (TypingEngine.KeyTimeJudgement): bit 9 of the SECOND CONFIG flags word. Live, a
// typed character is judged and recorded at the time its key went down, also when the OS commits its
// text frames later or commits no space for the spacebar. Clear (every replay stored before it), the
// frames carry the time the key and its commit were paired, and they re-derive at that time. This
// file pins the carrier (the bit, its round trip, Apply and ClearExtendedEras, the live factory) and
// both eras' re-derivation; the live key handler is pinned in TestSceneTypeBeatTextInput.

using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.ControlPoints;
using typebeat.Game.Replays.Legacy;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Replays;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.UI;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class KeyTimeJudgementEraTest
    {
        private static readonly Beatmap dummy_beatmap = new Beatmap();

        private static LyricLine line() => new LyricLine
        {
            RawText = "ab",
            StartTime = 1000,
            EndTime = 9000,
            SingEndTime = 2000,
            Units = new[] { new TimedUnit { Text = "ab", StartTime = 1000, EndTime = 2000 } },
        };

        private static TypingEngine engine() => new TypingEngine(new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata { Artist = "Test", Title = "Song", FolderPath = @"X:\nowhere", AudioFileName = "a.mp3" },
            Lines = new[] { line() },
            Granularity = TimingGranularity.Line,
        });

        private static TypeBeatReplayFrame roundTrip(TypeBeatReplayFrame frame)
        {
            var decoded = new TypeBeatReplayFrame();
            decoded.FromLegacy(frame.ToLegacy(dummy_beatmap), dummy_beatmap);
            return decoded;
        }

        [Test]
        public void TheBitIsSecondWordBitNineAndRoundTrips()
        {
            var frame = TypeBeatReplayFrame.CreateExtendedConfigFrame(0, keyTimeJudgement: true);

            Assert.AreEqual(512, (int)frame.ToLegacy(dummy_beatmap).MouseY!.Value, "bit 9 of the second word, and nothing else");

            var decoded = roundTrip(frame);
            Assert.IsTrue(decoded.IsConfigExtended && decoded.KeyTimeJudgement);
            Assert.IsFalse(decoded.EarlyFinish || decoded.JapaneseInputEra3 || decoded.RushCapCostsAccuracy, "it is mistaken for no other bit");

            Assert.IsFalse(roundTrip(TypeBeatReplayFrame.CreateExtendedConfigFrame(0)).KeyTimeJudgement, "a clear bit decodes clear");
        }

        [Test]
        public void EverySecondWordBitRoundTripsBesideIt()
        {
            var frame = TypeBeatReplayFrame.CreateExtendedConfigFrame(0, rushCapCostsAccuracy: true, inputEra2: true, authoredSyllablesOnly: true,
                alignSubdivisionTargets: true, earlyFinish: true, japaneseRomajiInput: true, japaneseWordTiming: true, japaneseInputEra2: true,
                japaneseInputEra3: true, keyTimeJudgement: true);

            Assert.AreEqual(1023, (int)frame.ToLegacy(dummy_beatmap).MouseY!.Value, "bits 0 to 9");

            var decoded = roundTrip(frame);
            Assert.IsTrue(decoded.RushCapCostsAccuracy && decoded.InputEra2 && decoded.AuthoredSyllablesOnly && decoded.AlignSubdivisionTargets
                          && decoded.EarlyFinish && decoded.JapaneseRomajiInput && decoded.JapaneseWordTiming && decoded.JapaneseInputEra2
                          && decoded.JapaneseInputEra3 && decoded.KeyTimeJudgement);
            Assert.AreEqual(1023, (int)decoded.ToLegacy(dummy_beatmap).MouseY!.Value, "and re-encodes whole");
        }

        /// <summary>
        /// The same value in the FIRST word is the wall-clock axis (first-word bit 9), never this era:
        /// each header kind decodes its own word.
        /// </summary>
        [Test]
        public void TheFirstWordsBitNineIsNotThisEra()
        {
            var config = new TypeBeatReplayFrame();
            config.FromLegacy(new LegacyReplayFrame(0, TypeBeatReplayFrame.CONFIG, 512, ReplayButtonState.None), dummy_beatmap);

            Assert.IsTrue(config.WallClockFrames);
            Assert.IsFalse(config.KeyTimeJudgement);
        }

        [Test]
        public void ABareEngineIsInTheStoredEraAndTheLiveFactorySetsIt()
        {
            Assert.IsFalse(engine().KeyTimeJudgement, "the default is the pairing-time stamping every stored replay was recorded with");

            Assert.IsTrue(new DrawableTypeBeatRuleset(new TypeBeatRuleset(), typeBeatBeatmap(), Array.Empty<Mod>()).Engine.KeyTimeJudgement,
                "every new live run is judged at key time");
        }

        [Test]
        public void TheConfigFrameClearsTheBitAndTheExtendedFrameSetsIt()
        {
            var typing = engine();
            typing.KeyTimeJudgement = true;

            ReplayEngineFeed.Apply(typing, TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true));
            Assert.IsFalse(typing.KeyTimeJudgement, "a CONFIG frame alone is a replay recorded before the era");

            ReplayEngineFeed.Apply(typing, TypeBeatReplayFrame.CreateExtendedConfigFrame(0, keyTimeJudgement: true));
            Assert.IsTrue(typing.KeyTimeJudgement);

            ReplayEngineFeed.ClearExtendedEras(typing);
            Assert.IsFalse(typing.KeyTimeJudgement);
            Assert.AreEqual(0, typing.CaretIndex, "a header types nothing");
        }

        /// <summary>
        /// One physical press, recorded by each era: the key for 'a' goes down at 1000 (its target)
        /// and the OS commits its text 60 ms later. Before the era the frame carries the pairing
        /// time, 1060, and the stored run keeps the 60 ms it was graded with; under it the frame
        /// carries the key time and the press is on target. Each re-derives as its own frames say,
        /// through the legacy encoding.
        /// </summary>
        [TestCase(false, 1060, 60)]
        [TestCase(true, 1000, 0)]
        public void EachEraReDerivesAtTheTimeItsFramesCarry(bool keyTime, double stamped, double delta)
        {
            var frames = new List<ReplayFrame>
            {
                TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true),
                TypeBeatReplayFrame.CreateExtendedConfigFrame(0, rushCapCostsAccuracy: true, inputEra2: true, keyTimeJudgement: keyTime),
                new TypeBeatReplayFrame(stamped, 'a'),
            };

            var decoded = frames.ConvertAll(f =>
            {
                var frame = roundTrip((TypeBeatReplayFrame)f);
                frame.Time = f.Time;
                return (ReplayFrame)frame;
            });

            var typing = engine();
            ReplayEngineFeed.RebuildTo(typing, decoded, 1500);

            Assert.AreEqual(keyTime, typing.KeyTimeJudgement);
            Assert.AreEqual(CellState.Correct, typing.Lines[0].Cells[0].State);
            Assert.AreEqual(delta, typing.Lines[0].Cells[0].JudgedDelta);
        }

        private static TypeBeatBeatmap typeBeatBeatmap()
        {
            var map = new TypeBeatBeatmap();
            map.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            map.HitObjects.Add(new TypeBeatHitObject { StartTime = 1000, LineIndex = 0, Line = line(), Granularity = TimingGranularity.Line });

            foreach (var hitObject in map.HitObjects)
                hitObject.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty(), CancellationToken.None);

            return map;
        }
    }
}
