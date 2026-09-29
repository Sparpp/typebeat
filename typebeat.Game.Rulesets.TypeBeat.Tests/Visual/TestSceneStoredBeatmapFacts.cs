// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using typebeat.Game.Beatmaps;
using typebeat.Game.Database;
using typebeat.Game.Online.API;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Play;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Backlog 335: the stored target WPM and intro beatdrop (<see cref="StoredBeatmapFacts"/>) are written where the
    /// map is decoded anyway. An editor save writes them (through <see cref="BeatmapUpdater.Process"/>, the path
    /// import takes too), and <see cref="BackgroundDataStoreProcessor"/> backfills a row still marked unprocessed,
    /// which is what a library migrated to realm schema 59 starts as.
    /// </summary>
    [TestFixture]
    public partial class TestSceneStoredBeatmapFacts : OsuTestScene
    {
        [Resolved]
        private BeatmapManager beatmaps { get; set; } = null!;

        [Resolved]
        private RealmAccess realm { get; set; } = null!;

        [Resolved]
        private IAPIProvider api { get; set; } = null!;

        // The processor sleeps while anyone might be playing, and reads a missing play-state provider as "playing".
        [Cached(typeof(ILocalUserPlayInfo))]
        private readonly NotPlaying notPlaying = new NotPlaying();

        private Guid plainId;
        private Guid droppedId;

        [Test]
        public void SaveWritesAndBackgroundPassBackfills()
        {
            double headline = 0;

            AddStep("save a map without and a map with a beatdrop", () =>
            {
                plainId = save(map(null));
                droppedId = save(map(4000));

                // Read back from the SAVED file, which is what every later decode sees: the round trip through the
                // native encoder re-derives some timings, so the in-memory fixture's own figure is not the one.
                var working = beatmaps.GetWorkingBeatmap(realm.Run(r => r.Find<BeatmapInfo>(plainId)!.Detach()));
                headline = ((IHasTypingPace)working.GetPlayableBeatmap(working.BeatmapInfo.Ruleset)).GetTypingPace()!.TargetWpm;
            });

            AddAssert("fixture has a pace", () => headline, () => Is.GreaterThan(0));
            AddAssert("save stored the target", () => stored(plainId).wpm, () => Is.EqualTo(headline).Within(1e-9));
            AddAssert("save stored no beatdrop", () => stored(plainId).drop, () => Is.False);
            AddAssert("save stored the beatdrop", () => stored(droppedId).drop, () => Is.True);

            AddStep("mark both unprocessed, as the migration does", () => realm.Write(r =>
            {
                foreach (var id in new[] { plainId, droppedId })
                {
                    var b = r.Find<BeatmapInfo>(id)!;
                    b.TargetWpm = StoredBeatmapFacts.UNPROCESSED;
                    b.HasIntroBeatdrop = false;
                }
            }));

            AddAssert("unprocessed", () => stored(plainId).wpm, () => Is.EqualTo(-1));

            TestProcessor processor = null!;

            AddStep("run the background pass", () => Add(processor = new TestProcessor()));

            AddUntilStep("both backfilled", () => stored(plainId).wpm >= 0 && stored(droppedId).wpm >= 0);
            AddAssert("target backfilled", () => stored(plainId).wpm, () => Is.EqualTo(headline).Within(1e-9));
            AddAssert("no beatdrop backfilled", () => stored(plainId).drop, () => Is.False);
            AddAssert("beatdrop backfilled", () => stored(droppedId).drop, () => Is.True);
            AddAssert("same target either way", () => stored(droppedId).wpm, () => Is.EqualTo(headline).Within(1e-9));

            // Let the whole pass finish here rather than leave it running into whichever scene runs next.
            AddUntilStep("pass finished", () => processor.Finished);
        }

        private partial class TestProcessor : BackgroundDataStoreProcessor
        {
            public bool Finished => ProcessingTask.IsCompleted;
        }

        private class NotPlaying : ILocalUserPlayInfo
        {
            public IBindable<LocalUserPlayingState> PlayingState { get; } = new Bindable<LocalUserPlayingState>(LocalUserPlayingState.NotPlaying);
        }

        private Guid save(TypeBeatBeatmap content)
        {
            var ruleset = realm.Run(r => r.Find<RulesetInfo>("typebeat")!.Detach());
            var working = beatmaps.CreateNew(ruleset, api.LocalUser.Value);

            content.BeatmapInfo = working.BeatmapInfo;
            beatmaps.Save(working.BeatmapInfo, content);

            return working.BeatmapInfo.ID;
        }

        private (double wpm, bool drop) stored(Guid id) => realm.Run(r =>
        {
            var b = r.Find<BeatmapInfo>(id)!;
            return (b.TargetWpm, b.HasIntroBeatdrop);
        });

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
                    units.Add(new TimedUnit { Text = "flame", StartTime = at + w * 400, EndTime = at + (w + 1) * 400, Source = TimingSource.Explicit });

                beatmap.HitObjects.Add(new TypeBeatHitObject
                {
                    StartTime = at,
                    Granularity = TimingGranularity.Word,
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
    }
}
