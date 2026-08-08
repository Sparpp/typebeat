// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Backlog 74: the LIVE pp counter in the gameplay HUD.
//
// The counter means "what this play is worth if it ends right here". Two things have to hold for
// that not to be a lie, and both are pinned below against the REAL score processor rather than
// against a hand-built statistics dictionary:
//
//  1. CONVERGENCE. The formula's `notes` is the count of JUDGED notes, so a mid-play reading is
//     genuinely the pp of the play so far, and the reading taken on the last judgement of a passed
//     play is bit-for-bit the value computed from the score that is then submitted. (The other half
//     of "the value the SERVER stores" is the two implementations agreeing, which is pinned in
//     typebeat-web/tests/Typebeat.WireCompat/PerformancePointsParityTest.cs.)
//
//  2. THE ELIGIBILITY GATE. A play that can never earn pp must show no number at all, never a
//     number it cannot keep.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.ControlPoints;
using typebeat.Game.Rulesets.Judgements;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Objects;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class PerformancePointsHudTest
    {
        #region Fixture: a real playable beatmap of TypeBeatHitObjects

        private static TimedUnit unit(string text, double start, double end)
            => new TimedUnit { Text = text, StartTime = start, EndTime = end };

        private static LyricLine line(string text, double start, double end, params TimedUnit[] units)
            => new LyricLine
            {
                RawText = text,
                StartTime = start,
                EndTime = end,
                SingEndTime = end,
                Units = units,
            };

        private static IReadOnlyList<LyricLine> lines() => new[]
        {
            line("hello there world", 1000, 4000, unit("hello", 1000, 2000), unit("there", 2000, 3000), unit("world", 3000, 4000)),
            line("typing is a rhythm", 4000, 8000, unit("typing", 4000, 5000), unit("is", 5000, 5500), unit("a", 5500, 6000), unit("rhythm", 6000, 8000)),
            line("one more line to seal", 8000, 12000, unit("one", 8000, 8600), unit("more", 8600, 9400), unit("line", 9400, 10200), unit("to", 10200, 10800), unit("seal", 10800, 12000)),
        };

        /// <summary>
        /// The converted beatmap the drawable ruleset hands the HUD: one line object per lyric line,
        /// each carrying one nested <see cref="TypeBeatCharObject"/> per typeable cell.
        /// </summary>
        private static Beatmap<TypeBeatHitObject> playable(BeatmapOnlineStatus status = BeatmapOnlineStatus.Ranked)
        {
            var beatmap = new Beatmap<TypeBeatHitObject>
            {
                BeatmapInfo = new BeatmapInfo { Status = status },
            };

            var source = lines();

            for (int i = 0; i < source.Count; i++)
            {
                var hitObject = new TypeBeatHitObject
                {
                    Line = source[i],
                    StartTime = source[i].StartTime,
                    LineIndex = i,
                    Granularity = TimingGranularity.Line,
                };

                hitObject.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty());
                beatmap.HitObjects.Add(hitObject);
            }

            return beatmap;
        }

        private static IReadOnlyList<Mod> mods(params Mod[] stack) => stack;

        #endregion

        #region The eligibility gate: what the readout shows, and when it shows nothing

        [Test]
        public void StarRating_IsLyricDifficultyAtThePlaysEligibleRate()
        {
            var beatmap = playable();
            var source = beatmap.HitObjects.Select(h => h.Line).ToList();

            Assert.Multiple(() =>
            {
                // The no-mod reading is the very number the server stores as difficulty_rating, and
                // the two rate readings are its sr_dt / sr_ht, all from the same mirrored pass.
                Assert.That(TypeBeatHudOverlay.StarRatingFor(beatmap, null),
                    Is.EqualTo(LyricDifficulty.Compute(source)).Within(1e-12));
                Assert.That(TypeBeatHudOverlay.StarRatingFor(beatmap, mods(new TypeBeatModDoubleTime())),
                    Is.EqualTo(LyricDifficulty.Compute(source, 1.50)).Within(1e-12));
                Assert.That(TypeBeatHudOverlay.StarRatingFor(beatmap, mods(new TypeBeatModNightcore())),
                    Is.EqualTo(LyricDifficulty.Compute(source, 1.50)).Within(1e-12));
                Assert.That(TypeBeatHudOverlay.StarRatingFor(beatmap, mods(new TypeBeatModHalfTime())),
                    Is.EqualTo(LyricDifficulty.Compute(source, 0.75)).Within(1e-12));

                // The rate lands entirely in the rating, and nowhere else: the three prices are
                // three genuinely different numbers. (Which way each moves is LyricDifficulty's
                // business, and on a fixture this short the per-character floor can dominate, so
                // asserting a direction here would pin the fixture rather than the formula.)
                Assert.That(new[]
                {
                    TypeBeatHudOverlay.StarRatingFor(beatmap, null),
                    TypeBeatHudOverlay.StarRatingFor(beatmap, mods(new TypeBeatModDoubleTime())),
                    TypeBeatHudOverlay.StarRatingFor(beatmap, mods(new TypeBeatModHalfTime())),
                }.Distinct().Count(), Is.EqualTo(3));
            });
        }

        [Test]
        public void StarRating_IsUnaffectedByNonRateMods()
        {
            var beatmap = playable();

            Assert.That(TypeBeatHudOverlay.StarRatingFor(beatmap, mods(new TypeBeatModLiterate(), new TypeBeatModNoFail(), new TypeBeatModFlashlight())),
                Is.EqualTo(TypeBeatHudOverlay.StarRatingFor(beatmap, null)));
        }

        [Test]
        public void RateMultiplier_IsExactlyOneForEveryRateButBaseRateHalfTime()
        {
            // Backlog 90 prices only base-rate Half Time by anything other than its rating. A
            // multiplier that was not exactly 1.0 anywhere else would silently reprice every other
            // play in the game.
            var beatmap = playable();

            Assert.Multiple(() =>
            {
                Assert.That(PerformancePointsDisplay.RateMultiplierFor(beatmap, null), Is.EqualTo(1.0), "no mods");
                Assert.That(PerformancePointsDisplay.RateMultiplierFor(beatmap, mods()), Is.EqualTo(1.0), "an empty stack");
                Assert.That(PerformancePointsDisplay.RateMultiplierFor(beatmap, mods(new TypeBeatModLiterate(), new TypeBeatModNoFail())),
                    Is.EqualTo(1.0), "non-rate mods");
                Assert.That(PerformancePointsDisplay.RateMultiplierFor(beatmap, mods(new TypeBeatModDoubleTime())), Is.EqualTo(1.0), "Double Time");
                Assert.That(PerformancePointsDisplay.RateMultiplierFor(beatmap, mods(new TypeBeatModNightcore())), Is.EqualTo(1.0), "Nightcore");

                // A custom rate never reaches a price at all, so 1.0 is simply the neutral answer.
                var custom = new TypeBeatModHalfTime();
                custom.SpeedChange.Value = 0.62;
                Assert.That(PerformancePointsDisplay.RateMultiplierFor(beatmap, mods(custom)), Is.EqualTo(1.0), "a custom Half Time rate");

                Assert.That(PerformancePointsDisplay.RateMultiplierFor(null, mods(new TypeBeatModHalfTime())), Is.EqualTo(1.0), "no beatmap");
            });
        }

        [Test]
        public void RateMultiplier_ForHalfTimeIsTheMirrorOfTheMapsOwnThreeRatings()
        {
            // The client computes the same three ratings the server stores, so it reaches the same
            // multiplier without fetching anything.
            var beatmap = playable();
            var source = beatmap.HitObjects.Select(h => h.Line).ToList();

            double expected = PerformancePoints.HalfTimeMultiplier(
                LyricDifficulty.Compute(source),
                LyricDifficulty.Compute(source, 1.50),
                LyricDifficulty.Compute(source, 0.75));

            double actual = PerformancePointsDisplay.RateMultiplierFor(beatmap, mods(new TypeBeatModHalfTime()));

            Assert.Multiple(() =>
            {
                Assert.That(actual, Is.EqualTo(expected).Within(1e-12));
                Assert.That(actual, Is.GreaterThan(0).And.LessThanOrEqualTo(1.0), "it is a penalty, never a bonus");
            });
        }

        [Test]
        public void ACustomRatePlayIsPricedAtNothingAtAll()
        {
            // Only the base rates earn pp (docs/pp.md). The play still ranks on the score
            // leaderboards exactly as before; the counter just refuses to promise pp for it.
            var beatmap = playable();
            var doubleTime = new TypeBeatModDoubleTime();
            doubleTime.SpeedChange.Value = 1.75;

            Assert.That(TypeBeatHudOverlay.StarRatingFor(beatmap, mods(doubleTime)), Is.Null);
        }

        [Test]
        public void AnUnrankedModPlayIsPricedAtNothingAtAll()
        {
            // Mashing is Mod.Ranked = false, which is exactly what makes the submission path store
            // the score ranked = false, which is what makes the server pay it nothing.
            var beatmap = playable();

            Assert.Multiple(() =>
            {
                Assert.That(new TypeBeatModMashing().Ranked, Is.False, "the premise of the gate");
                Assert.That(TypeBeatHudOverlay.StarRatingFor(beatmap, mods(new TypeBeatModMashing())), Is.Null);
                Assert.That(TypeBeatHudOverlay.StarRatingFor(beatmap, mods(new TypeBeatModLiterate(), new TypeBeatModMashing())), Is.Null);
            });
        }

        [TestCase(BeatmapOnlineStatus.None)]
        [TestCase(BeatmapOnlineStatus.LocallyModified)]
        [TestCase(BeatmapOnlineStatus.Graveyard)]
        [TestCase(BeatmapOnlineStatus.WIP)]
        [TestCase(BeatmapOnlineStatus.Pending)]
        [TestCase(BeatmapOnlineStatus.Qualified)]
        [TestCase(BeatmapOnlineStatus.Loved)]
        public void AMapThatGrantsNoPpIsPricedAtNothingAtAll(BeatmapOnlineStatus status)
            => Assert.That(TypeBeatHudOverlay.StarRatingFor(playable(status), null), Is.Null);

        [TestCase(BeatmapOnlineStatus.Ranked)]
        [TestCase(BeatmapOnlineStatus.Approved)]
        public void AMapThatGrantsPpIsPriced(BeatmapOnlineStatus status)
            => Assert.That(TypeBeatHudOverlay.StarRatingFor(playable(status), null), Is.GreaterThan(0));

        [Test]
        public void AnIneligiblePlayShowsNoNumber()
        {
            // The decision this constant encodes: a live "214" on a play the server will store at
            // 0 pp is a lie the player would only discover on the results screen.
            Assert.That(TypeBeatHudOverlay.INELIGIBLE_TEXT, Is.EqualTo("-"));
        }

        #endregion

        #region Convergence on the submitted score

        /// <summary>
        /// One pp reading taken exactly as <c>TypeBeatHudOverlay.updatePerformancePoints</c> takes
        /// it: from the LIVE score-processor state, nothing else.
        /// </summary>
        private static double liveReading(ScoreProcessor processor, double stars, IReadOnlyList<Mod>? withMods)
            => PerformancePoints.ForPlay(
                stars,
                PerformancePoints.CountNotes(processor.Statistics),
                processor.Accuracy.Value,
                processor.HighestCombo.Value,
                withMods);

        /// <summary>The same reading from a FINISHED score, i.e. from the row that gets submitted.</summary>
        private static double submittedReading(ScoreInfo score, double stars, IReadOnlyList<Mod>? withMods)
            => PerformancePoints.ForPlay(stars, PerformancePoints.CountNotes(score), score.Accuracy, score.MaxCombo, withMods);

        [Test]
        public void LiveCounterConvergesOnTheSubmittedScoresValue()
        {
            var beatmap = playable();
            var withMods = mods(new TypeBeatModLiterate());
            double stars = TypeBeatHudOverlay.StarRatingFor(beatmap, withMods)!.Value;

            var processor = new TypeBeatScoreProcessor(new TypeBeatRuleset());
            processor.ApplyBeatmap(beatmap);

            var readings = new List<double>();
            int cells = 0;

            foreach (var lineObject in beatmap.HitObjects)
            {
                foreach (var cell in lineObject.NestedHitObjects.OfType<TypeBeatCharObject>())
                {
                    // A messy but plausible play: mostly greats, some sloppy timing, the odd missed
                    // cell, and wrong keypresses that break combo without being notes.
                    var type = (cells % 11) switch
                    {
                        3 => HitResult.Ok,
                        6 => HitResult.Meh,
                        9 => HitResult.Miss,
                        _ => HitResult.Great,
                    };

                    processor.ApplyResult(new JudgementResult(cell, cell.CreateJudgement()) { Type = type });

                    // One mistype every 13 cells, not every 7. On this 56-cell fixture every 7 put
                    // the count at exactly 8, which is exactly the backlog-97 mistype cliff
                    // ((1 + sqrt(1 + 4·56))/2 = 8), so the whole play priced to 0 and the
                    // convergence assertion below became vacuous. This is a PLUMBING test (the live
                    // counter reaching the submitted value), not a shape test, so the fixture has
                    // to stay on the priced side of the cliff.
                    if (cells % 13 == 0)
                        processor.RecordMistype();

                    readings.Add(liveReading(processor, stars, withMods));
                    cells++;
                }

                // The line container seals as the scoring-inert IgnoreHit the ruleset gives it.
                processor.ApplyResult(new JudgementResult(lineObject, lineObject.CreateJudgement()) { Type = HitResult.IgnoreHit });
                readings.Add(liveReading(processor, stars, withMods));
            }

            var score = new ScoreInfo();
            processor.PopulateScore(score);

            double finalLive = readings[^1];
            double submitted = submittedReading(score, stars, withMods);

            Assert.Multiple(() =>
            {
                // THE POINT: the last live reading is the submitted play's value, exactly.
                Assert.That(finalLive, Is.EqualTo(submitted), "the live counter must land on the submitted score's value");
                Assert.That(submitted, Is.GreaterThan(0), "the fixture play must actually be worth something");

                // ...and it got there by counting judged notes, not the map's total: the denominator
                // grew to the whole map only once every cell was judged.
                var liveCounts = PerformancePoints.CountNotes(processor.Statistics);
                Assert.That(liveCounts.Notes, Is.EqualTo(cells));
                Assert.That(liveCounts, Is.EqualTo(PerformancePoints.CountNotes(score)));
                Assert.That(score.MaxCombo, Is.EqualTo(processor.HighestCombo.Value));
                Assert.That(score.Accuracy, Is.EqualTo(processor.Accuracy.Value));

                // Not vacuous: the reading really did move over the play, and the line containers
                // (ignore_hit) never moved it, which is why they must stay out of `notes`.
                Assert.That(readings.Distinct().Count(), Is.GreaterThan(cells / 2));
                Assert.That(readings[^1], Is.EqualTo(readings[^2]), "sealing a line is not a note and cannot change the price");

                // Every intermediate reading is a real number a HUD can print.
                foreach (double reading in readings)
                {
                    Assert.That(double.IsFinite(reading), Is.True);
                    Assert.That(reading, Is.GreaterThanOrEqualTo(0));
                }
            });
        }

        [Test]
        public void MistypesReachTheLiveCounterThroughTheCleanlinessTermOnly()
        {
            var beatmap = playable();
            double stars = TypeBeatHudOverlay.StarRatingFor(beatmap, null)!.Value;

            var clean = new TypeBeatScoreProcessor(new TypeBeatRuleset());
            var sloppy = new TypeBeatScoreProcessor(new TypeBeatRuleset());

            foreach (var processor in new[] { clean, sloppy })
            {
                processor.ApplyBeatmap(beatmap);

                foreach (var lineObject in beatmap.HitObjects)
                {
                    foreach (var cell in lineObject.NestedHitObjects.OfType<TypeBeatCharObject>())
                        processor.ApplyResult(new JudgementResult(cell, cell.CreateJudgement()) { Type = HitResult.Great });
                }
            }

            for (int i = 0; i < 40; i++)
                sloppy.RecordMistype();

            var cleanCounts = PerformancePoints.CountNotes(clean.Statistics);
            var sloppyCounts = PerformancePoints.CountNotes(sloppy.Statistics);

            Assert.Multiple(() =>
            {
                // Same map, same cells typed, same accuracy, same combo: only the mistypes differ...
                Assert.That(sloppyCounts.Notes, Is.EqualTo(cleanCounts.Notes));
                Assert.That(sloppyCounts.Misses, Is.EqualTo(cleanCounts.Misses));
                Assert.That(sloppyCounts.Mistypes, Is.EqualTo(40));
                Assert.That(sloppy.Accuracy.Value, Is.EqualTo(clean.Accuracy.Value));

                // ...and the counter prices exactly that difference.
                Assert.That(liveReading(sloppy, stars, null), Is.LessThan(liveReading(clean, stars, null)));
            });
        }

        [Test]
        public void ARewoundJudgementUnwindsTheCounterRatherThanStrandingIt()
        {
            // Replays scrub backwards, which reverts results. The counter reads live state, so it
            // must simply follow; a cached count that only ever grew would strand it.
            var beatmap = playable();
            double stars = TypeBeatHudOverlay.StarRatingFor(beatmap, null)!.Value;

            var processor = new TypeBeatScoreProcessor(new TypeBeatRuleset());
            processor.ApplyBeatmap(beatmap);

            var applied = new List<JudgementResult>();

            foreach (var cell in beatmap.HitObjects[0].NestedHitObjects.OfType<TypeBeatCharObject>())
            {
                var result = new JudgementResult(cell, cell.CreateJudgement()) { Type = HitResult.Great };
                processor.ApplyResult(result);
                applied.Add(result);
            }

            double atFullLine = liveReading(processor, stars, null);

            for (int i = applied.Count - 1; i >= applied.Count / 2; i--)
                processor.RevertResult(applied[i]);

            double afterRewind = liveReading(processor, stars, null);

            Assert.Multiple(() =>
            {
                Assert.That(PerformancePoints.CountNotes(processor.Statistics).Notes, Is.EqualTo(applied.Count / 2));
                Assert.That(afterRewind, Is.Not.EqualTo(atFullLine));
                Assert.That(double.IsFinite(afterRewind), Is.True);
                Assert.That(afterRewind, Is.GreaterThanOrEqualTo(0));
            });
        }

        #endregion
    }
}
