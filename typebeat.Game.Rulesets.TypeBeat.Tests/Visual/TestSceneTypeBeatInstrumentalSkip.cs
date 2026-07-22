// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Screens.Play;
using typebeat.Game.Tests.Visual;
using osuTK.Input;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// End-to-end coverage of backlog 5-fix over a synthetic map with one qualifying (>10s)
    /// instrumental gap — a headless analogue of the user's "immortal flame" report. Drives a real
    /// <see cref="Player"/> (whose test-scene base supplies the real prioritised GlobalActionContainer
    /// and a manual input manager) and pushes actual <c>Space</c> presses through the whole input
    /// stack, so it exercises the real routing to the deferred mid-song <see cref="SkipOverlay"/>:
    /// the overlay is dormant while the opening line is typed, becomes visible/skippable once the
    /// line has ended, a real Space press in the gap performs the skip (and Space while typing does
    /// not), and the overlay then cleans up with no ghost reappearing.
    /// </summary>
    public partial class TestSceneTypeBeatInstrumentalSkip : PlayerTestScene
    {
        protected override Ruleset CreatePlayerRuleset() => new TypeBeatRuleset();

        private TypeBeatPlayfield playfield => (TypeBeatPlayfield)Player.DrawableRuleset.Playfield;

        // The single deferred (mid-song) overlay; the intro/outro overlays are not deferred.
        private SkipOverlay instrumentalOverlay => Player.ChildrenOfType<SkipOverlay>().Single(o => o.IsDeferred);

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var beatmap = new Beatmap { HitObjects = new List<Rulesets.Objects.HitObject>() };

            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.BeatmapInfo.Metadata.Artist = "Test";
            beatmap.BeatmapInfo.Metadata.Title = "InstrumentalGap";

            // Line 0 ends at 2000 (seal ~2000 + grace). Line 1's vocals do not start until 14000, so
            // it self-activates at 14000 - CUE_LEAD (12500): a ~10.2s dead zone qualifies for a skip.
            // Skip target = activation - 3000 = 9500; the overlay window is [seal, 9500].
            addLine(beatmap, 0, "ab", 1000, 2000, 2000, 1000);
            addLine(beatmap, 1, "cd", 2000, 15000, 15000, 14000);

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

        [Test]
        public void TestSpaceSkipsInGapNotWhileTyping()
        {
            AddUntilStep("gameplay started", () => Player.GameplayClockContainer.CurrentTime > 0);

            AddAssert("one deferred instrumental overlay exists", () => Player.ChildrenOfType<SkipOverlay>().Count(o => o.IsDeferred) == 1);

            // While the opening line is being typed the skip machinery must be dormant, and a Space
            // press (Space is a typeable character) must NOT skip — it belongs to the typing surface.
            AddUntilStep("line 0 active", () => playfield.Engine.ActiveLineIndex == 0);
            AddAssert("overlay dormant during typing", () => !instrumentalOverlay.InSkipPeriod && !instrumentalOverlay.IsButtonVisible);
            AddStep("press Space while typing", () => InputManager.Key(Key.Space));
            AddAssert("no skip while typing", () => instrumentalOverlay.SkipCount == 0);
            AddAssert("clock did not jump", () => Player.GameplayClockContainer.CurrentTime < 9000);

            // Once the line has ended and the gap begins, the overlay opens its skip period.
            AddUntilStep("in the instrumental gap", () =>
                playfield.Engine.ActiveLineIndex == -1
                && playfield.Engine.NextUnsealedLineIndex == 1
                && Player.GameplayClockContainer.CurrentTime > 2200
                && Player.GameplayClockContainer.CurrentTime < 9000);

            AddUntilStep("overlay skip period open", () => instrumentalOverlay.InSkipPeriod);
            AddUntilStep("overlay button shown", () => instrumentalOverlay.IsButtonVisible);

            // A real Space press in the gap must skip through the full input stack, landing the player
            // at the run-up before line 1.
            AddStep("press Space in the gap", () => InputManager.Key(Key.Space));
            AddUntilStep("clock seeked past the gap", () => Player.GameplayClockContainer.CurrentTime >= 9400);
            AddAssert("exactly one skip recorded", () => instrumentalOverlay.SkipCount == 1);

            // Clean up like the intro overlay: no ghost overlay lingering or reappearing.
            AddUntilStep("overlay closed after skip", () => !instrumentalOverlay.InSkipPeriod && !instrumentalOverlay.IsButtonVisible);
            AddWaitStep("let a few frames pass", 5);
            AddAssert("overlay stays closed", () => !instrumentalOverlay.InSkipPeriod && !instrumentalOverlay.IsButtonVisible);
            AddAssert("no second skip", () => instrumentalOverlay.SkipCount == 1);
            AddAssert("gameplay still live", () => !playfield.Engine.IsFinished && !Player.GameplayState.HasFailed);
        }
    }
}
