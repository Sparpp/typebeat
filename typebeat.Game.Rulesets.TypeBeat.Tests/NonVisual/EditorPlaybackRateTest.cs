// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Audio;
using osu.Framework.Bindables;
using osu.Framework.Timing;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Screens.Edit;
using typebeat.Game.Screens.Edit.GameplayTest;
using typebeat.Game.Screens.Play;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class EditorPlaybackRateTest
    {
        [Test]
        public void EditorOwnsItsPlaybackTempoWhenTheControlsAreRecreated()
        {
            using var clock = new EditorClock();
            clock.PlaybackRate.Value = 0.5;
            Assert.That(clock.AudioAdjustments.AggregateTempo.Value, Is.EqualTo(0.5));
            clock.ResetSpeedAdjustments();
            Assert.That(clock.PlaybackRate.Value, Is.EqualTo(1));
            Assert.That(clock.AudioAdjustments.AggregateTempo.Value, Is.EqualTo(1));
            clock.PlaybackRate.Value = 0.75;
            Assert.That(clock.AudioAdjustments.AggregateTempo.Value, Is.EqualTo(0.75), "reset retains the owned playback adjustment");
        }

        [TestCase(0.25, 1)]
        [TestCase(0.5, 1)]
        [TestCase(0.75, 1)]
        [TestCase(1, 1)]
        [TestCase(0.5, 0.75)]
        [TestCase(0.5, 1.5)]
        [TestCase(1, 0.75)]
        [TestCase(1, 1.5)]
        public void EditorWpmUsesThePlaybackTempoAndRetainsOrdinaryRateMods(double editorRate, double modRate)
        {
            using var clock = new GameplayClockContainer(new StopwatchClock(), applyOffsets: false, requireDecoupling: false);
            var editorAdjustment = new BindableDouble(editorRate);
            clock.AdjustmentsFromPlayback.AddAdjustment(AdjustableProperty.Tempo, editorAdjustment);
            if (modRate < 1)
                new TypeBeatModHalfTime().ApplyToTrack(clock.AdjustmentsFromMods);
            else if (modRate > 1)
                new TypeBeatModDoubleTime().ApplyToTrack(clock.AdjustmentsFromMods);

            Assert.That(clock.GetTrueGameplayRate(), Is.EqualTo(modRate).Within(1e-6));
            var playback = new EditorTestPlayback(editorRate);
            double expectedRate = modRate * editorRate;
            var preview = run(playback.GetRateForStatistics(clock));
            var ordinary = run(expectedRate);
            Assert.That(preview.LiveWpm, Is.EqualTo(42 * expectedRate).Within(1e-8));
            Assert.That(preview.LiveRollingWpm, Is.EqualTo(ordinary.LiveRollingWpm).Within(1e-8),
                "the top WPM counter reads the engine directly without applying the editor tempo twice");
        }

        [TestCase(0.25, 1)]
        [TestCase(0.5, 1)]
        [TestCase(0.75, 1)]
        [TestCase(1, 1)]
        [TestCase(1.5, 1)]
        [TestCase(2, 1)]
        [TestCase(0.5, 0.75)]
        [TestCase(0.5, 1.5)]
        public void EditorPpUsesThePlaybackTempoForDifficultyAndShortMapPenalty(double editorRate, double modRate)
        {
            var playback = new EditorTestPlayback(editorRate);
            var source = Enumerable.Range(0, 8).Select(i => new LyricLine
            {
                RawText = "flame river cider amber",
                StartTime = i * 4000, EndTime = i * 4000 + 4000, SingEndTime = i * 4000 + 4000,
                Units = new[] { "flame", "river", "cider", "amber" }.Select((text, j) => new TimedUnit
                {
                    Text = text, StartTime = i * 4000 + j * 1000, EndTime = i * 4000 + (j + 1) * 1000,
                }).ToArray(),
            }).ToArray();
            var map = new TypeBeatBeatmap
            {
                HitObjects = source.Select(line => new TypeBeatHitObject { Line = line }).ToList(),
            };
            IReadOnlyList<Mod> mods = modRate < 1 ? new Mod[] { new TypeBeatModHalfTime() }
                : modRate > 1 ? new Mod[] { new TypeBeatModDoubleTime() } : System.Array.Empty<Mod>();
            double expectedRate = modRate * editorRate;
            var preview = PerformancePointsDisplay.DifficultyFor(map, mods, requireRankedMap: false, playbackRate: playback.StatisticsRate)!.Value;
            var expected = LyricDifficulty.ComputeDetail(source, expectedRate);
            Assert.That(preview.Stars, Is.EqualTo(expected.Stars).Within(1e-12));
            Assert.That(preview.DifficultCharacters, Is.EqualTo(expected.DifficultCharacters).Within(1e-12));
            double duration = PerformancePoints.PlayedDurationFor(source, mods) / playback.StatisticsRate;
            Assert.That(duration, Is.EqualTo(32 / expectedRate).Within(1e-12));
            var counts = new PerformancePoints.NoteCounts(160, 0, 0) { DifficultCharacters = preview.DifficultCharacters };
            var expectedCounts = counts with { DifficultCharacters = expected.DifficultCharacters };
            Assert.That(PerformancePoints.ForPlay(preview.Stars, counts, 1, 160, mods, duration),
                Is.EqualTo(PerformancePoints.ForPlay(expected.Stars, expectedCounts, 1, 160, mods, 32 / expectedRate)).Within(1e-12));

            // An editor preview must not alter the price of actual gameplay or finished scores.
            Assert.That(PerformancePointsDisplay.StarRatingFor(map, mods, requireRankedMap: false),
                Is.EqualTo(LyricDifficulty.Compute(source, modRate)).Within(1e-12));
        }

        [Test]
        public void EditorPreviewDoesNotMakeUnrankedOrCustomRateModsEligible()
        {
            var map = new TypeBeatBeatmap();
            var customRate = new TypeBeatModDoubleTime();
            customRate.SpeedChange.Value = 1.25;
            Assert.That(PerformancePointsDisplay.DifficultyFor(map, new Mod[] { customRate }, requireRankedMap: false, playbackRate: 0.8), Is.Null);
            Assert.That(PerformancePointsDisplay.DifficultyFor(map, new Mod[] { new TypeBeatModAutoplay() }, requireRankedMap: false, playbackRate: 0.5), Is.Null);
        }

        [Test]
        public void VerySlowPreviewBeyondTheRatingDurationLimitDoesNotCrashTheCounter()
        {
            var line = new LyricLine
            {
                RawText = "hello", StartTime = 0, EndTime = 120000, SingEndTime = 120000,
                Units = new[] { new TimedUnit { Text = "hello", StartTime = 0, EndTime = 120000 } },
            };
            var map = new TypeBeatBeatmap
            {
                HitObjects = new List<TypeBeatHitObject> { new TypeBeatHitObject { Line = line } },
            };
            Assert.That(PerformancePointsDisplay.DifficultyFor(map, null, requireRankedMap: false, playbackRate: 0.06), Is.Null);
            Assert.That(PerformancePointsDisplay.StarRatingFor(map, null, requireRankedMap: false), Is.Not.Null);
        }

        private static TypingEngine run(double wpmRate)
        {
            var map = new LyricBeatmap
            {
                Metadata = new LyricBeatmapMetadata { Artist = "Test", Title = "Rate", FolderPath = "", AudioFileName = "" },
                Granularity = TimingGranularity.Line,
                Lines = new[]
                {
                    new LyricLine
                    {
                        RawText = "ab cd", StartTime = 1000, EndTime = 10000, SingEndTime = 3000,
                        Units = new[]
                        {
                            new TimedUnit { Text = "ab", StartTime = 1000, EndTime = 2000 },
                            new TimedUnit { Text = "cd", StartTime = 2000, EndTime = 3000 },
                        },
                    },
                    new LyricLine
                    {
                        RawText = "ef", StartTime = 10000, EndTime = 12000, SingEndTime = 11000,
                        Units = new[] { new TimedUnit { Text = "ef", StartTime = 10000, EndTime = 11000 } },
                    },
                },
            };
            var engine = new TypingEngine(map);
            engine.Update(0, wpmRate);
            engine.Update(500, wpmRate);
            engine.Update(1000, wpmRate);
            engine.ProcessKey('a', 1000);
            engine.Update(1500, wpmRate);
            engine.ProcessKey('b', 1500);
            engine.Update(2000, wpmRate);
            engine.ProcessKey(' ', 2000);
            engine.ProcessKey('c', 2000);
            engine.Update(2500, wpmRate);
            engine.ProcessKey('d', 2500);
            engine.Update(9000, wpmRate);
            engine.Update(10000, wpmRate);
            engine.ProcessKey('e', 10000);
            engine.Update(10500, wpmRate);
            engine.ProcessKey('f', 10500);
            engine.Update(12000, wpmRate);
            return engine;
        }
    }
}
