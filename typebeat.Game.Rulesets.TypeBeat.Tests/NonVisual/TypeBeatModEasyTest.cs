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

        /// <summary>
        /// ONE two-syllable word, "ap|ple": the unit is sung over [1000, 3000] and the mapper timed
        /// the interior boundary at 2000, so syllable 0 is [1000, 2000] and syllable 1 is
        /// [2000, 3000]. The even-by-index target spread puts cell 2 ('p', syllable 1's FIRST
        /// character) at 1800, 200 ms before its own syllable opens, which is what makes the two
        /// shelter readings disagree about a press between them.
        /// </summary>
        private static LyricLine shelterLine() => new LyricLine
        {
            RawText = "apple",
            StartTime = 0,
            EndTime = 20000,
            SingEndTime = 3000,
            Units = new[]
            {
                new TimedUnit
                {
                    Text = "apple",
                    StartTime = 1000,
                    EndTime = 3000,
                    SyllableBoundaries = new[] { 2000.0 },
                },
            },
        };

        /// <summary>The shelter fixture for the engine path.</summary>
        private static LyricBeatmap shelterMap() => new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata
            {
                Artist = "Test",
                Title = "Song",
                FolderPath = @"X:\nowhere",
                AudioFileName = "a.mp3",
            },
            Lines = new[] { shelterLine() },
            Granularity = TimingGranularity.Syllable,
        };

        /// <summary>The shelter fixture for the replay path.</summary>
        private static TypeBeatBeatmap shelterReplayMap()
        {
            var line = shelterLine();

            var map = new TypeBeatBeatmap();
            map.HitObjects.Add(new TypeBeatHitObject { StartTime = 0, LineIndex = 0, Line = line, Granularity = TimingGranularity.Syllable });

            foreach (var hitObject in map.HitObjects)
                hitObject.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty(), CancellationToken.None);

            return map;
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
        /// ladder's symmetry and unit scale survive (there is ONE ladder to scale), and a factor of
        /// 1 is not merely equal to the unscaled ladder but IS it, so the default path allocates
        /// nothing and grades against the very objects it graded against before the scale existed.
        /// </summary>
        [Test]
        public void ScalingAWindowSetMultipliesEveryBoundAndKeepsUnitScaleIdentical()
        {
            var line = SyncWindows.Default;

            Assert.AreSame(line, line.Scaled(1));

            var doubled = line.Scaled(2);

            Assert.AreEqual(2.0, doubled.Scale, 1e-9);
            Assert.AreEqual(300, doubled.GreatEarly, 1e-9);   // 150 * 2
            Assert.AreEqual(300, doubled.GreatLate, 1e-9);    // 150 * 2, symmetric
            Assert.AreEqual(600, doubled.OkEarly, 1e-9);      // 300 * 2
            Assert.AreEqual(600, doubled.OkLate, 1e-9);
            Assert.AreEqual(1200, doubled.MehEarly, 1e-9);    // 600 * 2
            Assert.AreEqual(1200, doubled.MehLate, 1e-9);

            // There is ONE ladder, so a doubled one is the only thing a scale can produce: the
            // three granularity tiers this used to have to scale separately are gone.
        }

        /// <summary>
        /// Two scalings compose by multiplication, so the ladder is the same whichever order the
        /// mods that ask for them are applied in. This is what backlog 150 consumes: a rate mod
        /// multiplies its own factor in on top of Easy's.
        /// </summary>
        [Test]
        public void WindowScalesComposeMultiplicativelyAndCommute()
        {
            var line = SyncWindows.Default;

            Assert.AreEqual(line.Scaled(2).Scaled(1.5).MehLate, line.Scaled(1.5).Scaled(2).MehLate, 1e-9);
            Assert.AreEqual(line.Scaled(3).MehLate, line.Scaled(2).Scaled(1.5).MehLate, 1e-9);

            // And a scale that undoes another lands exactly back on the base ladder.
            Assert.AreEqual(line.MehLate, line.Scaled(2).Scaled(0.5).MehLate, 1e-9);
        }

        [Test]
        public void AWindowScaleMustBeFiniteAndPositive()
        {
            var line = SyncWindows.Default;

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
            Assert.AreSame(SyncWindows.Default, built.Windows);
        }

        /// <summary>
        /// Call sites 2 and 3 of 4: the two <c>Classify</c> calls. A press 151 ms late is one
        /// millisecond outside the unscaled Great window (150) and comfortably inside the doubled
        /// one (300), and a press 601 ms late falls off the unscaled ladder entirely (Lagging, 0
        /// points) where the doubled ladder still pays it as a Meh (its Ok edge is 600).
        /// </summary>
        [Test]
        public void TheScaleDecidesWhatAPressIsClassifiedAs()
        {
            // delta +151: one millisecond outside the unscaled Great window (150), inside the
            // doubled one (300).
            Assert.AreEqual(JudgementType.Ok, judgeFirstCell(1, 1151).Type);
            Assert.AreEqual(JudgementType.Great, judgeFirstCell(TypeBeatModEasy.WINDOW_SCALE, 1151).Type);

            // delta +601: right off the end of the unscaled ladder (MehLate 600), so it scores
            // nothing at all; the doubled ladder still pays it as a Meh, because its Ok edge is
            // 600 and its Meh edge is 1200.
            var plainLate = judgeFirstCell(1, 1601);
            var easyLate = judgeFirstCell(TypeBeatModEasy.WINDOW_SCALE, 1601);

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
        /// THE SHELTER (the user's decision): under Easy a press is measured against the span of its
        /// whole WORD rather than its own syllable's, so the window scale is only half of what the
        /// mod does. The fixture's syllable 1 is sung over [2000, 3000] and cell 2 opens it; a press
        /// at 1500 is 500 ms before that span opens (a Meh on the one ladder) and well inside the
        /// WORD [1000, 3000], which Easy calls dead on. Both arms run the live span stack, and the
        /// only difference between them is the mod's own two halves: the shelter and the scale.
        /// </summary>
        [Test]
        public void EasySheltersTheWholeWordRatherThanTheSyllable()
        {
            CharJudgement plain = judgeShelterPress(wordShelter: false);
            CharJudgement easy = judgeShelterPress(wordShelter: true);

            Assert.AreEqual(-500, plain.Delta, 1e-9, "the syllable rule: 500 ms before its own syllable opens");
            Assert.AreEqual(JudgementType.Meh, plain.Type);

            Assert.AreEqual(0, easy.Delta, 1e-9, "the word shelter: inside the word that syllable is sung in");
            Assert.AreEqual(JudgementType.Great, easy.Type);
        }

        /// <summary>
        /// Press cells 0..1 of the shelter fixture dead on their targets, then judge cell 2 at 1500
        /// with the live span stack either side of the mod's shelter.
        /// </summary>
        private static CharJudgement judgeShelterPress(bool wordShelter)
        {
            var built = new TypingEngine(shelterMap())
            {
                SyllableTiming = true,
                CharTimedStretch = true,
                FirstCharTiming = true,
                WordShelter = wordShelter,
                WindowScale = wordShelter ? TypeBeatModEasy.WINDOW_SCALE : 1,
            };

            CharJudgement? seen = null;
            built.CharJudged += judgement => seen = judgement;
            built.Update(0);

            Assert.IsTrue(built.ProcessKey('a', 1000));
            Assert.IsTrue(built.ProcessKey('p', 1400));
            Assert.IsTrue(built.ProcessKey('p', 1500));

            Assert.IsNotNull(seen);

            return seen!.Value;
        }

        /// <summary>
        /// Call sites 1 and 4 of 4: the live sync readout and the one <c>BuildResults</c> computes.
        /// Both are means of <c>SyncQuality</c>, which measures a delta against the WIDEST window,
        /// so the scale has to reach them: a press 600 ms late is exactly worthless on the unscaled
        /// ladder (q = 1 - 600/600) and worth half on the doubled one (q = 1 - 600/1200). Getting
        /// this wrong would grade a press Great while telling the player its timing scored zero.
        /// Backlog 251 made both readouts display-only (the letter grade is accuracy alone now), so
        /// what is at stake is the readout's honesty rather than a grade, which is reason enough:
        /// they are shown to a player who deliberately asked to see them.
        /// </summary>
        [Test]
        public void TheScaleReachesBothSyncReadouts()
        {
            var plain = engine();
            plain.Update(0);
            plain.Update(1600);
            Assert.IsTrue(plain.ProcessKey('a', 1600)); // delta +600

            var easy = engine(TypeBeatModEasy.WINDOW_SCALE);
            easy.Update(0);
            easy.Update(1600);
            Assert.IsTrue(easy.ProcessKey('a', 1600));

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
        /// pp: Easy is a flat term, applied once however many times it appears, and orthogonal to
        /// everything else in the table (the value times 0.90 for a No Fail stack). The value is the
        /// PP Sandbox's live dial, which is where it is chosen; this pins it so a change to it has to
        /// come through here.
        /// </summary>
        [Test]
        public void PerformancePointsPriceEasyAtItsOwnDial()
        {
            Assert.AreEqual(0.85, PerformancePoints.ModMultiplier(new Mod[] { new TypeBeatModEasy() }, 500), 1e-9);
            Assert.AreEqual(0.765, PerformancePoints.ModMultiplier(new Mod[] { new TypeBeatModEasy(), new TypeBeatModNoFail() }, 500), 1e-9);
            Assert.AreEqual(0.85, PerformancePoints.ModMultiplier(new Mod[] { new TypeBeatModEasy(), new TypeBeatModEasy() }, 500), 1e-9);
        }

        /// <summary>
        /// osu's <see cref="ModEasy"/> declares <see cref="ModHardRock"/> and
        /// <see cref="ModDifficultyAdjust"/> incompatible; the second has no type!beat
        /// implementation and can never have one, so the list is decided against the mods this
        /// ruleset actually offers. Nothing it offers conflicts with a wider window EXCEPT Hard
        /// Rock, which backlog 150 gave the ruleset: the entry 149 left here as an inert seam is
        /// now live, and the exclusion fires from both sides without this file being reopened for
        /// anything but its test.
        /// </summary>
        [Test]
        public void ComposesWithEveryModTheRulesetActuallyOffers()
        {
            var ruleset = new TypeBeatRuleset();
            var easy = new TypeBeatModEasy();

            Assert.AreEqual(new[] { typeof(ModHardRock) }, easy.IncompatibleMods);

            // The one real exclusion, stated from both ends: the two scale the same windows in
            // opposite directions, so a stack holding both would be a no-op ladder priced as two
            // difficulty adjustments.
            Assert.IsFalse(ModUtils.CheckCompatibleSet(new Mod[] { new TypeBeatModEasy(), new TypeBeatModHardRock() }));

            foreach (var other in ruleset.AllMods.OfType<Mod>().Where(m => m is not TypeBeatModEasy and not TypeBeatModHardRock))
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

            // 200 ms late: Ok on the plain ladder (inside 300), Great on the doubled one (600).
            for (int i = 0; i < 3; i++)
                frames.Add(new TypeBeatReplayFrame(i * 4000 + 200, "abc"[i]));

            var replay = new Replay();
            replay.Frames.AddRange(frames);

            var plain = TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), replay, TypoRule.Deferred, ComboRestoreRule.OnFix);
            var easy = TypeBeatReplayScorer.Score(map, new Mod[] { new TypeBeatModEasy() }, replay, TypoRule.Deferred, ComboRestoreRule.OnFix);

            Assert.AreEqual(3, plain.Statistics.GetValueOrDefault(HitResult.Ok));
            Assert.AreEqual(0, plain.Statistics.GetValueOrDefault(HitResult.Great));

            Assert.AreEqual(0, easy.Statistics.GetValueOrDefault(HitResult.Ok));
            Assert.AreEqual(3, easy.Statistics.GetValueOrDefault(HitResult.Great));
        }

        /// <summary>
        /// The other half of the mod's arm, and the reason a stored Easy row still re-derives: the
        /// shelter travels in the MOD LIST rather than in a CONFIG bit, so a re-derivation reads the
        /// same span the live run was graded against. The same five keystrokes, judged on the live
        /// span stack and again on the stored-era flags word with the mod still on the score: the
        /// off-syllable press is a Great under Easy (its word is still being sung) and a Meh
        /// without it, and the two Easy accounts are identical.
        /// </summary>
        [Test]
        public void AnEasyReplayReDerivesOnTheWordShelterItWasPlayedWith()
        {
            var map = shelterReplayMap();

            var frames = new List<TypeBeatReplayFrame>
            {
                TypeBeatReplayFrame.CreateConfigFrame(0, true, syllableTiming: true, charTimedStretch: true, firstCharTiming: true),
                new TypeBeatReplayFrame(1000, 'a'),
                new TypeBeatReplayFrame(1400, 'p'),
                new TypeBeatReplayFrame(1500, 'p'), // 500 ms before its own syllable opens, inside the word
                new TypeBeatReplayFrame(2200, 'l'),
                new TypeBeatReplayFrame(2600, 'e'),
            };

            var replay = new Replay();
            replay.Frames.AddRange(frames);

            var plain = TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), replay, TypoRule.Deferred, ComboRestoreRule.OnFix);
            var easy = TypeBeatReplayScorer.Score(map, new Mod[] { new TypeBeatModEasy() }, replay, TypoRule.Deferred, ComboRestoreRule.OnFix);
            var stored = TypeBeatReplayScorer.Score(map, new Mod[] { new TypeBeatModEasy() }, replay, TypoRule.Deferred, ComboRestoreRule.OnFix,
                SpaceTimingRule.Timed, RateWindowRule.Unscaled);

            Assert.AreEqual(4, plain.Statistics.GetValueOrDefault(HitResult.Great), "the syllable shelter marks the early press down");
            Assert.AreEqual(1, plain.Statistics.GetValueOrDefault(HitResult.Meh));
            Assert.AreEqual(0, plain.Statistics.GetValueOrDefault(HitResult.Miss));

            Assert.AreEqual(5, easy.Statistics.GetValueOrDefault(HitResult.Great), "the word shelter pays it");
            Assert.AreEqual(0, easy.Statistics.GetValueOrDefault(HitResult.Meh));
            Assert.AreEqual(0, easy.Statistics.GetValueOrDefault(HitResult.Miss));

            // Neither era setting reaches the shelter: it is a mod arm, so the stored row's own
            // flags word still lands on the span the live run used.
            Assert.AreEqual(easy.Statistics, stored.Statistics);
            Assert.AreEqual(easy.TotalScore, stored.TotalScore);
            Assert.AreEqual(easy.Accuracy, stored.Accuracy, 1e-9);
        }

        #endregion
    }
}
