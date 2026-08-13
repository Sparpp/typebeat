// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Backlog 149: the Easy mod, and the general window scale it is built on. Two halves:
//
//   1. the MECHANISM (TypingEngine.WindowScale), which is deliberately not an "easy" flag: it is a
//      multiplicative scale on every judgement window, reaching all four sites that grade or
//      measure a delta, and composing by multiplication so a second window-scaling mod can be
//      dropped in without either overwriting the other;
//   2. the MOD's shipping surface: acronym, type, ranked flag, score multiplier, incompatibilities,
//      and the fact that a replay is re-judged on the same ladder the live run was.

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
    public class TypeBeatModEasyTest
    {
        #region Fixture

        /// <summary>
        /// One line "ab" on [0, 20000], vocals [1000, 3000]: cell 'a' targets 1000 and cell 'b'
        /// 1000 + 1*(3000-1000)/2 = 2000. Both cells are TIMED (no space), so the sync readouts
        /// divide by exactly 2, and the line is long enough that a press two seconds behind its
        /// target still lands before the seal.
        /// </summary>
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

        private static TypingEngine engine(double windowScale = 1)
        {
            var built = new TypingEngine(engineMap());

            if (windowScale != 1)
                built.WindowScale *= windowScale;

            return built;
        }

        /// <summary>The same shape as a <see cref="TypeBeatBeatmap"/>, for the replay path.</summary>
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

            // Nested per-cell objects are built by ApplyDefaults, which is what gives the score
            // processor its maximum_statistics.
            foreach (var hitObject in map.HitObjects)
                hitObject.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty(), CancellationToken.None);

            return map;
        }

        #endregion

        #region The mechanism

        /// <summary>
        /// The scale is a pure restatement of the ladder: every bound moves by the same factor, the
        /// asymmetry and the granularity ratio survive, and a factor of 1 is not merely equal to the
        /// unscaled ladder but IS it, so the default path allocates nothing and grades against the
        /// very objects it graded against before the scale existed.
        /// </summary>
        [Test]
        public void ScalingAWindowSetMultipliesEveryBoundAndKeepsUnitScaleIdentical()
        {
            var line = SyncWindows.For(TimingGranularity.Line);

            Assert.AreSame(line, line.Scaled(1));

            var doubled = line.Scaled(2);

            Assert.AreEqual(2.0, doubled.Scale, 1e-9);
            Assert.AreEqual(500, doubled.GreatEarly, 1e-9);   // 250 * 2
            Assert.AreEqual(800, doubled.GreatLate, 1e-9);    // 400 * 2
            Assert.AreEqual(1200, doubled.OkEarly, 1e-9);     // 600 * 2
            Assert.AreEqual(2000, doubled.OkLate, 1e-9);      // 1000 * 2
            Assert.AreEqual(2400, doubled.MehEarly, 1e-9);    // 1200 * 2
            Assert.AreEqual(4000, doubled.MehLate, 1e-9);     // 2000 * 2

            // The granularity split survives: a Syllable cell is still judged more tightly than a
            // Line one, at double the tolerance it had (0.45 * 2 = 0.9).
            var syllable = SyncWindows.For(TimingGranularity.Syllable).Scaled(2);

            Assert.AreEqual(0.9, syllable.Scale, 1e-9);
            Assert.AreEqual(1080, syllable.MehEarly, 1e-9);   // 1200 * 0.45 * 2
            Assert.Less(syllable.MehLate, doubled.MehLate);
        }

        /// <summary>
        /// Two scalings compose by multiplication, so the ladder is the same whichever order the
        /// mods that ask for them are applied in. This is what backlog 150 consumes: a rate mod
        /// multiplies its own factor in on top of Easy's.
        /// </summary>
        [Test]
        public void WindowScalesComposeMultiplicativelyAndCommute()
        {
            var line = SyncWindows.For(TimingGranularity.Line);

            Assert.AreEqual(line.Scaled(2).Scaled(1.5).MehLate, line.Scaled(1.5).Scaled(2).MehLate, 1e-9);
            Assert.AreEqual(line.Scaled(3).MehLate, line.Scaled(2).Scaled(1.5).MehLate, 1e-9);

            // And a scale that undoes another lands exactly back on the base ladder.
            Assert.AreEqual(line.MehLate, line.Scaled(2).Scaled(0.5).MehLate, 1e-9);
        }

        [Test]
        public void AWindowScaleMustBeFiniteAndPositive()
        {
            var line = SyncWindows.For(TimingGranularity.Line);

            Assert.Throws<ArgumentOutOfRangeException>(() => line.Scaled(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => line.Scaled(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => line.Scaled(double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => line.Scaled(double.PositiveInfinity));

            var built = engine();

            Assert.Throws<ArgumentOutOfRangeException>(() => built.WindowScale = 0);
            Assert.Throws<ArgumentOutOfRangeException>(() => built.WindowScale = -2);
            Assert.Throws<ArgumentOutOfRangeException>(() => built.WindowScale = double.NaN);
            Assert.AreEqual(1, built.WindowScale, 1e-9);
        }

        [Test]
        public void AnUnmoddedEngineGradesAgainstTheUnscaledLadder()
        {
            var built = engine();

            Assert.AreEqual(1, built.WindowScale, 1e-9);
            Assert.AreSame(SyncWindows.For(TimingGranularity.Line), built.Windows);
        }

        /// <summary>
        /// Call sites 2 and 3 of 4: the two <c>Classify</c> calls. A press 401 ms late is one
        /// millisecond outside the unscaled Great window and comfortably inside the doubled one
        /// (800), and a press 2001 ms late falls off the unscaled ladder entirely (Lagging, 0
        /// points) where the doubled ladder still pays it as an Ok.
        /// </summary>
        [Test]
        public void TheScaleDecidesWhatAPressIsClassifiedAs()
        {
            // delta +401: one millisecond outside the unscaled Great window, well inside the
            // doubled one (400 -> 800).
            Assert.AreEqual(JudgementType.Ok, judgeFirstCell(1, 1401).Type);
            Assert.AreEqual(JudgementType.Great, judgeFirstCell(TypeBeatModEasy.WINDOW_SCALE, 1401).Type);

            // delta +2001: right off the end of the unscaled ladder (MehLate 2000), so it scores
            // nothing at all; the doubled ladder still pays it, one millisecond past its Ok edge
            // (doubled OkLate is 2000, exactly where the unscaled ladder ran out) and so as a Meh.
            var plainLate = judgeFirstCell(1, 3001);
            var easyLate = judgeFirstCell(TypeBeatModEasy.WINDOW_SCALE, 3001);

            Assert.AreEqual(JudgementType.Lagging, plainLate.Type);
            Assert.AreEqual(0, plainLate.PointsAwarded);
            Assert.AreEqual(JudgementType.Meh, easyLate.Type);
            Assert.AreEqual(50, easyLate.PointsAwarded); // Meh base 50, combo 0 before the press
        }

        /// <summary>Judge one press of 'a' (target 1000) at <paramref name="pressTime"/>.</summary>
        private static CharJudgement judgeFirstCell(double windowScale, double pressTime)
        {
            var built = engine(windowScale);
            CharJudgement? seen = null;
            built.CharJudged += judgement => seen = judgement;

            built.Update(0);
            built.Update(pressTime);
            Assert.IsTrue(built.ProcessKey('a', pressTime));
            Assert.IsNotNull(seen);

            return seen!.Value;
        }

        /// <summary>
        /// Call sites 1 and 4 of 4: the live sync readout and the one <c>BuildResults</c> computes.
        /// Both are means of <c>SyncQuality</c>, which measures a delta against the WIDEST window,
        /// so the scale has to reach them: a press 2000 ms late is exactly worthless on the unscaled
        /// ladder (q = 1 - 2000/2000) and worth half on the doubled one (q = 1 - 2000/4000). Getting
        /// this wrong would grade a press Great while telling the player its timing scored zero, and
        /// SyncPercent gates the letter grade.
        /// </summary>
        [Test]
        public void TheScaleReachesBothSyncReadouts()
        {
            var plain = engine();
            plain.Update(0);
            plain.Update(3000);
            Assert.IsTrue(plain.ProcessKey('a', 3000)); // delta +2000

            var easy = engine(TypeBeatModEasy.WINDOW_SCALE);
            easy.Update(0);
            easy.Update(3000);
            Assert.IsTrue(easy.ProcessKey('a', 3000));

            // One resolved timed cell either way, so the live mean is that one cell's quality.
            Assert.AreEqual(0.0, plain.LiveSyncPercent, 1e-9);
            Assert.AreEqual(50.0, easy.LiveSyncPercent, 1e-9);

            // Seal the line: 'b' is never typed, so the final mean divides both by 2 TIMED cells.
            plain.Update(20000);
            easy.Update(20000);

            Assert.AreEqual(0.0, plain.BuildResults().SyncPercent, 1e-9);
            Assert.AreEqual(25.0, easy.BuildResults().SyncPercent, 1e-9);
        }

        #endregion

        #region The mod

        [Test]
        public void ReportsRankedDifficultyReductionModWithEzAcronym()
        {
            var mod = new TypeBeatModEasy();

            Assert.AreEqual("Easy", mod.Name);
            Assert.AreEqual("EZ", mod.Acronym);
            Assert.AreEqual(ModType.DifficultyReduction, mod.Type);
            Assert.IsTrue(mod.Ranked, "Easy is a priced handicap, not a cheat; its scores must reach the leaderboards.");
            Assert.AreEqual("Twice as long to hit every character.", mod.Description.ToString());
            Assert.IsTrue(mod.HasImplementation);
            Assert.IsNotNull(mod.Icon);
            Assert.AreEqual(2.0, TypeBeatModEasy.WINDOW_SCALE, 1e-9);
        }

        [Test]
        public void RulesetSurfacesEasyUnderDifficultyReduction()
        {
            var ruleset = new TypeBeatRuleset();

            Assert.IsTrue(ruleset.GetModsFor(ModType.DifficultyReduction).Any(m => m is TypeBeatModEasy),
                "Easy must be offered in the mod-select overlay under Difficulty Reduction.");

            var acronyms = ruleset.AllMods.Select(m => m.Acronym).ToList();

            Assert.AreEqual(acronyms.Count, acronyms.Distinct().Count(), "two mods share an acronym");
            Assert.AreEqual(1, acronyms.Count(a => a == "EZ"));
        }

        /// <summary>
        /// osu's Easy is scored at 0.5x, the same value No Fail carries here, and the obsolete
        /// self-report agrees with the authoritative calculator. Stacking must compose rather than
        /// absorb.
        /// </summary>
        [Test]
        public void ScoreMultiplierIsOsuValueOfAHalf()
        {
            var calculator = new TypeBeatScoreMultiplierCalculator(
                new ScoreMultiplierContext(new BeatmapDifficulty()));

            Assert.AreEqual(0.5, calculator.CalculateFor(new Mod[] { new TypeBeatModEasy() }), 1e-9);

            // 0.5 * 1.05 = 0.525.
            Assert.AreEqual(0.525, calculator.CalculateFor(new Mod[] { new TypeBeatModEasy(), new TypeBeatModLiterate() }), 1e-9);

#pragma warning disable CS0618 // Member is obsolete
            Assert.AreEqual(0.5, new TypeBeatModEasy().ScoreMultiplier, 1e-9);
#pragma warning restore CS0618
        }

        /// <summary>
        /// pp: Easy is a flat 0.75, applied once however many times it appears, and orthogonal to
        /// everything else in the table (0.75 * 0.90 for a No Fail stack).
        /// </summary>
        [Test]
        public void PerformancePointsPriceEasyAtThreeQuarters()
        {
            Assert.AreEqual(0.75, PerformancePoints.ModMultiplier(new Mod[] { new TypeBeatModEasy() }, 500), 1e-9);
            Assert.AreEqual(0.675, PerformancePoints.ModMultiplier(new Mod[] { new TypeBeatModEasy(), new TypeBeatModNoFail() }, 500), 1e-9);
            Assert.AreEqual(0.75, PerformancePoints.ModMultiplier(new Mod[] { new TypeBeatModEasy(), new TypeBeatModEasy() }, 500), 1e-9);
        }

        /// <summary>
        /// osu's <see cref="ModEasy"/> declares <see cref="ModHardRock"/> and
        /// <see cref="ModDifficultyAdjust"/> incompatible; neither has a type!beat implementation,
        /// so the list is decided against the mods this ruleset actually offers. Nothing it offers
        /// conflicts with a wider window, and the Hard Rock entry is a deliberate inert seam for the
        /// mod that tightens the same windows.
        /// </summary>
        [Test]
        public void ComposesWithEveryModTheRulesetActuallyOffers()
        {
            var ruleset = new TypeBeatRuleset();
            var easy = new TypeBeatModEasy();

            Assert.AreEqual(new[] { typeof(ModHardRock) }, easy.IncompatibleMods);

            foreach (var other in ruleset.AllMods.OfType<Mod>().Where(m => m is not TypeBeatModEasy))
            {
                // Autoplay-style mods are exclusive of each other, not of Easy, so test the pair.
                Assert.IsFalse(easy.IncompatibleMods.Any(t => t.IsInstanceOfType(other)),
                    $"Easy declares {other.Acronym} incompatible");
                Assert.IsFalse(other.IncompatibleMods.Any(t => t.IsInstanceOfType(easy)),
                    $"{other.Acronym} declares Easy incompatible");
            }

            Assert.IsTrue(ModUtils.CheckCompatibleSet(new Mod[] { new TypeBeatModEasy(), new TypeBeatModHalfTime() }));
            Assert.IsTrue(ModUtils.CheckCompatibleSet(new Mod[] { new TypeBeatModEasy(), new TypeBeatModDoubleTime() }));
            Assert.IsTrue(ModUtils.CheckCompatibleSet(new Mod[] { new TypeBeatModEasy(), new TypeBeatModNoFail(), new TypeBeatModLiterate() }));
        }

        /// <summary>
        /// osu's Easy halves CircleSize, ApproachRate and DrainRate. type!beat has none of those, so
        /// the inherited behaviour is overridden away rather than left to move three numbers nothing
        /// reads.
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

            new TypeBeatModEasy().ApplyToDifficulty(difficulty);

            Assert.AreEqual(5, difficulty.CircleSize, 1e-9);
            Assert.AreEqual(6, difficulty.ApproachRate, 1e-9);
            Assert.AreEqual(7, difficulty.DrainRate, 1e-9);
            Assert.AreEqual(8, difficulty.OverallDifficulty, 1e-9);
        }

        /// <summary>
        /// A replay carries KEYSTROKES and is re-judged from scratch, so the recalculation path has
        /// to apply the same window scale the live run did or every stored Easy score reprices on
        /// the wrong ladder. Three cells struck 500 ms late: an Ok apiece unscaled, a Great apiece
        /// under Easy.
        /// </summary>
        [Test]
        public void AReplayIsReJudgedOnTheModdedLadder()
        {
            var map = replayMap();

            // Cell targets 0, 4000, 8000 (three chars evenly over the unit's [0, 12000]).
            var frames = new List<TypeBeatReplayFrame> { TypeBeatReplayFrame.CreateConfigFrame(0, true) };

            for (int i = 0; i < 3; i++)
                frames.Add(new TypeBeatReplayFrame(i * 4000 + 500, "abc"[i]));

            var replay = new Replay();
            replay.Frames.AddRange(frames);

            var plain = TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), replay, TypoRule.Deferred, ComboRestoreRule.OnFix);
            var easy = TypeBeatReplayScorer.Score(map, new Mod[] { new TypeBeatModEasy() }, replay, TypoRule.Deferred, ComboRestoreRule.OnFix);

            Assert.AreEqual(3, plain.Statistics.GetValueOrDefault(HitResult.Ok));
            Assert.AreEqual(0, plain.Statistics.GetValueOrDefault(HitResult.Great));

            Assert.AreEqual(0, easy.Statistics.GetValueOrDefault(HitResult.Ok));
            Assert.AreEqual(3, easy.Statistics.GetValueOrDefault(HitResult.Great));
        }

        #endregion
    }
}
