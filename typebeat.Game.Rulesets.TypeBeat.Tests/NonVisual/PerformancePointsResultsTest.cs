// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Backlog 75: PER-SCORE pp on the results screen and on replays.
//
// Four things have to hold, and each has a way of going quietly wrong:
//
//  1. NO ROUND TRIP. An offline play, an imported .osr and a replay downloaded from the website
//     must all read as what they are worth. Nothing here may depend on having talked to a server.
//
//  2. THE SERVER'S VALUE WINS WHEN THERE IS ONE, and "is there one" is not the same question as
//     "is it non-zero". A submitted-but-ineligible play is stored at a settled 0; a play that never
//     reached the server holds nothing at all. Collapsing those two would either print "0" for an
//     unpriced play or silently reprice one the server already priced.
//
//  3. INELIGIBLE IS NOT ZERO. 0 is a legitimate price for an eligible play (a give-up run earns
//     it), so a play that can NEVER earn pp cannot be rendered as one.
//
//  4. A REPLAY IS PRICED BY ITS SIMULATION. Watching a replay re-derives its statistics, so the pp
//     printed beside those statistics must be the simulation's too, not the recording's.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.ControlPoints;
using typebeat.Game.Rulesets.Difficulty;
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
using typebeat.Game.Screens.Ranking.Expanded.Statistics;
using typebeat.Game.Screens.Ranking.Statistics;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class PerformancePointsResultsTest
    {
        #region Fixture: a real playable beatmap, and a real played score over it

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

        /// <summary>
        /// An empty score carrying what a real results-screen score always carries: the map it was
        /// set on (whose status is a pp gate) and the ruleset (which is how the shared score panel
        /// reaches that gate, since it cannot reference this assembly).
        /// </summary>
        private static ScoreInfo scoreOn(Beatmap<TypeBeatHitObject> beatmap) => new ScoreInfo
        {
            BeatmapInfo = beatmap.BeatmapInfo,
            Ruleset = new TypeBeatRuleset().RulesetInfo,
        };

        /// <summary>
        /// A messy but plausible finished play over <paramref name="beatmap"/>, produced by the REAL
        /// score processor rather than a hand-built statistics dictionary, so accuracy, combo, rank
        /// and the mistype count are all mutually consistent the way a submitted score's are.
        /// </summary>
        private static (ScoreInfo Score, TypeBeatScoreProcessor Processor) play(Beatmap<TypeBeatHitObject> beatmap, IReadOnlyList<Mod>? withMods = null)
        {
            var processor = new TypeBeatScoreProcessor(new TypeBeatRuleset());
            processor.ApplyBeatmap(beatmap);

            int cells = 0;

            foreach (var lineObject in beatmap.HitObjects)
            {
                foreach (var cell in lineObject.NestedHitObjects.OfType<TypeBeatCharObject>())
                {
                    var type = (cells % 11) switch
                    {
                        3 => HitResult.Ok,
                        6 => HitResult.Meh,
                        9 => HitResult.Miss,
                        _ => HitResult.Great,
                    };

                    processor.ApplyResult(new JudgementResult(cell, cell.CreateJudgement()) { Type = type });

                    if (cells % 7 == 0)
                        processor.RecordMistype();

                    cells++;
                }

                processor.ApplyResult(new JudgementResult(lineObject, lineObject.CreateJudgement()) { Type = HitResult.IgnoreHit });
            }

            var score = scoreOn(beatmap);

            processor.PopulateScore(score);

            if (withMods != null)
                score.Mods = withMods.ToArray();

            return (score, processor);
        }

        /// <summary>The value of the results table's pp row, exactly as the results screen builds it.</summary>
        private static double? ppRow(ScoreInfo score, IBeatmap? beatmap)
        {
            var rows = TypeBeatRuleset.CreateCompletionStatistics(score, beatmap);

            Assert.That(rows[^1].Name, Is.EqualTo("pp"), "the pp row is the last row of the completion table");

            return ((SimpleStatisticItem<double?>)rows[^1]).Value;
        }

        /// <summary>
        /// What the SCORE PANEL's pp readout would show for the same score: null where it renders
        /// its ineligible dash, otherwise the number it prints. Built from the panel's own public
        /// gate plus the same two value sources the panel uses in the same order (a server-supplied
        /// price first, then the ruleset's performance calculator), so this is the panel's rule
        /// rather than a paraphrase of it.
        /// </summary>
        private static double? panelValue(ScoreInfo score, Beatmap<TypeBeatHitObject>? beatmap)
        {
            if (!PerformanceStatistic.ScoreEarnsPerformancePoints(score))
                return null;

            if (score.PP is double stored)
                return stored;

            if (beatmap == null)
                return null;

            // BeatmapDifficultyCache hands the panel exactly these attributes: TypeBeatDifficultyCalculator
            // rates the map at the play's own clock rate.
            var attributes = new DifficultyAttributes(score.Mods, rateAdjustedStars(beatmap, score.Mods));

            return new TypeBeatPerformanceCalculator(new TypeBeatRuleset()).Calculate(score, attributes).Total;
        }

        /// <summary>The star rating TypeBeatDifficultyCalculator produces for a play, rate and all.</summary>
        private static double rateAdjustedStars(Beatmap<TypeBeatHitObject> beatmap, IReadOnlyList<Mod> withMods)
        {
            double rate = 1;

            foreach (var mod in withMods.OfType<IApplicableToRate>())
                rate = mod.ApplyToRate(0, rate);

            return LyricDifficulty.Compute(beatmap.HitObjects.Select(h => h.Line), rate);
        }

        #endregion

        #region The row itself

        [Test]
        public void ThePpRowIsUnconditionalAndClosesTheTable()
        {
            // Unlike Mistypes, which appears only for a score that carries the stat, a pp reading
            // always exists: either a number or "could never have earned any". It sits last, after
            // the raw counts it is derived from.
            var beatmap = playable();
            var (score, _) = play(beatmap);

            var priced = TypeBeatRuleset.CreateCompletionStatistics(score, beatmap);
            var ineligible = TypeBeatRuleset.CreateCompletionStatistics(score, playable(BeatmapOnlineStatus.Graveyard));

            Assert.Multiple(() =>
            {
                Assert.That(priced.Select(r => r.Name), Is.EqualTo(new[] { "Completion", "Missed characters", "Mistypes", "pp" }));
                Assert.That(ineligible.Select(r => r.Name), Is.EqualTo(new[] { "Completion", "Missed characters", "Mistypes", "pp" }));
            });
        }

        [Test]
        public void IneligibleRendersAsADashAndZeroRendersAsZero()
        {
            // The whole point of the nullable: these two must not share a rendering, and the two
            // surfaces that print them share this one function so they cannot drift apart.
            Assert.Multiple(() =>
            {
                Assert.That(PerformancePointsDisplay.Format(null), Is.EqualTo("-"));
                Assert.That(PerformancePointsDisplay.Format(0), Is.EqualTo("0"));
                Assert.That(PerformancePointsDisplay.Format(213.6), Is.EqualTo("214"));
                Assert.That(TypeBeatHudOverlay.INELIGIBLE_TEXT, Is.EqualTo(PerformancePointsDisplay.INELIGIBLE_TEXT),
                    "the live counter and the results screen must render an ineligible play identically");
            });
        }

        #endregion

        #region No round trip: a play the server never saw is still priced

        [Test]
        public void AnOfflinePlayIsPricedLocallyFromTheScoreAndTheBeatmap()
        {
            var beatmap = playable();
            var (score, _) = play(beatmap);

            Assert.That(score.PP, Is.Null, "the premise: this play never reached a server");

            double stars = PerformancePointsDisplay.StarRatingFor(beatmap, score.Mods)!.Value;
            double expected = PerformancePoints.ForPlay(stars, PerformancePoints.CountNotes(score), score.Accuracy, score.MaxCombo, score.Mods);

            Assert.Multiple(() =>
            {
                Assert.That(expected, Is.GreaterThan(0), "the fixture play must actually be worth something");
                Assert.That(ppRow(score, beatmap), Is.EqualTo(expected));
            });
        }

        [Test]
        public void TheLocalPriceMovesWithTheMapsRateJustAsTheServersDoes()
        {
            // DT/HT are priced exclusively through the star rating (docs/pp.md), and the client
            // recomputes that rating itself, so an offline DT play is not simply the nomod price.
            var beatmap = playable();

            double? nomod = ppRow(play(beatmap).Score, beatmap);
            double? doubleTime = ppRow(play(beatmap, mods(new TypeBeatModDoubleTime())).Score, beatmap);
            double? halfTime = ppRow(play(beatmap, mods(new TypeBeatModHalfTime())).Score, beatmap);

            Assert.Multiple(() =>
            {
                Assert.That(new[] { nomod, doubleTime, halfTime }.Distinct().Count(), Is.EqualTo(3));
                Assert.That(nomod, Is.GreaterThan(0));
            });
        }

        [Test]
        public void WithoutABeatmapToPriceAgainstTheRowReadsAsIneligible()
        {
            // The honest reading of "there is no map here": never a fabricated number.
            var (score, _) = play(playable());

            Assert.That(ppRow(score, null), Is.Null);
        }

        #endregion

        #region The server's value wins, but only where a value genuinely exists

        [Test]
        public void AStoredServerValueOutranksTheLocalCalculation()
        {
            var beatmap = playable();
            var (score, _) = play(beatmap);

            double local = ppRow(score, beatmap)!.Value;

            // A number the local calculation would never produce: the server priced this play
            // against its own stored ratings, and may know of refusals the client cannot see.
            score.PP = local + 137;

            Assert.That(ppRow(score, beatmap), Is.EqualTo(local + 137));
        }

        [Test]
        public void AStoredValueSurvivesGatesTheLocalCopyOfTheMapWouldFail()
        {
            // A stored value is proof the server ran the formula, which it does only for a play it
            // considers eligible (it sends null for every other kind). So a stored value is
            // self-certifying, which matters when the LOCAL copy of the map has drifted from the
            // ranked one the play was set on: opening it in the editor marks it LocallyModified, and
            // the local gates alone would then hide a number the player genuinely earned.
            var (score, _) = play(playable());
            score.PP = 214;

            Assert.That(ppRow(score, playable(BeatmapOnlineStatus.LocallyModified)), Is.EqualTo(214));
        }

        [Test]
        public void AStoredZeroIsARealPriceAndPrintsAsZero()
        {
            // A give-up run on a ranked map earns exactly 0, and the server says so with a 0 rather
            // than a null. That is a price, so it prints, and it must not be confused with the dash
            // an ineligible play gets.
            var beatmap = playable();
            var (score, _) = play(beatmap);
            score.PP = 0;

            var value = ppRow(score, beatmap);

            Assert.Multiple(() =>
            {
                Assert.That(value, Is.EqualTo(0d));
                Assert.That(PerformancePointsDisplay.Format(value), Is.EqualTo("0"));
                Assert.That(PerformancePointsDisplay.Format(null), Is.Not.EqualTo(PerformancePointsDisplay.Format(0d)));
            });
        }

        [Test]
        public void AnAbsentStoredValueIsNotAStoredZero()
        {
            // The distinction the whole design rests on, stated directly.
            var beatmap = playable();
            var (unsubmitted, _) = play(beatmap);
            var (submitted, _) = play(beatmap);
            submitted.PP = 0;

            Assert.Multiple(() =>
            {
                Assert.That(ppRow(unsubmitted, beatmap), Is.GreaterThan(0), "an unsubmitted play is priced, not zeroed");
                Assert.That(ppRow(submitted, beatmap), Is.EqualTo(0d));
            });
        }

        #endregion

        #region Ineligible reads as ineligible

        // NOTE ON THE PREMISE OF THIS WHOLE REGION: an ineligible play carries NO stored value. The
        // server refuses to price one and sends null (pinned server-side by
        // VariableRateScoreTest.PerformancePoints_AreWrittenAtSubmission_AndOnlyForBaseRatePlays and
        // RankedApprovalTest.ScoreOnPendingSet_StoresUnranked_AndShowsOnTheUnrankedBoard), so the
        // 0 sitting in its scores.pp column never reaches the client to be mistaken for a price.

        [Test]
        public void AnUnrankedModPlayReadsAsIneligible()
        {
            var beatmap = playable();
            var (score, _) = play(beatmap, mods(new TypeBeatModMashing()));

            Assert.Multiple(() =>
            {
                Assert.That(new TypeBeatModMashing().Ranked, Is.False, "the premise of the gate");
                Assert.That(ppRow(score, beatmap), Is.Null);
            });
        }

        [Test]
        public void ACustomRatePlayReadsAsIneligible()
        {
            var beatmap = playable();
            var doubleTime = new TypeBeatModDoubleTime();
            doubleTime.SpeedChange.Value = 1.75;

            var (score, _) = play(beatmap, mods(doubleTime));

            Assert.That(ppRow(score, beatmap), Is.Null);
        }

        [TestCase(BeatmapOnlineStatus.None)]
        [TestCase(BeatmapOnlineStatus.Graveyard)]
        [TestCase(BeatmapOnlineStatus.WIP)]
        [TestCase(BeatmapOnlineStatus.Pending)]
        [TestCase(BeatmapOnlineStatus.Qualified)]
        [TestCase(BeatmapOnlineStatus.Loved)]
        public void AMapThatGrantsNoPpReadsAsIneligible(BeatmapOnlineStatus status)
        {
            var beatmap = playable(status);
            var (score, _) = play(beatmap);

            Assert.That(ppRow(score, beatmap), Is.Null);
        }

        [Test]
        public void AFailedPlayReadsAsIneligible()
        {
            // The one gate the results screen has that the live counter cannot: mid-play a fail is
            // unknowable, so the HUD keeps counting; afterwards it is settled, and a failed run can
            // never be worth anything.
            var beatmap = playable();

            var (failedFlag, _) = play(beatmap);
            failedFlag.Passed = false;

            var (failedRank, _) = play(beatmap);
            failedRank.Rank = ScoreRank.F;

            var (passed, _) = play(beatmap);

            Assert.Multiple(() =>
            {
                Assert.That(ppRow(failedFlag, beatmap), Is.Null);
                Assert.That(ppRow(failedRank, beatmap), Is.Null);
                Assert.That(ppRow(passed, beatmap), Is.GreaterThan(0), "not vacuous: the same play passes and is priced");
            });
        }

        #endregion

        #region The score panel and the results table agree

        /// <summary>
        /// Every state a results screen can show a play in, as (name, score, map) rows. This is the
        /// list the agreement tests below sweep, so a new state gets pinned on both surfaces at once.
        /// </summary>
        private static IEnumerable<TestCaseData> everyState()
        {
            var ranked = playable();

            var priced = play(ranked).Score;
            priced.PP = 214.4;
            yield return new TestCaseData("priced by the server", priced, ranked);

            var pricedAtZero = play(ranked).Score;
            pricedAtZero.PP = 0;
            yield return new TestCaseData("priced by the server at zero", pricedAtZero, ranked);

            yield return new TestCaseData("never submitted, priced locally", play(ranked).Score, ranked);

            var doubleTime = play(ranked, mods(new TypeBeatModDoubleTime())).Score;
            yield return new TestCaseData("never submitted, base-rate Double Time", doubleTime, ranked);

            var customRate = new TypeBeatModDoubleTime();
            customRate.SpeedChange.Value = 1.75;
            yield return new TestCaseData("ineligible: custom rate", play(ranked, mods(customRate)).Score, ranked);

            yield return new TestCaseData("ineligible: unranked mod", play(ranked, mods(new TypeBeatModMashing())).Score, ranked);

            var wip = playable(BeatmapOnlineStatus.WIP);
            yield return new TestCaseData("ineligible: map grants no pp", play(wip).Score, wip);

            var failed = play(ranked).Score;
            failed.Passed = false;
            failed.Rank = ScoreRank.F;
            yield return new TestCaseData("ineligible: failed play", failed, ranked);
        }

        [TestCaseSource(nameof(everyState))]
        public void ThePanelAndTheTableShowTheSameThing(string state, ScoreInfo score, Beatmap<TypeBeatHitObject> beatmap)
        {
            // THE POINT: the results screen shows pp TWICE, in the score panel and in the statistics
            // table below it. Before backlog 75 the panel printed a hardcoded 0 for every type!beat
            // play, because the ruleset had no performance calculator for it to reach. Two readings
            // of the same play on the same screen must never disagree, and least of all about
            // whether the play was in the running at all.
            double? panel = panelValue(score, beatmap);
            double? table = ppRow(score, beatmap);

            Assert.Multiple(() =>
            {
                Assert.That(panel == null, Is.EqualTo(table == null),
                    $"{state}: one surface shows a number and the other shows {PerformancePointsDisplay.INELIGIBLE_TEXT}");

                if (table != null)
                    Assert.That(panel, Is.EqualTo(table).Within(1e-9), $"{state}: the two surfaces priced the play differently");
            });
        }

        [Test]
        public void TheStatesSweptAreActuallyBothKinds()
        {
            // Guards the sweep above from passing vacuously (e.g. if every case became ineligible).
            var states = everyState().ToList();
            var values = states.Select(s => ppRow((ScoreInfo)s.Arguments[1]!, (Beatmap<TypeBeatHitObject>)s.Arguments[2]!)).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(values.Count(v => v != null), Is.EqualTo(4), "four priced states");
                Assert.That(values.Count(v => v == null), Is.EqualTo(4), "four ineligible states");
                Assert.That(values.Where(v => v != null).Distinct().Count(), Is.GreaterThan(1), "and the prices are not all the same number");
            });
        }

        [Test]
        public void TheRulesetSuppliesAPerformanceCalculatorAtAll()
        {
            // The actual regression this fixes: Ruleset.CreatePerformanceCalculator defaults to
            // null, and the score panel silently renders 0 when it gets one.
            Assert.That(new TypeBeatRuleset().CreatePerformanceCalculator(), Is.Not.Null);
        }

        [Test]
        public void ThePanelsGateAndTheTablesGateAreOneImplementation()
        {
            // Not two rules that happen to agree: the panel reaches the ruleset override through
            // score.Ruleset (a shared component cannot reference this assembly) and the table
            // reaches the same override through PerformancePointsDisplay.Eligible.
            var beatmap = playable();
            var (eligible, _) = play(beatmap);
            var (ineligible, _) = play(beatmap, mods(new TypeBeatModMashing()));

            Assert.Multiple(() =>
            {
                Assert.That(PerformancePointsDisplay.Eligible(eligible), Is.True);
                Assert.That(PerformancePointsDisplay.Eligible(ineligible), Is.False);

                Assert.That(eligible.Ruleset.CreateInstance().ScoreEarnsPerformancePoints(eligible),
                    Is.EqualTo(PerformancePointsDisplay.Eligible(eligible)));
                Assert.That(ineligible.Ruleset.CreateInstance().ScoreEarnsPerformancePoints(ineligible),
                    Is.EqualTo(PerformancePointsDisplay.Eligible(ineligible)));
            });
        }

        [Test]
        public void TheCalculatorRefusesACustomRateRatherThanPricingIt()
        {
            // The one place the calculator must not simply trust the attributes it is handed:
            // BeatmapDifficultyCache rates a play at WHATEVER rate it ran at, including 1.75x, while
            // docs/pp.md pays only the base rates. Pricing off that rating would invent pp for a
            // play the server refuses to pay for.
            var beatmap = playable();
            var customRate = new TypeBeatModDoubleTime();
            customRate.SpeedChange.Value = 1.75;

            var (score, _) = play(beatmap, mods(customRate));
            var attributes = new DifficultyAttributes(score.Mods, rateAdjustedStars(beatmap, score.Mods));

            Assert.Multiple(() =>
            {
                Assert.That(attributes.StarRating, Is.GreaterThan(0), "the difficulty cache does rate the play");
                Assert.That(new TypeBeatPerformanceCalculator(new TypeBeatRuleset()).Calculate(score, attributes).Total, Is.Zero);
            });
        }

        #endregion

        #region Replays are priced by their simulation

        [Test]
        public void ReSimulatingAScoreDropsTheRecordedPrice()
        {
            // Watching a replay re-derives its statistics through ScoreProcessor.PopulateScore. The
            // recorded pp described the values being overwritten, so it goes with them; what the
            // results screen then prints is the pp OF THE SIMULATION, consistent with every other
            // row of the same table.
            var beatmap = playable();
            var (recorded, processor) = play(beatmap);

            recorded.PP = 999;

            // The replay is watched: the same ScoreInfo is repopulated from the re-simulation.
            processor.PopulateScore(recorded);

            Assert.Multiple(() =>
            {
                Assert.That(recorded.PP, Is.Null, "a re-simulation cannot leave a stale price behind");
                Assert.That(ppRow(recorded, beatmap), Is.Not.EqualTo(999));
                Assert.That(ppRow(recorded, beatmap), Is.GreaterThan(0), "and it is priced from the simulation instead");
            });
        }

        [Test]
        public void AReplayThatIsRewoundIsRepricedRatherThanStranded()
        {
            // Scrubbing backwards reverts results and repopulates, so the reading follows the
            // simulation's current state rather than freezing on the recording's.
            var beatmap = playable();

            var processor = new TypeBeatScoreProcessor(new TypeBeatRuleset());
            processor.ApplyBeatmap(beatmap);

            var results = new List<JudgementResult>();

            foreach (var lineObject in beatmap.HitObjects)
            {
                foreach (var cell in lineObject.NestedHitObjects.OfType<TypeBeatCharObject>())
                {
                    var result = new JudgementResult(cell, cell.CreateJudgement()) { Type = HitResult.Great };
                    processor.ApplyResult(result);
                    results.Add(result);
                }
            }

            var atEnd = scoreOn(beatmap);
            processor.PopulateScore(atEnd);

            for (int i = results.Count - 1; i >= results.Count / 2; i--)
                processor.RevertResult(results[i]);

            var rewound = scoreOn(beatmap);
            rewound.PP = 999;
            processor.PopulateScore(rewound);

            Assert.Multiple(() =>
            {
                Assert.That(rewound.PP, Is.Null);
                Assert.That(ppRow(rewound, beatmap), Is.Not.EqualTo(ppRow(atEnd, beatmap)));
                Assert.That(ppRow(rewound, beatmap), Is.GreaterThanOrEqualTo(0));
            });
        }

        #endregion

        #region The two surfaces agree

        [Test]
        public void TheResultsRowIsTheLiveCountersFinalReading()
        {
            // The HUD's contract is "what this play is worth if it ends right here"; the results
            // row is what it was worth. On the last judgement of a passed play those are the same
            // number, and they must stay the same number.
            var beatmap = playable();
            var withMods = mods(new TypeBeatModLiterate());
            var (score, processor) = play(beatmap, withMods);

            double stars = TypeBeatHudOverlay.StarRatingFor(beatmap, withMods)!.Value;

            double liveReading = PerformancePoints.ForPlay(
                stars,
                PerformancePoints.CountNotes(processor.Statistics),
                processor.Accuracy.Value,
                processor.HighestCombo.Value,
                withMods);

            Assert.Multiple(() =>
            {
                Assert.That(ppRow(score, beatmap), Is.EqualTo(liveReading));
                Assert.That(PerformancePointsDisplay.Format(ppRow(score, beatmap)), Is.EqualTo(PerformancePointsDisplay.Format(liveReading)));
            });
        }

        [Test]
        public void TheTwoSurfacesShareOneSetOfGates()
        {
            // Not a paraphrase of the HUD's rule: literally the same function, so a gate added to
            // one is a gate added to both.
            var beatmap = playable();

            Assert.Multiple(() =>
            {
                Assert.That(TypeBeatHudOverlay.StarRatingFor(beatmap, null),
                    Is.EqualTo(PerformancePointsDisplay.StarRatingFor(beatmap, null)));
                Assert.That(TypeBeatHudOverlay.StarRatingFor(playable(BeatmapOnlineStatus.WIP), null),
                    Is.EqualTo(PerformancePointsDisplay.StarRatingFor(playable(BeatmapOnlineStatus.WIP), null)));
                Assert.That(TypeBeatHudOverlay.StarRatingFor(beatmap, mods(new TypeBeatModMashing())),
                    Is.EqualTo(PerformancePointsDisplay.StarRatingFor(beatmap, mods(new TypeBeatModMashing()))));
            });
        }

        #endregion
    }
}
