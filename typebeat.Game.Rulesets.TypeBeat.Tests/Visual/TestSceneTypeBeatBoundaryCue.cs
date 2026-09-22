// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// The TWO "get ready" cues are Fletcher's alone. With the caret unpinned (no mods, the default)
    /// an unopened line cannot be typed, so the game counts a line as starting when its FIRST WORD
    /// starts and there is a single signal; the separate SOLID bar on the line BOUNDARY
    /// (<see cref="LyricLine.StartTime"/>) belongs to the pinned caret, which really is handed the
    /// line at that instant.
    ///
    /// <para>Line 1's boundary (2000) and its first word (4000) are a full 2 s apart, each with the
    /// standard 1500 ms lead, so the map has two SEPARATED windows: the boundary alone over
    /// [500,2000], the first word alone over [2500,4000]. That separation is deliberate - the tests
    /// read each window on its own and the headless runner steps the clock in coarse frames. The
    /// default gets a cue in the second window and NOTHING in the first; Fletcher gets both.</para>
    /// </summary>
    public partial class TestSceneTypeBeatBoundaryCue : PlayerTestScene
    {
        protected override bool HasCustomSteps => true;

        protected override Ruleset CreatePlayerRuleset() => new TypeBeatRuleset();

        private LyricStage stage => Player.ChildrenOfType<LyricStage>().Single();

        private double clock => Player.GameplayClockContainer.CurrentTime;

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var beatmap = new Beatmap { HitObjects = new List<Rulesets.Objects.HitObject>() };

            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.BeatmapInfo.Metadata.Artist = "Test";
            beatmap.BeatmapInfo.Metadata.Title = "BoundaryCue";

            // Line 0 runs the opening second; line 1's boundary lands the moment it ends (2000) while
            // its first word is a further two seconds away (4000), so [500,2000] is line 1's boundary
            // window and [2500,4000] its first-word window: no overlap, one window each.
            addLine(beatmap, 0, "ab", 1000, 2000, 2000, 1000);
            addLine(beatmap, 1, "cd", 2000, 6000, 6000, 4000);

            return beatmap;
        }

        private static void addLine(Beatmap beatmap, int index, string text, double start, double end, double singEnd, double vocalStart)
        {
            var line = new LyricLine
            {
                RawText = text,
                StartTime = start,
                EndTime = end,
                SingEndTime = singEnd,
                Units = new[] { new TimedUnit { Text = text, StartTime = vocalStart, EndTime = singEnd } },
            };

            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = start,
                LineIndex = index,
                Line = line,
                Granularity = TimingGranularity.Line,
            });
        }

        private void loadAndStart(Mod[] mods)
        {
            AddStep($"load player with {(mods.Length == 0 ? "no mods" : "Fletcher")}", () => LoadPlayer(mods));
            AddUntilStep("player loaded", () => Player.IsLoaded && Player.Alpha == 1);
            AddUntilStep("gameplay started", () => Player.GameplayClockContainer.CurrentTime > 0);
        }

        private void waitForClock(double time) =>
            AddUntilStep($"clock past {time:N0}", () => clock > time);

        [Test]
        public void TestBoundaryCueIsFletcherOnly()
        {
            loadAndStart(Array.Empty<Mod>());

            // Even under the opening line, whose boundary and first word coincide, the default shows
            // the translucent first-word bar only: one signal, and never the solid one. This alone
            // catches a regression that puts the boundary bar back on for everybody.
            AddUntilStep("first-word bar carries line 0's cue alone", () =>
                stage.ApproachCueTargetLine == 0 && stage.ApproachCueVisible && !stage.BoundaryCueVisible);

            // The boundary-only window: line 1's boundary is due but its first word is not. An
            // unpinned caret gets NOTHING here - the line has not started for it yet. The 1.2 s of
            // margin is what makes this safe to assert at an instant, coarse frames and all.
            waitForClock(1200);
            AddAssert("no cue while only the boundary is due", () =>
                clock < 2400 && !stage.ApproachCueVisible && !stage.BoundaryCueVisible);

            // The line is counted in when its FIRST WORD starts instead, and with the one bar.
            AddUntilStep("first word's own window still cues line 1", () =>
                clock > 2500 && clock < 4000 && stage.ApproachCueTargetLine == 1 && stage.ApproachCueVisible && !stage.BoundaryCueVisible);
        }

        [Test]
        public void TestBoundaryCueShownUnderFletcher()
        {
            loadAndStart(new Mod[] { new TypeBeatModFletcher() });

            // The pinned caret really is handed the line at its boundary, so that moment keeps its
            // own bar: the second signal the default mode does without.
            AddUntilStep("solid boundary bar is up while only the boundary is due", () =>
                clock > 1200 && clock < 2000 && stage.ApproachCueTargetLine == 1 && stage.BoundaryCueVisible);

            // ...and the first word keeps its own, which is the bar the default has all to itself.
            AddUntilStep("first-word bar also arrives under Fletcher", () =>
                clock > 2600 && clock < 4000 && stage.ApproachCueTargetLine == 1 && stage.ApproachCueVisible);
        }
    }
}
