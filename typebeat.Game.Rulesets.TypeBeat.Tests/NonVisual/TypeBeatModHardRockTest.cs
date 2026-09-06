// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Backlog 150 shipped the Hard Rock mod as a HALVING of every judgement window, the exact mirror of
// Easy on the general window scale backlog 149 built (TypingEngine.WindowScale), and backlog 180
// gave it a second half: the revert to the classic per-character point targets, because backlog
// 179's syllable-span rule (delta 0 anywhere inside the sung span) undercuts the halved windows.
//
// Backlog 264 dropped the halving, by the user's decision: stacked, the two made the mod unplayable
// for nearly everyone, and the mod is now the judgement revert alone at NORMAL windows. What the
// halving became is the ERA every stored HR row was played under, carried by the replay's own CONFIG
// frame (bit 13, TypingEngine.UnhalvedHardRockWindows) and paired with the mod fact
// (TypingEngine.HardRockFromMod), which is the only thing a frame cannot say for itself.
//
// So this fixture covers: the shipping surface (acronym, type, ranked flag, score multiplier, pp),
// the one incompatibility, the score-multiplier headroom the server's stack cap depends on, the
// live judgement revert, the stored halved ladder, and the era seam that keeps the two apart.

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
using typebeat.Game.Rulesets.TypeBeat.UI;
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

        /// <summary>
        /// "cake" (backlog 179's own fixture): ONE word and ONE syllable, the final e being silent,
        /// sung over [1000, 3000]. The flat char ramp puts the point targets at 1000/1500/2000/2500
        /// and the group's span is [1000, 3000], so every target sits inside it. That is the shape
        /// backlog 180 needs: a press can be IN SPAN and OFF TARGET at the same time, which is the
        /// only way the two rules are distinguishable.
        /// </summary>
        private static TypeBeatBeatmap cakeMap()
        {
            var line = new LyricLine
            {
                RawText = "cake",
                StartTime = 0,
                EndTime = 60000,
                SingEndTime = 3000,
                Units = new[] { new TimedUnit { Text = "cake", StartTime = 1000, EndTime = 3000 } },
            };

            var map = new TypeBeatBeatmap();
            map.HitObjects.Add(new TypeBeatHitObject { StartTime = 0, LineIndex = 0, Line = line, Granularity = TimingGranularity.Line });

            foreach (var hitObject in map.HitObjects)
                hitObject.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty(), CancellationToken.None);

            return map;
        }

        /// <summary>
        /// A drawable ruleset built over <see cref="cakeMap"/> exactly as gameplay builds it, mods
        /// and all, then handed the mods the framework's <c>applyRulesetMods</c> would hand it. It
        /// is never loaded into a hierarchy and does not need to be: the engine is a lazy property
        /// off the constructor's beatmap and mod list, which is precisely why backlog 180 decides
        /// the judgement ERA there rather than from an <c>ApplyToDrawableRuleset</c> seam.
        ///
        /// <para>Since backlog 264 Hard Rock has no such seam at all: it no longer implements
        /// <see cref="IApplicableToDrawableRuleset{T}"/>, so the loop below is a no-op for it and
        /// everything the mod does to the engine is decided in <c>createEngine</c>. Easy still uses
        /// the seam, which is what the loop is for.</para>
        /// </summary>
        private static TypingEngine liveEngine(params Mod[] mods)
        {
            var drawable = new DrawableTypeBeatRuleset(new TypeBeatRuleset(), cakeMap(), mods);

            foreach (var mod in mods.OfType<IApplicableToDrawableRuleset<TypeBeatHitObject>>())
                mod.ApplyToDrawableRuleset(drawable);

            return drawable.Engine;
        }

        /// <summary>Press one char and hand back the judgement it raised.</summary>
        private static CharJudgement press(TypingEngine engine, char character, double time)
        {
            CharJudgement? seen = null;
            Action<CharJudgement> capture = judgement => seen = judgement;

            engine.CharJudged += capture;

            engine.Update(time);
            Assert.IsTrue(engine.ProcessKey(character, time));
            Assert.IsNotNull(seen);

            engine.CharJudged -= capture;

            return seen!.Value;
        }

        #endregion

        [Test]
        public void ReportsRankedDifficultyIncreaseModWithHrAcronym()
        {
            var mod = new TypeBeatModHardRock();

            Assert.AreEqual("Hard Rock", mod.Name);
            Assert.AreEqual("HR", mod.Acronym);
            Assert.AreEqual(ModType.DifficultyIncrease, mod.Type);
            Assert.IsTrue(mod.Ranked, "Hard Rock is a priced handicap; its scores must reach the leaderboards.");
            Assert.AreEqual("Every character is timed on its own, not on the syllable around it.", mod.Description.ToString());
            Assert.IsTrue(mod.HasImplementation);
            Assert.IsNotNull(mod.Icon);

            // 0.5 is now the ERA constant and nothing else: backlog 264 retired the live halving, so
            // no mod and no engine factory writes it. It survives because the rows already on the
            // leaderboards were played against it, and TypingEngine reads it for exactly those.
            Assert.AreEqual(0.5, TypeBeatModHardRock.WINDOW_SCALE, 1e-9);

            // And the mod no longer touches the live windows at all, which is why it dropped the
            // drawable-ruleset seam that used to multiply that constant in.
            Assert.IsNotInstanceOf<IApplicableToDrawableRuleset<TypeBeatHitObject>>(mod);
        }

        /// <summary>
        /// EASY IS FULLY INDEPENDENT AND WAS NOT TOUCHED BY BACKLOG 264. Its 2.0 was never derived
        /// from Hard Rock's 0.5 (the reciprocal was a design symmetry, not a dependency), and it
        /// still reaches the live engine from its own <c>ApplyToDrawableRuleset</c>. Pinned here
        /// rather than only in <c>TypeBeatModEasyTest</c> because this is the file that changed.
        /// </summary>
        [Test]
        public void EasyKeepsItsOwnDoubledWindowsAndItsOwnLiveSeam()
        {
            Assert.AreEqual(2.0, TypeBeatModEasy.WINDOW_SCALE, 1e-9);
            Assert.IsInstanceOf<IApplicableToDrawableRuleset<TypeBeatHitObject>>(new TypeBeatModEasy());

            // The live ladder itself: Easy doubles it and Hard Rock, since 264, leaves it alone.
            double plain = liveEngine().Windows.GreatLate;

            Assert.AreEqual(2.0, liveEngine(new TypeBeatModEasy()).Windows.GreatLate / plain, 1e-9);
            Assert.AreEqual(1.0, liveEngine(new TypeBeatModHardRock()).Windows.GreatLate / plain, 1e-9);
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
        /// THE STORED ERA'S LADDER, which every Hard Rock row on the leaderboards was graded on
        /// (backlog 150 to 264): a press 201 ms late is comfortably Great on the unscaled ladder
        /// (GreatLate 400) and an Ok on the halved one (200), and a press 1001 ms late is a paid Meh
        /// unscaled (MehLate 2000) but falls off the halved ladder entirely (1000) and scores nothing
        /// at all. That is a lot of grade to move, which is why the era has to travel per replay
        /// rather than be inferred from the acronym.
        /// </summary>
        [Test]
        public void TheStoredHalvedLadderDecidesWhatAPressIsClassifiedAs()
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
        /// THE LIVE LADDER SINCE BACKLOG 264: normal width, on a real drawable ruleset built the way
        /// gameplay builds it. <see cref="TypingEngine.WindowScale"/> itself is untouched (nothing
        /// multiplies into it any more) and the ladder it produces is the no-mod one, so the mod's
        /// whole handicap is the judgement revert the region at the bottom of this fixture covers.
        /// </summary>
        [Test]
        public void LivePlayNoLongerHalvesTheWindowsUnderHardRock()
        {
            var hard = liveEngine(new TypeBeatModHardRock());

            Assert.AreEqual(1.0, hard.WindowScale, 1e-9, "nothing multiplies the general scale under Hard Rock any more");
            Assert.IsTrue(hard.HardRockFromMod, "the mod fact still reaches the engine: the stored era needs it");
            Assert.IsTrue(hard.UnhalvedHardRockWindows, "a live run is the unhalved era, and records bit 13 SET");

            Assert.AreEqual(liveEngine().Windows.GreatLate, hard.Windows.GreatLate, 1e-9);
            Assert.AreEqual(liveEngine().Windows.MehLate, hard.Windows.MehLate, 1e-9);

            // And the pair really is what decides it: clear the era bit on the same engine and the
            // halved ladder is back, with no WindowScale write anywhere.
            hard.UnhalvedHardRockWindows = false;

            Assert.AreEqual(1.0, hard.WindowScale, 1e-9);
            Assert.AreEqual(liveEngine().Windows.GreatLate * TypeBeatModHardRock.WINDOW_SCALE, hard.Windows.GreatLate, 1e-9);
        }

        /// <summary>
        /// The era through the SCORER, which is the path a stored row is re-derived on. Three cells
        /// struck 300 ms late, judged on point targets under both arms: a Great apiece at normal
        /// windows (GreatLate 400) and an Ok apiece on the halved ladder (GreatLate 200, OkLate 500).
        /// A stored HR run carries bit 13 CLEAR and gets the second; a run recorded by today's client
        /// carries it SET and gets the first, off the SAME keystrokes and the SAME mod list.
        /// </summary>
        [Test]
        public void AStoredHardRockReplayKeepsTheHalvedLadderAndALiveOneJudgesAtNormalWindows()
        {
            var map = replayMap();

            // Cell targets 0, 4000, 8000 (three chars evenly over the unit's [0, 12000]).
            Replay run(bool unhalved)
            {
                var replay = new Replay();
                replay.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, true, unhalvedHardRockWindows: unhalved));

                for (int i = 0; i < 3; i++)
                    replay.Frames.Add(new TypeBeatReplayFrame(i * 4000 + 300, "abc"[i]));

                return replay;
            }

            Mod[] hardRock = { new TypeBeatModHardRock() };

            var plain = TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), run(false), TypoRule.Deferred, ComboRestoreRule.OnFix);
            var stored = TypeBeatReplayScorer.Score(map, hardRock, run(false), TypoRule.Deferred, ComboRestoreRule.OnFix);
            var live = TypeBeatReplayScorer.Score(map, hardRock, run(true), TypoRule.Deferred, ComboRestoreRule.OnFix);

            Assert.AreEqual(3, plain.Statistics.GetValueOrDefault(HitResult.Great));
            Assert.AreEqual(0, plain.Statistics.GetValueOrDefault(HitResult.Ok));

            Assert.AreEqual(0, stored.Statistics.GetValueOrDefault(HitResult.Great), "a stored HR row keeps the ladder it was played on");
            Assert.AreEqual(3, stored.Statistics.GetValueOrDefault(HitResult.Ok));

            Assert.AreEqual(3, live.Statistics.GetValueOrDefault(HitResult.Great), "a run recorded today is judged at normal windows");
            Assert.AreEqual(0, live.Statistics.GetValueOrDefault(HitResult.Ok));

            // The bit is the only difference, so the newer arm judges exactly as the no-mod run
            // does. The TOTALS differ by the mod's own 1.10x score multiplier, which 264 did not
            // touch, so the comparison is on accuracy and the statistics that produce it.
            Assert.AreEqual(plain.Accuracy, live.Accuracy, 1e-12);
            Assert.AreEqual(plain.TotalScore * 1.10, live.TotalScore, 1);
            Assert.Less(stored.TotalScore, live.TotalScore, "the halved ladder prices the same keystrokes lower");

            // And the bit is inert without the mod: a no-mod score reads the same either way.
            var plainUnhalved = TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), run(true), TypoRule.Deferred, ComboRestoreRule.OnFix);

            Assert.AreEqual(plain.TotalScore, plainUnhalved.TotalScore);
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

        #region Backlog 180: Hard Rock reverts the judgement rule to point targets

        /// <summary>
        /// The flag itself, read off a real drawable ruleset built the way gameplay builds it: HR
        /// and only HR turns the syllable rule off. Easy is explicitly NOT symmetric here, which is
        /// the one place the Easy/Hard Rock mirror does not hold: widening the windows is a help,
        /// and the syllable rule is already a help, so they compose instead of cancelling.
        /// </summary>
        [Test]
        public void OnlyHardRockBuildsTheEngineOnTheClassicRule()
        {
            Assert.IsFalse(liveEngine(new TypeBeatModHardRock()).SyllableTiming,
                "Hard Rock must judge on per-character point targets, or the halved windows grade almost nothing.");

            Assert.IsTrue(liveEngine().SyllableTiming, "a no-mod play keeps the backlog 179 syllable rule");
            Assert.IsTrue(liveEngine(new TypeBeatModEasy()).SyllableTiming, "Easy keeps it");
            Assert.IsTrue(liveEngine(new TypeBeatModDoubleTime()).SyllableTiming, "a rate mod keeps it");
            Assert.IsTrue(liveEngine(new TypeBeatModLiterate(), new TypeBeatModFlashlight()).SyllableTiming, "the rest of the stack keeps it");

            // Composed with something else, HR still wins: the arm is "any HR in the list".
            Assert.IsFalse(liveEngine(new TypeBeatModDoubleTime(), new TypeBeatModHardRock()).SyllableTiming);
        }

        /// <summary>
        /// The rule where it can be seen: 'a' pressed at 2000 is INSIDE the span "cake" is sung
        /// over ([1000, 3000]) and 500 ms past its own point target (1500). Under every other stack
        /// that is delta 0 and a Great; under Hard Rock it is delta 500, which the NORMAL ladder
        /// prices as an Ok (its Great window ends 400 ms late, its Ok window 1000). One press, two
        /// rules, and the mod is the only difference between the two engines.
        ///
        /// <para>500 rather than backlog 180's 300: at the halved windows the mod used to carry, 300
        /// was already past the Great window, and since backlog 264 it is not. The point of the test
        /// is the RULE, so the delta was moved into the Ok tier of the ladder the mod now judges on
        /// rather than left where it would quietly assert a Great.</para>
        ///
        /// <para>The demonstrating cell is 'a', the span's second char, and deliberately NOT 'c':
        /// since backlog 247 the FIRST char of a syllable is judged on its distance from the span's
        /// start under the live stack too, and "cake" opens on 'c''s own target, so a press on 'c'
        /// would grade the same number under both rules and prove nothing. 'c' is pressed dead on
        /// first (delta 0 under both) purely to hand the caret over.</para>
        /// </summary>
        [Test]
        public void AnInSpanOffTargetPressIsJudgedOnItsPointTargetUnderHardRock()
        {
            const double press_time = 2000;

            var hard = liveEngine(new TypeBeatModHardRock());
            var plain = liveEngine();

            // The press really is in span, so this is a test of the RULE and not of a press that
            // would have been graded the same either way.
            var line = hard.Lines[0];
            var span = line.Syllables[line.SyllableIndexOf(1)];

            Assert.AreEqual(1000, span.StartTime, 1e-9);
            Assert.AreEqual(3000, span.EndTime, 1e-9);
            Assert.AreEqual(1500, line.Cells[1].TargetTime, 1e-9);
            Assert.GreaterOrEqual(press_time, span.StartTime);
            Assert.LessOrEqual(press_time, span.EndTime);

            Assert.AreEqual(0, press(hard, 'c', 1000).Delta, 1e-9);
            Assert.AreEqual(0, press(plain, 'c', 1000).Delta, 1e-9);

            var hardJudgement = press(hard, 'a', press_time);
            var plainJudgement = press(plain, 'a', press_time);

            Assert.AreEqual(500, hardJudgement.Delta, 1e-9, "Hard Rock judges the distance to the point target");
            Assert.AreEqual(JudgementType.Ok, hardJudgement.Type);

            Assert.AreEqual(0, plainJudgement.Delta, 1e-9, "every other stack judges the distance to the sung span");
            Assert.AreEqual(JudgementType.Great, plainJudgement.Type);

            // Stored, not just announced, so every readout that re-reads JudgedDelta agrees.
            Assert.AreEqual(500, hard.Lines[0].Cells[1].JudgedDelta!.Value, 1e-9);
            Assert.AreEqual(0, plain.Lines[0].Cells[1].JudgedDelta!.Value, 1e-9);
        }

        /// <summary>
        /// BOTH eras survive the round trip with no scorer change, which is the whole reason the
        /// flags are decided at engine construction: the recorder stamps the LIVE engine's flags into
        /// the CONFIG frame, so an HR run records bit 2 CLEAR (point targets, forever) and, since
        /// backlog 264, bit 13 SET (normal windows). Fed back through <see cref="ReplayEngineFeed"/>
        /// the run reproduces bit-exactly, cell states, stored deltas, score, combo and accuracy
        /// alike.
        ///
        /// <para>The window half needs the mod as well as the bit, because a score's mods are not in
        /// its frames: a watching client sets <see cref="TypingEngine.HardRockFromMod"/> off the
        /// score and the frame supplies the era, exactly as bit 5 pairs with
        /// <see cref="TypingEngine.FlexibleCaretFromMod"/>.</para>
        /// </summary>
        [Test]
        public void AHardRockRunRecordsBothErasAndReDerivesBitExact()
        {
            var live = liveEngine(new TypeBeatModHardRock());

            // Four presses, every one of them inside the span "cake" is sung over ([1000, 3000]) and
            // every one off its own point target (1000/1500/2000/2500) by a different amount, so the
            // two judgement eras cannot agree about any of them. Deltas +300, +600, +900, +450.
            // Cross-checks against the LIVE ladder (Great [-250, 400], Ok [-600, 1000]): Great, Ok,
            // Ok, Ok. Against the STORED halved one (Great [-125, 200], Ok [-300, 500],
            // Meh [-600, 1000]): Ok, Meh, Meh, Ok, which is strictly worse, and what the last arm
            // below re-derives.
            (double time, char character)[] presses = { (1300, 'c'), (2100, 'a'), (2900, 'k'), (2950, 'e') };

            // Exactly what TypeBeatReplayRecorder writes: one CONFIG header off the live engine's
            // own settings, ahead of the first input, then one frame per effective input.
            var frames = new List<TypeBeatReplayFrame>
            {
                TypeBeatReplayFrame.CreateConfigFrame(presses[0].time, live.AllowWrongInput, live.SpaceSkipsWord, live.SyllableTiming,
                    unhalvedHardRockWindows: live.UnhalvedHardRockWindows),
            };

            foreach ((double time, char character) in presses)
            {
                live.Update(time);
                Assert.IsTrue(live.ProcessKey(character, time));
                frames.Add(new TypeBeatReplayFrame(time, character));
            }

            Assert.IsTrue(frames[0].IsConfig);
            Assert.IsFalse(frames[0].SyllableTiming, "an HR run records flags bit 2 CLEAR: the classic judgement era");
            Assert.IsTrue(frames[0].UnhalvedHardRockWindows, "and, since backlog 264, bit 13 SET: the unhalved window era");

            // The live run really was graded on point deltas, so the comparison below is not two
            // copies of the syllable rule agreeing with each other.
            Assert.AreEqual(new double?[] { 300, 600, 900, 450 }, live.Lines[0].Cells.Select(c => c.JudgedDelta).ToArray());
            Assert.AreEqual(4, live.Lines[0].Cells.Count(c => c.JudgedDelta != null));

            // The watching engine deliberately starts in the SYLLABLE era, which is what a no-mod
            // client builds, so the frame's bit is the only thing that can put it back on point
            // targets. The MOD half of the window question arrives from the score, as it always does.
            var replayed = liveEngine();
            replayed.HardRockFromMod = true;

            Assert.IsTrue(replayed.SyllableTiming, "the watcher's own default is the live rule");

            foreach (var frame in frames)
                ReplayEngineFeed.Apply(replayed, frame);

            Assert.IsFalse(replayed.SyllableTiming, "the replay's own header wins over the watching client's era");
            Assert.IsTrue(replayed.UnhalvedHardRockWindows);

            assertSameJudgements(live, replayed);

            // And each recorded bit is load-bearing rather than decorative. Bit 2 first: the SAME
            // keystrokes under a header with it set are four in-span Greats at delta 0.
            var wrongJudgementEra = liveEngine();
            wrongJudgementEra.HardRockFromMod = true;
            frames[0] = TypeBeatReplayFrame.CreateConfigFrame(presses[0].time, live.AllowWrongInput, live.SpaceSkipsWord, true,
                unhalvedHardRockWindows: true);

            foreach (var frame in frames)
                ReplayEngineFeed.Apply(wrongJudgementEra, frame);

            Assert.AreEqual(new double?[] { 0, 0, 0, 0 }, wrongJudgementEra.Lines[0].Cells.Select(c => c.JudgedDelta).ToArray());
            Assert.AreNotEqual(live.Score, wrongJudgementEra.Score, "the two judgement eras must not score the same run alike");

            // Then bit 13: the same point deltas, re-derived on the halved ladder a stored HR row was
            // played against, are a strictly worse account of the same fingers.
            var storedWindowEra = liveEngine();
            storedWindowEra.HardRockFromMod = true;
            frames[0] = TypeBeatReplayFrame.CreateConfigFrame(presses[0].time, live.AllowWrongInput, live.SpaceSkipsWord, live.SyllableTiming);

            foreach (var frame in frames)
                ReplayEngineFeed.Apply(storedWindowEra, frame);

            Assert.IsFalse(storedWindowEra.UnhalvedHardRockWindows);
            Assert.AreEqual(new double?[] { 300, 600, 900, 450 }, storedWindowEra.Lines[0].Cells.Select(c => c.JudgedDelta).ToArray());
            Assert.Less(storedWindowEra.Score, live.Score, "the halved ladder prices the same deltas lower");

            // Re-feeding the header (which is what every backwards seek does) must not compound the
            // halving: the ladder is assigned, never multiplied in.
            double halved = storedWindowEra.Windows.GreatLate;

            for (int i = 0; i < 5; i++)
                ReplayEngineFeed.Apply(storedWindowEra, frames[0]);

            Assert.AreEqual(halved, storedWindowEra.Windows.GreatLate, 1e-9, "a re-fed CONFIG frame must be idempotent on the ladder");
        }

        private static void assertSameJudgements(TypingEngine expected, TypingEngine actual)
        {
            Assert.AreEqual(expected.Score, actual.Score, "score");
            Assert.AreEqual(expected.MaxCombo, actual.MaxCombo, "max combo");
            Assert.AreEqual(expected.CaretIndex, actual.CaretIndex, "caret");
            Assert.AreEqual(expected.LiveAccuracy, actual.LiveAccuracy, 1e-12, "accuracy");

            var expectedCells = expected.Lines[0].Cells;
            var actualCells = actual.Lines[0].Cells;

            Assert.AreEqual(expectedCells.Count, actualCells.Count);

            for (int i = 0; i < expectedCells.Count; i++)
            {
                Assert.AreEqual(expectedCells[i].State, actualCells[i].State, $"cell {i} state");
                Assert.AreEqual(expectedCells[i].TypedChar, actualCells[i].TypedChar, $"cell {i} char");
                Assert.AreEqual(expectedCells[i].JudgedDelta, actualCells[i].JudgedDelta, $"cell {i} delta");
            }
        }

        #endregion
    }
}
