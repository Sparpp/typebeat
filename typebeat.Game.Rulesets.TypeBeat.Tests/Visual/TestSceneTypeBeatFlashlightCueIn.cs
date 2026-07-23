// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Testing;
using osu.Framework.Utils;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// The reworked Flashlight must not leave the player staring at a fully dark line they are about
    /// to type during a CUE-IN. This drives a real gameplay clock through a pre-roll (the first line
    /// starts at 5s), so there is a window where no line is active yet but the approach cue is
    /// counting the first line in. During that window the flashlight anchors on the upcoming line's
    /// first char, so its opening letters are readable before it activates, while chars past the
    /// budget stay dark (proving it is a WINDOW, not a blanket reveal).
    /// </summary>
    public partial class TestSceneTypeBeatFlashlightCueIn : PlayerTestScene
    {
        private const int radius = 5;

        protected override bool HasCustomSteps => true;

        protected override Ruleset CreatePlayerRuleset() => new TypeBeatRuleset();

        private TypeBeatPlayfield playfield => (TypeBeatPlayfield)Player.DrawableRuleset.Playfield;
        private TypingEngine engine => playfield.Engine;
        private LyricStage stage => Player.ChildrenOfType<LyricStage>().Single();

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var beatmap = new Beatmap { HitObjects = new List<Rulesets.Objects.HitObject>() };

            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.BeatmapInfo.Metadata.Artist = "Test";
            beatmap.BeatmapInfo.Metadata.Title = "FlashlightCueIn";

            // First line starts at 5s: early gameplay is a live pre-roll (no active line) whose final
            // 1.5s is the approach cue for line 0. Ten letters so the window (radius 5) lights the head
            // and leaves the tail dark.
            addLine(beatmap, 0, "abcdefghij", 5000, 6500, 6500);
            addLine(beatmap, 1, "klmnopqrst", 6500, 8000, 8000);

            return beatmap;
        }

        private static void addLine(Beatmap beatmap, int index, string text, double start, double end, double singEnd)
        {
            var line = new LyricLine
            {
                RawText = text,
                StartTime = start,
                EndTime = end,
                SingEndTime = singEnd,
                Units = new[] { new TimedUnit { Text = text, StartTime = start, EndTime = singEnd } },
            };

            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = start,
                LineIndex = index,
                Line = line,
                Granularity = TimingGranularity.Line,
            });
        }

        [Test]
        public void TestCueInLightsUpcomingLineHead()
        {
            AddStep("load player with Flashlight", () => LoadPlayer(new Mod[] { new TypeBeatModFlashlight() }));
            AddUntilStep("player loaded", () => Player.IsLoaded && Player.Alpha == 1);
            AddUntilStep("gameplay started", () => Player.GameplayClockContainer.CurrentTime > 0);

            // The heart of the fix, asserted atomically so the clock cannot race past activation: while
            // NO line is active yet but the cue is counting line 0 in, that line's first char is lit.
            AddUntilStep("cue-in lights upcoming line head before activation", () =>
                engine.ActiveLineIndex == -1
                && stage.ApproachCueVisible
                && stage.ApproachCueTargetLine == 0
                && stage.DisplayAt(0)!.CellAlpha(0) > 0.5f);

            // It is a window, not a blanket reveal: a char past the budget stays dark during cue-in.
            AddAssert("char past the budget stays dark during cue-in", () =>
                engine.ActiveLineIndex <= 0
                && stage.DisplayAt(0)!.CellAlpha(radius + 3) < 0.05f);

            // Once the line activates the head is still lit (caret sits at its first char).
            AddUntilStep("first line active", () => engine.ActiveLineIndex == 0);
            AddAssert("head still lit at activation", () => stage.DisplayAt(0)!.CellAlpha(0) > 0.5f);
        }

        /// <summary>
        /// The layout-snap regression. During cue-in the flashlight lights only the head of line 0; the
        /// rest of the line is hidden (alpha 0). Those hidden cells must still occupy their layout slots
        /// so the line renders at its FULL width and does not re-centre onto the lit head, which would
        /// snap the whole line sideways the moment it activates and the window slides. We pin this two
        /// ways: the display's on-screen width equals its full-line width even while most cells are dark,
        /// and cell 0 does not move between cue-in and activation.
        /// </summary>
        [Test]
        public void TestLineLayoutStableDuringCueIn()
        {
            AddStep("load player with Flashlight", () => LoadPlayer(new Mod[] { new TypeBeatModFlashlight() }));
            AddUntilStep("player loaded", () => Player.IsLoaded && Player.Alpha == 1);
            AddUntilStep("gameplay started", () => Player.GameplayClockContainer.CurrentTime > 0);

            float cueInCell0X = 0f;

            AddUntilStep("cue-in shows line 0 head", () =>
                engine.ActiveLineIndex == -1
                && stage.ApproachCueTargetLine == 0
                && stage.DisplayAt(0)!.CellAlpha(0) > 0.5f);

            AddAssert("line 0 occupies full width during cue-in (hidden cells still present)", () =>
            {
                var d = stage.DisplayAt(0)!;
                cueInCell0X = d.CellScreenPosition(0).X;

                // A tail char is genuinely dark (proving most of the line is hidden)...
                bool tailDark = d.CellAlpha(d.CellCount - 1) < 0.05f;
                // ...yet the line still measures its full width (the hidden cells hold their slots).
                bool fullWidth = Precision.AlmostEquals(d.DrawWidth, d.FullOnScreenWidth, 1f);
                return tailDark && fullWidth;
            });

            AddUntilStep("first line active", () => engine.ActiveLineIndex == 0);

            AddAssert("line 0 still full width after activation", () =>
                Precision.AlmostEquals(stage.DisplayAt(0)!.DrawWidth, stage.DisplayAt(0)!.FullOnScreenWidth, 1f));

            AddAssert("cell 0 did not snap sideways from cue-in to activation", () =>
                Precision.AlmostEquals(stage.DisplayAt(0)!.CellScreenPosition(0).X, cueInCell0X, 1f));
        }
    }
}
