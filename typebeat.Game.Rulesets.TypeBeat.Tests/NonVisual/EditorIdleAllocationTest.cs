// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Edit;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// An IDLE editor's lyric strip, line list and line boundaries band allocate nothing per update frame (backlog 377, the band added by 379),
    /// the strip's backing vocal lane included (backlog 445).
    /// Both re-sorted every line and rebuilt every row's strings (the pipe form, the caption, the
    /// time and index labels) on every frame, about 130 KiB a frame on a real map, which is a gen 0
    /// collection every couple of seconds while the mapper is only looking at the screen.
    ///
    /// <para>The three components are updated directly, N times on the update thread with nothing
    /// changing in between, and the thread's allocation over those updates is the measurement. That
    /// isolates them from the rest of the editor, whose own per-frame allocation is not this
    /// item's.</para>
    /// </summary>
    public partial class EditorIdleAllocationTest : EditorTestScene
    {
        private const int line_count = 40;
        private const int frames = 100;

        /// <summary>
        /// The allowance per idle frame, for ALL THREE components together. The fixed code measures 0;
        /// the allowance is there only so a framework detail (a lazily grown list, a pooled buffer)
        /// cannot make the pin flaky. The code this replaced allocated about 93 KiB a frame on this
        /// 40 line map (the strip 52 KiB of it, the list 43 KiB), so the pin fails by more than two
        /// orders of magnitude if either component goes back to rebuilding per frame.
        /// </summary>
        private const long max_bytes_per_frame = 256;

        protected override Ruleset CreateEditorRuleset() => new TypeBeatRuleset();

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.BeatmapInfo.Metadata.Artist = "Editor";
            beatmap.BeatmapInfo.Metadata.Title = "Idle";

            // A language with originals, so the line list takes its caption path and the original
            // view toggle is live, exactly as for a romanised map.
            beatmap.BeatmapInfo.Metadata.Language = BeatmapLanguage.Japanese;

            for (int i = 0; i < line_count; i++)
            {
                double start = 1000 + i * 3000;
                var units = new List<TimedUnit>();

                // Three words; the middle one subdivided so the row's pipe form is built by a join
                // rather than returned as the raw text.
                units.Add(new TimedUnit { Text = "sora", StartTime = start, EndTime = start + 800, Original = "空" });
                units.Add(new TimedUnit { Text = "kokoro", StartTime = start + 900, EndTime = start + 1800, SyllableBoundaries = new[] { start + 1200, start + 1500 } });
                units.Add(new TimedUnit { Text = "yume", StartTime = start + 1900, EndTime = start + 2700 });

                beatmap.HitObjects.Add(new TypeBeatHitObject
                {
                    StartTime = start,
                    LineIndex = i,
                    Granularity = TimingGranularity.Word,
                    Line = new LyricLine
                    {
                        RawText = "sora kokoro yume",
                        StartTime = start,
                        EndTime = start + 3000,
                        SingEndTime = start + 2700,
                        Units = units,
                        Original = $"空 心 夢 {i}",
                        // Backing vocals (backlog 445), one placed and one on its default place, so the
                        // backing lane's blocks, anchors and dotted lines are on the idle path too.
                        BackingVocals = new[] { new BackingVocal("ah", start + 200, start + 600), new BackingVocal("oh", start + 1200, start + 1600, 0.3) },
                    },
                });
            }

            return beatmap;
        }

        [Test]
        public void TestIdleFramesDoNotAllocate()
        {
            LyricTimeline strip = null!;
            LineListPanel list = null!;
            LineBoundariesBand band = null!;

            AddUntilStep("strip, list and band loaded", () =>
            {
                strip = Editor.ChildrenOfType<LyricTimeline>().SingleOrDefault()!;
                list = Editor.ChildrenOfType<LineListPanel>().SingleOrDefault()!;
                band = Editor.ChildrenOfType<LineBoundariesBand>().SingleOrDefault()!;
                return strip?.IsLoaded == true && band?.IsLoaded == true && list?.IsLoaded == true && list.ChildrenOfType<LineListPanel.LineRow>().Count() == line_count;
            });
            AddStep("stop the clock", () => EditorClock.Stop());
            AddWaitStep("settle", 10);

            // Non-vacuity: the strip really built its bands (it bails before the sort until the
            // waveform timeline is loaded), and the rows really carry the caption path.
            AddAssert("strip built", () => strip.DrawnBandEndTime(EditorBeatmap.HitObjects.OfType<TypeBeatHitObject>().First()) != null);
            // Three words per line: one shade, one line mark and three word ticks each.
            AddAssert("band built", () => band.DrawnElementCount == line_count * 5);
            AddAssert("rows show originals", () => list.ChildrenOfType<LineListPanel.LineRow>().All(r => r.OriginalCaptionText.Length > 0));
            AddAssert("rows draw rest marks", () => list.ChildrenOfType<LineListPanel.LineRow>().All(r => r.VisibleRestMarkers.Any()));

            foreach (bool originalView in new[] { false, true })
            {
                AddStep($"original view {originalView}", () => editState().ShowOriginalLyrics.Value = originalView);
                AddWaitStep("settle", 5);
                AddAssert($"idle frames allocate nothing (original view {originalView})", () =>
                {
                    // One unmeasured pass, so a change the step above made is not counted.
                    strip.UpdateSubTree();
                    list.UpdateSubTree();
                    band.UpdateSubTree();

                    long before = GC.GetAllocatedBytesForCurrentThread();

                    for (int i = 0; i < frames; i++)
                    {
                        strip.UpdateSubTree();
                        list.UpdateSubTree();
                        band.UpdateSubTree();
                    }

                    long perFrame = (GC.GetAllocatedBytesForCurrentThread() - before) / frames;
                    TestContext.Progress.WriteLine($"idle allocation, original view {originalView}: {perFrame} bytes per frame");
                    return perFrame <= max_bytes_per_frame;
                });
            }
        }

        private LyricEditState editState() => Editor.ChildrenOfType<LyricComposeScreen>().Single().EditState;
    }
}
