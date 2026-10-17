// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Scoring;
using typebeat.Game.Screens.Play;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class PolyglotRankingTest
    {
        private static IEnumerable<BeatmapLanguage> dropdownLanguages => BeatmapLanguageExtensions.DisplayOrder;

        [TestCaseSource(nameof(dropdownLanguages))]
        public void DropdownLanguageControlsRankingSubmissionAndPp(BeatmapLanguage language)
        {
            var map = beatmap(language);
            var mod = new TypeBeatModPolyglot();
            Mod[] mods = { mod, new TypeBeatModLiterate() };
            var score = scoreFor(map, mods);
            bool ranked = language is not (BeatmapLanguage.Chinese or BeatmapLanguage.Korean);
            var ruleset = new TypeBeatRuleset();
            Assert.Multiple(() =>
            {
                Assert.That(mod.IsRankedFor(map), Is.EqualTo(ranked));
                Assert.That(mod.IsLocalOnlyFor(map), Is.EqualTo(!ranked));
                Assert.That(SubmittingPlayer.IsLocalOnly(mods, map), Is.EqualTo(!ranked));
                Assert.That(ruleset.ScoreEarnsPerformancePoints(score), Is.EqualTo(ranked));
                Assert.That(ruleset.ScoreCanDisplayPerformancePoints(score), Is.EqualTo(ranked));
            });
        }

        [Test]
        public void ReusingAndCloningTheModDoesNotRetainAnotherMapsLanguage()
        {
            var mod = new TypeBeatModPolyglot();
            foreach (var language in new[] { BeatmapLanguage.Japanese, BeatmapLanguage.Chinese, BeatmapLanguage.Russian, BeatmapLanguage.Korean, BeatmapLanguage.Spanish })
            {
                var map = beatmap(language);
                bool ranked = language is not (BeatmapLanguage.Chinese or BeatmapLanguage.Korean);
                Assert.That(mod.IsRankedFor(map), Is.EqualTo(ranked));
                Assert.That(mod.DeepClone().IsRankedFor(map), Is.EqualTo(ranked));
            }
        }

        [Test]
        public void OtherModsKeepTheirRankingAndSubmissionPolicies()
        {
            var ruleset = new TypeBeatRuleset();
            foreach (var language in dropdownLanguages)
            {
                var map = beatmap(language);
                var literate = new TypeBeatModLiterate();
                var unranked = new TypeBeatModMashing();
                Assert.Multiple(() =>
                {
                    Assert.That(literate.IsRankedFor(map), Is.EqualTo(literate.Ranked));
                    Assert.That(unranked.IsRankedFor(map), Is.EqualTo(unranked.Ranked));
                    Assert.That(literate.IsLocalOnlyFor(map), Is.EqualTo(literate.LocalOnly));
                    Assert.That(unranked.IsLocalOnlyFor(map), Is.EqualTo(unranked.LocalOnly));
                    Assert.That(ruleset.ScoreEarnsPerformancePoints(scoreFor(map, new Mod[] { new TypeBeatModPolyglot(), unranked })), Is.False);
                });
            }
        }

        [Test]
        public void RankedPolyglotStillRequiresAPassedRankedMapAndAnEligibleRate()
        {
            var ruleset = new TypeBeatRuleset();
            var score = scoreFor(beatmap(BeatmapLanguage.Japanese), new Mod[] { new TypeBeatModPolyglot() });
            Assert.That(ruleset.ScoreEarnsPerformancePoints(score), Is.True);
            score.Passed = false;
            score.Rank = ScoreRank.F;
            Assert.That(ruleset.ScoreEarnsPerformancePoints(score), Is.False);
            score.Passed = true;
            score.Rank = ScoreRank.X;
            score.BeatmapInfo!.Status = BeatmapOnlineStatus.Pending;
            Assert.That(ruleset.ScoreEarnsPerformancePoints(score), Is.False);
            score.BeatmapInfo.Status = BeatmapOnlineStatus.Ranked;
            var doubleTime = new TypeBeatModDoubleTime();
            doubleTime.SpeedChange.Value = 1.75;
            score.Mods = new Mod[] { new TypeBeatModPolyglot(), doubleTime };
            Assert.That(ruleset.ScoreEarnsPerformancePoints(score), Is.False);
            Assert.That(ruleset.ScoreCanDisplayPerformancePoints(score), Is.False);
        }

        private static BeatmapInfo beatmap(BeatmapLanguage language) => new BeatmapInfo
        {
            Status = BeatmapOnlineStatus.Ranked,
            Metadata = new BeatmapMetadata { Language = language },
        };

        private static ScoreInfo scoreFor(BeatmapInfo map, Mod[] mods) => new ScoreInfo
        {
            BeatmapInfo = map, Mods = mods, Passed = true, Rank = ScoreRank.X,
        };
    }
}
