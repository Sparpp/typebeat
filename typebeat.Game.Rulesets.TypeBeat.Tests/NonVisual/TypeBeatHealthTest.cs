// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Headless coverage of the health / fail model (backlog item 9): sustained not-typing must
// drain HP and fail the play, while imperfect-but-complete play survives. The engine is the
// judgement authority; these tests bridge its judgements into TypeBeatHealthProcessor exactly
// as TypeBeatPlayfield does (CharJudged -> Great/Ok/Meh, seal misses -> Miss, wrong keys ->
// ApplyWrongKeyStreak), so they exercise the real magnitudes end-to-end without a game host.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using typebeat.Game.Rulesets.Judgements;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Judgements;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class TypeBeatHealthTest
    {
        private const double frame = 1000.0 / 60;

        #region Direct-processor unit pins

        [Test]
        public void MissDrainsAndCorrectCharRecovers()
        {
            var health = new TypeBeatHealthProcessor();

            apply(health, HitResult.Miss);
            Assert.AreEqual(1 - TypeBeatHealthProcessor.MISS_HEALTH_DRAIN, health.Health.Value, 1e-9);

            apply(health, HitResult.Great);
            // Recovery caps at the full bar.
            Assert.AreEqual(1.0, health.Health.Value, 1e-9);

            // From a drained bar the recovery is a real increment, not a snap to full.
            for (int i = 0; i < 20; i++)
                apply(health, HitResult.Miss);

            double drained = health.Health.Value;
            apply(health, HitResult.Meh);
            Assert.AreEqual(drained + TypeBeatHealthProcessor.MEH_HEALTH_INCREASE, health.Health.Value, 1e-9);
        }

        [Test]
        public void SustainedMissesEmptyBarAndFail()
        {
            // The whole point of item 9: nothing but misses (i.e. never typing) empties the bar
            // and fails. Death lands after ceil(1 / MISS_HEALTH_DRAIN) misses.
            int missesToDeath = (int)Math.Ceiling(1.0 / TypeBeatHealthProcessor.MISS_HEALTH_DRAIN);

            var health = new TypeBeatHealthProcessor();

            for (int i = 0; i < missesToDeath - 1; i++)
                apply(health, HitResult.Miss);

            Assert.IsFalse(health.HasFailed, "must not fail before the bar empties");

            apply(health, HitResult.Miss);
            Assert.IsTrue(health.HasFailed, "sustained misses must empty the bar and fail");
        }

        [Test]
        public void WrongKeyStreakEmptiesBarAndFailsAtThreshold()
        {
            var health = new TypeBeatHealthProcessor();

            // An uninterrupted mash from full depletes the bar linearly (the "stop mashing" warning)
            // and fails exactly at the streak threshold.
            for (int streak = 1; streak < TypeBeatHealthProcessor.WRONG_KEY_FAIL_STREAK; streak++)
            {
                health.ApplyWrongKeyStreak(streak);
                Assert.IsFalse(health.HasFailed, $"must not fail at streak {streak}");
            }

            Assert.Less(health.Health.Value, 0.1, "bar is nearly empty just before the fail threshold");

            health.ApplyWrongKeyStreak(TypeBeatHealthProcessor.WRONG_KEY_FAIL_STREAK);
            Assert.IsTrue(health.HasFailed, "mashing must fail at the streak threshold");
        }

        [Test]
        public void OccasionalSpreadMissesNeverFail()
        {
            // A few misses scattered through otherwise-correct play (here a punishing ~12.5%, one
            // miss every 8 cells, over a long map) must comfortably survive: the bar refills between
            // misses and never approaches empty. Only SUSTAINED not-typing kills.
            var health = new TypeBeatHealthProcessor();

            double min = 1;

            for (int i = 0; i < 1600; i++)
            {
                apply(health, i % 8 == 7 ? HitResult.Miss : HitResult.Great);
                min = Math.Min(min, health.Health.Value);
            }

            Assert.IsFalse(health.HasFailed, "scattered misses must not fail");
            Assert.Greater(min, 0.9, "health stays comfortably full through spread-out misses");
        }

        #endregion

        #region Synthetic multi-line map (runs without the standalone maps checkout)

        [Test]
        public void SyntheticAfkFailsPartwayThroughMap()
        {
            var beatmap = syntheticMap(lineCount: 20, cellsPerLine: 10);
            var engine = new TypingEngine(beatmap);
            var bridge = new HealthBridge(engine);

            double failTime = playThrough(engine, bridge, beatmap, typeEverything: false);

            Assert.IsTrue(bridge.Health.HasFailed, "sustained AFK must fail");
            Assert.Greater(failTime, 0, "must not fail on the very first frame");
            Assert.Less(failTime, beatmap.LastLineEnd, "AFK must fail before the map ends");
        }

        [Test]
        public void SyntheticPerfectPlayKeepsHealthFull()
        {
            var beatmap = syntheticMap(lineCount: 20, cellsPerLine: 10);
            var engine = new TypingEngine(beatmap);
            var bridge = new HealthBridge(engine);

            playThrough(engine, bridge, beatmap, typeEverything: true);

            Assert.IsFalse(bridge.Health.HasFailed, "perfect play must never fail");
            Assert.AreEqual(1.0, bridge.Health.Health.Value, 1e-9, "perfect play keeps the bar full");
        }

        #endregion

        #region Real Spectator map pins

        [Test]
        public void RealSpectatorFullAfkFailsPartwayThroughMap()
        {
            var beatmap = loadSpectator();
            var engine = new TypingEngine(beatmap);
            var bridge = new HealthBridge(engine);

            double failTime = playThrough(engine, bridge, beatmap, typeEverything: false);

            Assert.IsTrue(bridge.Health.HasFailed, "sustained AFK on the real map must fail");
            Assert.Greater(failTime, 0);
            // Dies well inside the first half of the ~165s map — clearly "sustained not typing",
            // not a last-second technicality.
            Assert.Less(failTime, beatmap.LastLineEnd * 0.5, "AFK must die partway through, not at the end");
            TestContext.WriteLine($"Full-AFK death lands at t={failTime:0}ms of {beatmap.LastLineEnd:0}ms.");
        }

        [Test]
        public void RealSpectatorPerfectPlayKeepsHealthHigh()
        {
            var beatmap = loadSpectator();
            var engine = new TypingEngine(beatmap);
            var bridge = new HealthBridge(engine);

            playThrough(engine, bridge, beatmap, typeEverything: true);

            Assert.IsTrue(engine.IsFinished);
            Assert.AreEqual(0, engine.BuildResults().Counts[JudgementType.Miss], "rhythm-perfect play has zero misses");
            Assert.IsFalse(bridge.Health.HasFailed, "rhythm-perfect play must never fail");
            Assert.AreEqual(1.0, bridge.Health.Health.Value, 1e-9, "rhythm-perfect play keeps the bar full");
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Runs the engine frame-by-frame. When <paramref name="typeEverything"/> is set it types
        /// each caret cell on the first frame at/after its target (rhythm-perfect play); otherwise
        /// it never types (full AFK). Returns the gameplay time the play first entered a failed
        /// state, or -1 if it never failed.
        /// </summary>
        private static double playThrough(TypingEngine engine, HealthBridge bridge, LyricBeatmap beatmap, bool typeEverything)
        {
            double failTime = -1;

            for (double t = 0; t <= beatmap.LastLineEnd + 1000 && !engine.IsFinished; t += frame)
            {
                engine.Update(t);

                if (typeEverything)
                {
                    while (engine.ActiveLineIndex != -1 && !engine.IsLineComplete)
                    {
                        var cell = engine.Lines[engine.ActiveLineIndex].Cells[engine.CaretIndex];

                        if (cell.TargetTime > t)
                            break;

                        engine.ProcessKey(cell.Expected, t);
                    }
                }

                if (failTime < 0 && bridge.Health.HasFailed)
                {
                    failTime = t;
                    break;
                }
            }

            if (typeEverything)
                engine.Update(beatmap.LastLineEnd + 1100);

            return failTime;
        }

        private static void apply(TypeBeatHealthProcessor health, HitResult type)
            => health.ApplyResult(new JudgementResult(sharedObject, sharedJudgement) { Type = type });

        // Health only reads result.Type, so a single shared carrier is sufficient.
        private static readonly TypeBeatCharObject sharedObject = new TypeBeatCharObject();
        private static readonly TypeBeatCharJudgement sharedJudgement = new TypeBeatCharJudgement();

        private static LyricBeatmap syntheticMap(int lineCount, int cellsPerLine)
        {
            string word = new string('a', cellsPerLine); // all-typeable, no punctuation/spaces
            var lines = new List<LyricLine>(lineCount);

            for (int i = 0; i < lineCount; i++)
            {
                double start = i * 3000;
                double end = start + 2000;
                lines.Add(new LyricLine
                {
                    RawText = word,
                    StartTime = start,
                    EndTime = end,
                    SingEndTime = end,
                    Units = new[] { new TimedUnit { Text = word, StartTime = start, EndTime = end } },
                });
            }

            return new LyricBeatmap
            {
                Metadata = new LyricBeatmapMetadata
                {
                    Artist = "Test",
                    Title = "AFK",
                    FolderPath = @"X:\nowhere",
                    AudioFileName = "a.mp3",
                },
                Lines = lines,
                Granularity = TimingGranularity.Line,
            };
        }

        private static LyricBeatmap loadSpectator()
        {
            string path = StandaloneMaps.Require("Friday Pilots Club - Spectator", "timing.json");
            Assert.IsTrue(TimingJsonLoader.TryLoad(path, out var lyricLines));

            return new LyricBeatmap
            {
                Metadata = new LyricBeatmapMetadata
                {
                    Artist = "Friday Pilots Club",
                    Title = "Spectator",
                    FolderPath = Path.GetDirectoryName(path)!,
                    AudioFileName = "unused.mp3",
                    HasWordTiming = true,
                },
                Lines = lyricLines,
                Granularity = TimingGranularity.Word,
            };
        }

        /// <summary>
        /// Mirrors <see cref="UI.TypeBeatPlayfield"/>'s health wiring: a correct char reaches health
        /// as its Great/Ok/Meh result, every cell that seals untyped as a Miss, and a rejected wrong
        /// key through the mash-streak drain — the same paths the drawable bridge takes in gameplay.
        /// </summary>
        private sealed class HealthBridge
        {
            public readonly TypeBeatHealthProcessor Health = new TypeBeatHealthProcessor();

            public HealthBridge(TypingEngine engine)
            {
                engine.CharJudged += j => TypeBeatHealthTest.apply(Health, toHitResult(j.Type));
                engine.LineSealed += r =>
                {
                    for (int i = 0; i < r.MissedCells; i++)
                        TypeBeatHealthTest.apply(Health, HitResult.Miss);
                };
                engine.WrongKeyRejected += _ => Health.ApplyWrongKeyStreak(engine.ConsecutiveWrongKeys);
            }

            private static HitResult toHitResult(JudgementType type)
            {
                switch (type)
                {
                    case JudgementType.Perfect:
                        return HitResult.Great;

                    case JudgementType.Good:
                        return HitResult.Ok;

                    case JudgementType.Ok:
                        return HitResult.Meh;

                    default:
                        return HitResult.Miss;
                }
            }
        }

        #endregion
    }
}
