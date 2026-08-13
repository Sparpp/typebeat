// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Backlog 150: the Hard Rock mod, the exact mirror of Easy on the general window scale backlog 149
// built (TypingEngine.WindowScale). The mechanism itself is pinned by TypeBeatModEasyTest, so this
// fixture covers what is specific to HR: that halving really tightens what a press is graded as,
// that a replay is re-judged on the tightened ladder, the shipping surface (acronym, type, ranked
// flag, score multiplier, pp), the one incompatibility, and the score-multiplier headroom the
// server's stack cap depends on.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.ControlPoints;
using typebeat.Game.Replays;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Utils;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class TypeBeatModHardRockTest
    {
        #region Fixture

        /// <summary>The same one-line "ab" map <see cref="TypeBeatModEasyTest"/> judges against.</summary>
        private static LyricBeatmap engineMap() => new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata
            {
                Artist = "Test",
                Title = "Song",
                FolderPath = @"X:\nowhere",
                AudioFileName = "a.mp3",
            },
            Lines = new[]
            {
                new LyricLine
                {
                    RawText = "ab",
                    StartTime = 0,
                    EndTime = 20000,
                    SingEndTime = 3000,
                    Units = new[] { new TimedUnit { Text = "ab", StartTime = 1000, EndTime = 3000 } },
                },
            },
            Granularity = TimingGranularity.Line,
        };

        private static TypeBeatBeatmap replayMap()
        {
            var line = new LyricLine
            {
                RawText = "abc",
                StartTime = 0,
                EndTime = 20000,
                SingEndTime = 12000,
                Units = new[] { new TimedUnit { Text = "abc", StartTime = 0, EndTime = 12000 } },
            };

            var map = new TypeBeatBeatmap();
            map.HitObjects.Add(new TypeBeatHitObject { StartTime = 0, LineIndex = 0, Line = line, Granularity = TimingGranularity.Line });

            foreach (var hitObject in map.HitObjects)
                hitObject.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty(), CancellationToken.None);

            return map;
        }

        /// <summary>Judge one press of 'a' (target 1000) at <paramref name="pressTime"/>.</summary>
        private static CharJudgement judgeFirstCell(double windowScale, double pressTime)
        {
            var built = new TypingEngine(engineMap());

            if (windowScale != 1)
                built.WindowScale *= windowScale;

            CharJudgement? seen = null;
            built.CharJudged += judgement => seen = judgement;

            built.Update(0);
            built.Update(pressTime);
            Assert.IsTrue(built.ProcessKey('a', pressTime));
            Assert.IsNotNull(seen);

            return seen!.Value;
        }

        private static TypeBeatScoreMultiplierCalculator calculator()
            => new TypeBeatScoreMultiplierCalculator(new ScoreMultiplierContext(new BeatmapDifficulty()));

        #endregion

        [Test]
        public void ReportsRankedDifficultyIncreaseModWithHrAcronym()
        {
            var mod = new TypeBeatModHardRock();

            Assert.AreEqual("Hard Rock", mod.Name);
            Assert.AreEqual("HR", mod.Acronym);
            Assert.AreEqual(ModType.DifficultyIncrease, mod.Type);
            Assert.IsTrue(mod.Ranked, "Hard Rock is a priced handicap; its scores must reach the leaderboards.");
            Assert.AreEqual("Half as long to hit every character.", mod.Description.ToString());
            Assert.IsTrue(mod.HasImplementation);
            Assert.IsNotNull(mod.Icon);

            // The exact mirror of Easy, which is the whole design (see the mod's own docs).
            Assert.AreEqual(0.5, TypeBeatModHardRock.WINDOW_SCALE, 1e-9);
            Assert.AreEqual(1.0, TypeBeatModHardRock.WINDOW_SCALE * TypeBeatModEasy.WINDOW_SCALE, 1e-9);
        }

        [Test]
        public void RulesetSurfacesHardRockUnderDifficultyIncrease()
        {
            var ruleset = new TypeBeatRuleset();

            Assert.IsTrue(ruleset.GetModsFor(ModType.DifficultyIncrease).Any(m => m is TypeBeatModHardRock),
                "Hard Rock must be offered in the mod-select overlay under Difficulty Increase.");

            var acronyms = ruleset.AllMods.Select(m => m.Acronym).ToList();

            Assert.AreEqual(acronyms.Count, acronyms.Distinct().Count(), "two mods share an acronym");
            Assert.AreEqual(1, acronyms.Count(a => a == "HR"));
        }

        /// <summary>
        /// Halving the ladder is what the mod IS, so it has to decide the grade: a press 201 ms late
        /// is comfortably Great on the unscaled ladder (GreatLate 400) and an Ok on the halved one
        /// (200), and a press 1001 ms late is a paid Meh unscaled (MehLate 2000) but falls off the
        /// halved ladder entirely (1000) and scores nothing at all.
        /// </summary>
        [Test]
        public void TheHalvedLadderDecidesWhatAPressIsClassifiedAs()
        {
            Assert.AreEqual(JudgementType.Great, judgeFirstCell(1, 1201).Type);
            Assert.AreEqual(JudgementType.Ok, judgeFirstCell(TypeBeatModHardRock.WINDOW_SCALE, 1201).Type);

            var plainLate = judgeFirstCell(1, 2001);
            var hardLate = judgeFirstCell(TypeBeatModHardRock.WINDOW_SCALE, 2001);

            Assert.AreEqual(JudgementType.Meh, plainLate.Type);
            Assert.AreEqual(50, plainLate.PointsAwarded); // Meh base 50, combo 0 before the press
            Assert.AreEqual(JudgementType.Lagging, hardLate.Type);
            Assert.AreEqual(0, hardLate.PointsAwarded);
        }

        /// <summary>
        /// A replay carries KEYSTROKES and is re-judged from scratch, so the recalculation path has
        /// to apply the same window scale the live run did. Three cells struck 300 ms late: a Great
        /// apiece unscaled, an Ok apiece under Hard Rock.
        /// </summary>
        [Test]
        public void AReplayIsReJudgedOnTheTightenedLadder()
        {
            var map = replayMap();

            // Cell targets 0, 4000, 8000 (three chars evenly over the unit's [0, 12000]).
            var frames = new List<TypeBeatReplayFrame> { TypeBeatReplayFrame.CreateConfigFrame(0, true) };

            for (int i = 0; i < 3; i++)
                frames.Add(new TypeBeatReplayFrame(i * 4000 + 300, "abc"[i]));

            var replay = new Replay();
            replay.Frames.AddRange(frames);

            var plain = TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), replay, TypoRule.Deferred, ComboRestoreRule.OnFix);
            var hard = TypeBeatReplayScorer.Score(map, new Mod[] { new TypeBeatModHardRock() }, replay, TypoRule.Deferred, ComboRestoreRule.OnFix);

            Assert.AreEqual(3, plain.Statistics.GetValueOrDefault(HitResult.Great));
            Assert.AreEqual(0, plain.Statistics.GetValueOrDefault(HitResult.Ok));

            Assert.AreEqual(0, hard.Statistics.GetValueOrDefault(HitResult.Great));
            Assert.AreEqual(3, hard.Statistics.GetValueOrDefault(HitResult.Ok));
        }

        /// <summary>
        /// 1.10x, and the value has a ceiling to respect as well as a provenance (see
        /// <see cref="TypeBeatScoreMultiplierCalculator"/>): the fattest stack a client can now
        /// assemble out of ranked mods must stay under the server's <c>ModMultiplier.STACK_CAP</c>
        /// of 2.0, or an honest maximal play is clamped and stored UNRANKED.
        /// </summary>
        [Test]
        public void ScoreMultiplierIsOneAndATenthAndLeavesTheStackUnderTheServersCap()
        {
            var mods = calculator();

            Assert.AreEqual(1.10, mods.CalculateFor(new Mod[] { new TypeBeatModHardRock() }), 1e-9);

            // 1.10 * 1.05 = 1.155.
            Assert.AreEqual(1.155, mods.CalculateFor(new Mod[] { new TypeBeatModHardRock(), new TypeBeatModLiterate() }), 1e-9);

            var doubleTime = new TypeBeatModDoubleTime();
            doubleTime.SpeedChange.Value = 2.00;

            // DT@2.00 (1.46) x FL (1.05) x LT (1.05) x HR (1.10) = 1.770615.
            double fattest = mods.CalculateFor(new Mod[]
            {
                doubleTime,
                new TypeBeatModFlashlight(),
                new TypeBeatModLiterate(),
                new TypeBeatModHardRock(),
            });

            Assert.AreEqual(1.770615, fattest, 1e-9);
            Assert.Less(fattest, 2.0, "the server's absolute stack cap would clamp an honest play");

#pragma warning disable CS0618 // Member is obsolete
            Assert.AreEqual(1.10, new TypeBeatModHardRock().ScoreMultiplier, 1e-9);
#pragma warning restore CS0618
        }

        /// <summary>
        /// pp: Hard Rock is a flat 1.25, applied once however many times it appears, and orthogonal
        /// to everything else in the table (1.25 * 0.90 for a No Fail stack). Separate from the 1.10x
        /// SCORE multiplier, exactly as Easy's 0.75 is separate from its 0.5x.
        /// </summary>
        [Test]
        public void PerformancePointsPriceHardRockAtFiveQuarters()
        {
            Assert.AreEqual(1.25, PerformancePoints.ModMultiplier(new Mod[] { new TypeBeatModHardRock() }, 500), 1e-9);
            Assert.AreEqual(1.125, PerformancePoints.ModMultiplier(new Mod[] { new TypeBeatModHardRock(), new TypeBeatModNoFail() }, 500), 1e-9);
            Assert.AreEqual(1.25, PerformancePoints.ModMultiplier(new Mod[] { new TypeBeatModHardRock(), new TypeBeatModHardRock() }, 500), 1e-9);
        }

        /// <summary>
        /// Exactly one exclusion, and it is the one osu declares too: Easy, which scales the same
        /// windows the other way. osu's <see cref="ModDifficultyAdjust"/> entry is dropped rather
        /// than inherited, because no type!beat mod can ever derive from it.
        /// </summary>
        [Test]
        public void ExcludesEasyAndComposesWithEverythingElseTheRulesetOffers()
        {
            var ruleset = new TypeBeatRuleset();
            var hardRock = new TypeBeatModHardRock();

            Assert.AreEqual(new[] { typeof(ModEasy) }, hardRock.IncompatibleMods);
            Assert.IsFalse(ModUtils.CheckCompatibleSet(new Mod[] { new TypeBeatModHardRock(), new TypeBeatModEasy() }));

            foreach (var other in ruleset.AllMods.OfType<Mod>().Where(m => m is not TypeBeatModHardRock and not TypeBeatModEasy))
            {
                Assert.IsFalse(hardRock.IncompatibleMods.Any(t => t.IsInstanceOfType(other)),
                    $"Hard Rock declares {other.Acronym} incompatible");
                Assert.IsFalse(other.IncompatibleMods.Any(t => t.IsInstanceOfType(hardRock)),
                    $"{other.Acronym} declares Hard Rock incompatible");
            }

            // The stacks that matter: a rate mod scales the same windows, and the two must compose.
            Assert.IsTrue(ModUtils.CheckCompatibleSet(new Mod[] { new TypeBeatModHardRock(), new TypeBeatModDoubleTime() }));
            Assert.IsTrue(ModUtils.CheckCompatibleSet(new Mod[] { new TypeBeatModHardRock(), new TypeBeatModHalfTime() }));
            Assert.IsTrue(ModUtils.CheckCompatibleSet(new Mod[] { new TypeBeatModHardRock(), new TypeBeatModFlashlight(), new TypeBeatModLiterate() }));
        }

        /// <summary>
        /// osu's Hard Rock raises DrainRate; type!beat has no drain, so the inherited behaviour is
        /// overridden away rather than left to move a number nothing reads.
        /// </summary>
        [Test]
        public void DoesNotTouchTheOsuDifficultyAttributes()
        {
            var difficulty = new BeatmapDifficulty
            {
                CircleSize = 5,
                ApproachRate = 6,
                DrainRate = 7,
                OverallDifficulty = 8,
            };

            new TypeBeatModHardRock().ApplyToDifficulty(difficulty);

            Assert.AreEqual(5, difficulty.CircleSize, 1e-9);
            Assert.AreEqual(6, difficulty.ApproachRate, 1e-9);
            Assert.AreEqual(7, difficulty.DrainRate, 1e-9);
            Assert.AreEqual(8, difficulty.OverallDifficulty, 1e-9);
        }
    }
}
