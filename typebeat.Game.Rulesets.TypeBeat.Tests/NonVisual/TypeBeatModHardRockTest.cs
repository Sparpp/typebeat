// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Backlog 150: the Hard Rock mod, the exact mirror of Easy on the general window scale backlog 149
// built (TypingEngine.WindowScale). The mechanism itself is pinned by TypeBeatModEasyTest, so this
// fixture covers what is specific to HR: that halving really tightens what a press is graded as,
// that a replay is re-judged on the tightened ladder, the shipping surface (acronym, type, ranked
// flag, score multiplier, pp), the one incompatibility, and the score-multiplier headroom the
// server's stack cap depends on.
//
// Backlog 180 gave HR a SECOND half: it reverts the judgement rule to the classic per-character
// point targets, because backlog 179's syllable-span rule (delta 0 anywhere inside the sung span)
// undercuts the halved windows. The region at the bottom of this fixture covers that, live and
// through a replay round trip.

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
        /// the judgement ERA there rather than in
        /// <see cref="TypeBeatModHardRock.ApplyToDrawableRuleset"/>. The window scale, which is
        /// re-read per judgement and so has no ordering hazard, still arrives from the mod.
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
        /// The rule where it can be seen: 'c' pressed at 1300 is INSIDE the span "cake" is sung
        /// over ([1000, 3000]) and 300 ms past its own point target (1000). Under every other stack
        /// that is delta 0 and a Great; under Hard Rock it is delta 300, which the halved ladder
        /// prices as an Ok (its Great window ends 200 ms late). One press, two rules, and the mod
        /// is the only difference between the two engines.
        /// </summary>
        [Test]
        public void AnInSpanOffTargetPressIsJudgedOnItsPointTargetUnderHardRock()
        {
            const double press_time = 1300;

            var hard = liveEngine(new TypeBeatModHardRock());
            var plain = liveEngine();

            // The press really is in span, so this is a test of the RULE and not of a press that
            // would have been graded the same either way.
            var line = hard.Lines[0];
            var span = line.Syllables[line.SyllableIndexOf(0)];

            Assert.AreEqual(1000, span.StartTime, 1e-9);
            Assert.AreEqual(3000, span.EndTime, 1e-9);
            Assert.AreEqual(1000, line.Cells[0].TargetTime, 1e-9);
            Assert.GreaterOrEqual(press_time, span.StartTime);
            Assert.LessOrEqual(press_time, span.EndTime);

            var hardJudgement = press(hard, 'c', press_time);
            var plainJudgement = press(plain, 'c', press_time);

            Assert.AreEqual(300, hardJudgement.Delta, 1e-9, "Hard Rock judges the distance to the point target");
            Assert.AreEqual(JudgementType.Ok, hardJudgement.Type);

            Assert.AreEqual(0, plainJudgement.Delta, 1e-9, "every other stack judges the distance to the sung span");
            Assert.AreEqual(JudgementType.Great, plainJudgement.Type);

            // Stored, not just announced, so every readout that re-reads JudgedDelta agrees.
            Assert.AreEqual(300, hard.Lines[0].Cells[0].JudgedDelta!.Value, 1e-9);
            Assert.AreEqual(0, plain.Lines[0].Cells[0].JudgedDelta!.Value, 1e-9);
        }

        /// <summary>
        /// The era survives the round trip with no scorer change, which is the whole reason the flag
        /// is decided at engine construction: the recorder stamps the LIVE engine's flag into the
        /// CONFIG frame (bit 2), so an HR run records the bit CLEAR and every re-derivation of it,
        /// forever, judges on point targets from the frame alone. Fed back through
        /// <see cref="ReplayEngineFeed"/> the run reproduces bit-exactly, cell states, stored deltas,
        /// score, combo and accuracy alike.
        /// </summary>
        [Test]
        public void AHardRockRunRecordsTheClassicEraAndReDerivesBitExact()
        {
            var live = liveEngine(new TypeBeatModHardRock());

            // Four in-span presses, each off its own point target (1000/1500/2000/2500) by a
            // different amount. Cross-checks against the halved ladder (Great [-125, 200],
            // Ok [-300, 500]): +300 Ok, -100 Great, +400 Ok, +100 Great.
            (double time, char character)[] presses = { (1300, 'c'), (1400, 'a'), (2400, 'k'), (2600, 'e') };

            // Exactly what TypeBeatReplayRecorder writes: one CONFIG header off the live engine's
            // own settings, ahead of the first input, then one frame per effective input.
            var frames = new List<TypeBeatReplayFrame>
            {
                TypeBeatReplayFrame.CreateConfigFrame(presses[0].time, live.AllowWrongInput, live.SpaceSkipsWord, live.SyllableTiming),
            };

            foreach ((double time, char character) in presses)
            {
                live.Update(time);
                Assert.IsTrue(live.ProcessKey(character, time));
                frames.Add(new TypeBeatReplayFrame(time, character));
            }

            Assert.IsTrue(frames[0].IsConfig);
            Assert.IsFalse(frames[0].SyllableTiming, "an HR run records flags bit 2 CLEAR: the classic era");

            // The live run really was graded on point deltas, so the comparison below is not two
            // copies of the syllable rule agreeing with each other.
            Assert.AreEqual(new double?[] { 300, -100, 400, 100 }, live.Lines[0].Cells.Select(c => c.JudgedDelta).ToArray());

            // The watching engine deliberately starts in the SYLLABLE era, which is what a no-mod
            // client builds, so the frame's bit is the only thing that can put it back on point
            // targets. The ladder arrives from the score's mods, as it always does.
            var replayed = liveEngine();
            replayed.WindowScale *= TypeBeatModHardRock.WINDOW_SCALE;

            Assert.IsTrue(replayed.SyllableTiming, "the watcher's own default is the live rule");

            foreach (var frame in frames)
                ReplayEngineFeed.Apply(replayed, frame);

            Assert.IsFalse(replayed.SyllableTiming, "the replay's own header wins over the watching client's era");

            assertSameJudgements(live, replayed);

            // And the recorded bit is load-bearing rather than decorative: the SAME keystrokes under
            // a header with bit 2 set are four in-span Greats at delta 0 and a different total.
            var wrongEra = liveEngine();
            wrongEra.WindowScale *= TypeBeatModHardRock.WINDOW_SCALE;
            frames[0] = TypeBeatReplayFrame.CreateConfigFrame(presses[0].time, live.AllowWrongInput, live.SpaceSkipsWord, true);

            foreach (var frame in frames)
                ReplayEngineFeed.Apply(wrongEra, frame);

            Assert.AreEqual(new double?[] { 0, 0, 0, 0 }, wrongEra.Lines[0].Cells.Select(c => c.JudgedDelta).ToArray());
            Assert.AreNotEqual(live.Score, wrongEra.Score, "the two eras must not score the same run alike");
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
