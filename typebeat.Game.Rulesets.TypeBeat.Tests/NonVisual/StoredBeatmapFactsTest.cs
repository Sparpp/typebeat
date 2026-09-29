// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using osu.Framework.Extensions;
using osu.Framework.Platform;
using Realms;
using typebeat.Game.Beatmaps;
using typebeat.Game.Collections;
using typebeat.Game.Database;
using typebeat.Game.Graphics.Carousel;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Scoring;
using typebeat.Game.Screens.Menu;
using typebeat.Game.Screens.Select;
using typebeat.Game.Screens.Select.Filter;
using typebeat.Game.Tests.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Backlog 335: a difficulty's target WPM and whether it declares an intro beatdrop are STORED on its realm row
    /// (<see cref="StoredBeatmapFacts"/>, realm schema 59) so song select can group the whole library by them without
    /// decoding. Pins the migration, the computation, and the two groupings built on them.
    /// </summary>
    [TestFixture]
    public class StoredBeatmapFactsTest
    {
        // ---- storage ----

        [Test]
        public void ANewRowIsUnprocessed()
        {
            var beatmap = new BeatmapInfo();

            Assert.Multiple(() =>
            {
                Assert.That(beatmap.TargetWpm, Is.EqualTo(-1));
                Assert.That(beatmap.HasIntroBeatdrop, Is.False);
                Assert.That(StoredBeatmapFacts.IsProcessed(beatmap), Is.False);
            });
        }

        [Test]
        public void Schema59MarksEveryExistingRowUnprocessed()
        {
            string directory = Path.Combine(Path.GetTempPath(), "typebeat-facts-migration-" + Guid.NewGuid().ToString("N"));
            const string filename = "client.realm";

            try
            {
                var storage = new NativeStorage(directory);
                Guid id = Guid.Empty;

                using (var realm = Realm.GetInstance(new RealmConfiguration(storage.GetFullPath(filename, true)) { SchemaVersion = 58 }))
                {
                    realm.Write(() =>
                    {
                        var ruleset = realm.Add(new RulesetInfo("typebeat", "type!beat", "typebeat.Game.Rulesets.TypeBeat.TypeBeatRuleset, typebeat.Game.Rulesets.TypeBeat", 0));
                        var set = new BeatmapSetInfo();

                        // What realm hands a column that did not exist in a schema 58 file: the type's own
                        // default, which for a double is 0 and would read as "processed, nothing typeable".
                        var beatmap = new BeatmapInfo(ruleset, new BeatmapDifficulty(), new BeatmapMetadata()) { BeatmapSet = set, TargetWpm = 0 };
                        set.Beatmaps.Add(beatmap);

                        realm.Add(set);
                        id = beatmap.ID;
                    });
                }

                using (var access = new RealmAccess(storage, filename))
                {
                    var (wpm, beatdrop) = access.Run(r =>
                    {
                        var b = r.Find<BeatmapInfo>(id)!;
                        return (b.TargetWpm, b.HasIntroBeatdrop);
                    });

                    Assert.Multiple(() =>
                    {
                        Assert.That(wpm, Is.EqualTo(StoredBeatmapFacts.UNPROCESSED), "the migration marks the row for the background pass");
                        Assert.That(beatdrop, Is.False);
                    });
                }
            }
            finally
            {
                try
                {
                    Directory.Delete(directory, true);
                }
                catch
                {
                    // a leftover temp directory is not worth failing a test over.
                }
            }
        }

        // ---- computation ----

        [Test]
        public void ComputeReadsTheHeadlineTargetAndTheBeatdrop()
        {
            var plain = map(beatdrop: null);
            var dropped = map(beatdrop: 4000);

            var plainFacts = StoredBeatmapFacts.Compute(new TestWorkingBeatmap(plain));
            var droppedFacts = StoredBeatmapFacts.Compute(new TestWorkingBeatmap(dropped));

            double headline = map(beatdrop: null).GetTypingPace()!.TargetWpm;

            Assert.Multiple(() =>
            {
                Assert.That(headline, Is.GreaterThan(0), "the fixture must have a pace to measure");
                Assert.That(plainFacts.TargetWpm, Is.EqualTo(headline).Within(1e-9), "the stored figure is the wedge's rate 1.0 headline");
                Assert.That(droppedFacts.TargetWpm, Is.EqualTo(headline).Within(1e-9), "a beatdrop is not map content");
                Assert.That(plainFacts.HasIntroBeatdrop, Is.False);
                Assert.That(droppedFacts.HasIntroBeatdrop, Is.True);
            });
        }

        [Test]
        public void AMapWithNothingTypeableIsProcessedAtZero()
        {
            var empty = new TypeBeatBeatmap { BeatmapInfo = { Ruleset = new TypeBeatRuleset().RulesetInfo } };

            var facts = StoredBeatmapFacts.Compute(new TestWorkingBeatmap(empty));

            // 0, not -1: a genuinely paceless map must not be re-queued on every startup.
            Assert.That(facts.TargetWpm, Is.EqualTo(0));
        }

        [Test]
        public void AnyIntroBeatdropTrustsOnlyProcessedRows()
        {
            BeatmapInfo row(double wpm, bool drop) => new BeatmapInfo { TargetWpm = wpm, HasIntroBeatdrop = drop };

            Assert.Multiple(() =>
            {
                Assert.That(StoredBeatmapFacts.AnyIntroBeatdrop(new[] { row(100, false), row(90, true) }), Is.True);
                Assert.That(StoredBeatmapFacts.AnyIntroBeatdrop(new[] { row(100, false), row(0, false) }), Is.False);
                Assert.That(StoredBeatmapFacts.AnyIntroBeatdrop(new[] { row(100, false), row(-1, false) }), Is.Null, "an unprocessed row leaves it open");
                Assert.That(StoredBeatmapFacts.AnyIntroBeatdrop(new[] { row(-1, true) }), Is.Null, "an unprocessed row's bool is not trusted");
                Assert.That(StoredBeatmapFacts.AnyIntroBeatdrop(new[] { row(-1, false), row(120, true) }), Is.True, "a processed beatdrop settles it");
            });
        }

        // ---- group by WPM ----

        [TestCase(59.4, "Under 60 WPM")]
        [TestCase(59.9, "60 to 80 WPM")] // the wedge shows it as "60 WPM", so it groups there
        [TestCase(60, "60 to 80 WPM")]
        [TestCase(79.4, "60 to 80 WPM")]
        [TestCase(79.9, "80 to 100 WPM")]
        [TestCase(80, "80 to 100 WPM")]
        [TestCase(199.4, "180 to 200 WPM")]
        [TestCase(199.5, "200 WPM and over")]
        [TestCase(200, "200 WPM and over")]
        [TestCase(412, "200 WPM and over")]
        [TestCase(12, "Under 60 WPM")]
        [TestCase(0, "Unknown")]
        [TestCase(-1, "Unknown")]
        public void WpmBucketEdges(double targetWpm, string expected)
            => Assert.That(title(BeatmapCarouselFilterGrouping.DefineGroupByWpm(targetWpm).Single()), Is.EqualTo(expected));

        [Test]
        public void WpmGroupsAreOrderedSlowestFirstWithUnknownLast()
        {
            double[] figures = { -1, 250, 40, 150, 70, 199 };

            var orders = figures.Select(f => BeatmapCarouselFilterGrouping.DefineGroupByWpm(f).Single().Order).ToList();

            Assert.That(figures.OrderBy(f => orders[Array.IndexOf(figures, f)]), Is.EqualTo(new double[] { 40, 70, 150, 199, 250, -1 }));
        }

        [Test]
        public void WpmGroupingIsPerDifficulty()
        {
            var set = new BeatmapSetInfo();
            var easy = difficulty(set, 55, false);
            var hard = difficulty(set, 145, false);
            var unprocessed = difficulty(set, -1, false);

            var groups = run(GroupMode.Wpm, set);

            Assert.Multiple(() =>
            {
                Assert.That(groupOf(groups, easy), Is.EqualTo("Under 60 WPM"));
                Assert.That(groupOf(groups, hard), Is.EqualTo("140 to 160 WPM"));
                Assert.That(groupOf(groups, unprocessed), Is.EqualTo("Unknown"));
                Assert.That(groups.Keys, Is.EqualTo(new[] { "Under 60 WPM", "140 to 160 WPM", "Unknown" }), "slowest first, unknown last");
            });
        }

        // ---- group by enabled on intro ----

        [TestCase(null, true, true)]
        [TestCase(null, false, false)]
        [TestCase(true, false, true)]
        [TestCase(true, true, true)]
        [TestCase(false, true, false)]
        [TestCase(false, false, false)]
        public void EnabledOnIntroIsTheIntrosOwnRule(bool? inclusion, bool anyBeatdrop, bool expected)
        {
            var set = new BeatmapSetInfo { IntroPoolInclusion = inclusion };
            difficulty(set, 100, false);
            difficulty(set, 120, anyBeatdrop);

            string group = title(BeatmapCarouselFilterGrouping.DefineGroupByIntro(set).Single());

            Assert.Multiple(() =>
            {
                Assert.That(group, Is.EqualTo(expected ? "Enabled on intro" : "Not on intro"));
                Assert.That(expected, Is.EqualTo(IntroBeatdropPool.IsCandidate(inclusion, anyBeatdrop)), "the case table is the intro's own");
                Assert.That(IntroBeatdropPool.StoredCandidacy(inclusion, set.Beatmaps), Is.EqualTo(expected), "and the intro's decode-free pre-check agrees");
            });
        }

        [Test]
        public void EnabledOnIntroReadsAnUnprocessedRowAsNoBeatdrop()
        {
            var set = new BeatmapSetInfo();
            difficulty(set, -1, true);

            Assert.Multiple(() =>
            {
                Assert.That(title(BeatmapCarouselFilterGrouping.DefineGroupByIntro(set).Single()), Is.EqualTo("Not on intro"));
                Assert.That(IntroBeatdropPool.StoredCandidacy(null, set.Beatmaps), Is.Null, "the intro decodes it instead");
            });
        }

        [Test]
        public void EnabledOnIntroGroupsWholeSetsEnabledFirst()
        {
            var inPool = new BeatmapSetInfo();
            var inPoolEasy = difficulty(inPool, 60, false);
            var inPoolHard = difficulty(inPool, 150, true);

            var outOfPool = new BeatmapSetInfo { IntroPoolInclusion = false };
            var outOfPoolOnly = difficulty(outOfPool, 90, true);

            var groups = run(GroupMode.EnabledOnIntro, outOfPool, inPool);

            Assert.Multiple(() =>
            {
                Assert.That(groupOf(groups, inPoolEasy), Is.EqualTo("Enabled on intro"), "a set's difficulties land together");
                Assert.That(groupOf(groups, inPoolHard), Is.EqualTo("Enabled on intro"));
                Assert.That(groupOf(groups, outOfPoolOnly), Is.EqualTo("Not on intro"));
                Assert.That(groups.Keys, Is.EqualTo(new[] { "Enabled on intro", "Not on intro" }));
            });
        }

        // ---- sort by WPM ----

        [Test]
        public void SortByWpmOrdersSetsByTheirFastestDifficulty()
        {
            var slow = new BeatmapSetInfo();
            difficulty(slow, 70, false);

            var fast = new BeatmapSetInfo();
            difficulty(fast, 50, false);
            difficulty(fast, 180, false);

            var unknown = new BeatmapSetInfo();
            difficulty(unknown, -1, false);

            var sorting = new BeatmapCarouselFilterSorting(() => new FilterCriteria { Sort = SortMode.Wpm });
            var sorted = sorting.Run(new[] { fast, unknown, slow }.SelectMany(s => s.Beatmaps).Select(b => new CarouselItem(b)).ToList(), CancellationToken.None)
                                .GetResultSafely()
                                .Select(i => ((BeatmapInfo)i.Model).BeatmapSet)
                                .Distinct()
                                .ToList();

            Assert.That(sorted, Is.EqualTo(new[] { unknown, slow, fast }));
        }

        // ---- helpers ----

        private static TypeBeatBeatmap map(double? beatdrop)
        {
            var beatmap = new TypeBeatBeatmap
            {
                BeatmapInfo = { Ruleset = new TypeBeatRuleset().RulesetInfo },
                IntroBeatdropTime = beatdrop,
            };

            for (int i = 0; i < 12; i++)
            {
                double at = i * 2000;
                var units = new List<TimedUnit>();

                for (int w = 0; w < 4; w++)
                    units.Add(new TimedUnit { Text = "flame", StartTime = at + w * 400, EndTime = at + (w + 1) * 400 });

                beatmap.HitObjects.Add(new TypeBeatHitObject
                {
                    StartTime = at,
                    Granularity = TimingGranularity.Line,
                    LineIndex = i,
                    Line = new LyricLine
                    {
                        RawText = string.Join(' ', units.Select(u => u.Text)),
                        StartTime = at,
                        EndTime = at + 1800,
                        SingEndTime = at + 1800,
                        Units = units,
                    },
                });
            }

            return beatmap;
        }

        private static BeatmapInfo difficulty(BeatmapSetInfo set, double targetWpm, bool hasIntroBeatdrop)
        {
            var beatmap = new BeatmapInfo(new TypeBeatRuleset().RulesetInfo, new BeatmapDifficulty(), new BeatmapMetadata())
            {
                BeatmapSet = set,
                DifficultyName = $"{targetWpm} WPM",
                TargetWpm = targetWpm,
                HasIntroBeatdrop = hasIntroBeatdrop,
            };

            set.Beatmaps.Add(beatmap);
            return beatmap;
        }

        /// <summary>Runs the real grouping filter and returns each group's title, in carousel order, with its difficulties.</summary>
        private static Dictionary<string, List<BeatmapInfo>> run(GroupMode mode, params BeatmapSetInfo[] sets)
        {
            var grouping = new BeatmapCarouselFilterGrouping
            {
                GetCriteria = () => new FilterCriteria { Group = mode },
                GetCollections = () => new List<BeatmapCollection>(),
                GetLocalUserTopRanks = _ => new Dictionary<Guid, ScoreRank>(),
                GetFavouriteBeatmapSets = () => new HashSet<int>(),
            };

            var items = sets.SelectMany(s => s.Beatmaps).Select(b => new CarouselItem(b)).ToList();
            var result = grouping.Run(items, CancellationToken.None).GetResultSafely();

            var groups = new Dictionary<string, List<BeatmapInfo>>();
            List<BeatmapInfo>? current = null;

            foreach (var item in result)
            {
                switch (item.Model)
                {
                    case GroupDefinition group:
                        groups[title(group)] = current = new List<BeatmapInfo>();
                        break;

                    case GroupedBeatmap grouped:
                        current!.Add(grouped.Beatmap);
                        break;
                }
            }

            return groups;
        }

        private static string groupOf(Dictionary<string, List<BeatmapInfo>> groups, BeatmapInfo beatmap)
            => groups.Single(g => g.Value.Contains(beatmap)).Key;

        private static string title(GroupDefinition group) => group.Title.ToString();
    }
}
