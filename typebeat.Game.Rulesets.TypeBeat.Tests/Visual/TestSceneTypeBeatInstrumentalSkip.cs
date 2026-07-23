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
    /// End-to-end coverage of the mid-song instrumental skip over a map with one qualifying (>10s)
    /// instrumental gap, built through the PRODUCTION line resolution
    /// (<see cref="TimingJsonLoader.BuildLines"/>) so it carries the real decoder shape: line
    /// windows are CONTIGUOUS — the line before the gap stays active (complete, input-inert) for
    /// the entire instrumental, because its window runs to the next line's start. This is the
    /// exact shape of the user's "immortal flame" / "neon rain" maps, on which two prior
    /// hole-between-lines synthetic tests falsely passed. Drives a real <see cref="Player"/> and
    /// pushes actual key presses through the whole input stack: Space while the line is being
    /// typed is consumed by typing; after the line is fully typed (still active!) Space falls
    /// through to the deferred mid-song <see cref="SkipOverlay"/> and performs the skip.
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

            // Real decoder shape via BuildLines: line 0 sings "ab" 1000-2000, line 1 sings "cd"
            // from 14000 — so line 0's WINDOW runs to 14000 (contiguous; no dead zone anywhere).
            // Perceived gap = 14000 - 2000 = 12000 >= 10s -> qualifies. Skip period opens at
            // SingEnd + settle = 3000; line 1 activates at 14000; skip target = 11000.
            var built = TimingJsonLoader.BuildLines(new[]
            {
                new TimingJsonLoader.RawLine("ab", 1000, 2000, false,
                    new List<(string, double, double, double, List<double>)> { ("ab", 1000, 2000, 1.0, new List<double>()) }),
                new TimingJsonLoader.RawLine("cd", 14000, 15000, false,
                    new List<(string, double, double, double, List<double>)> { ("cd", 14000, 15000, 1.0, new List<double>()) }),
            }, songEndMs: 30000);

            for (int i = 0; i < built.Count; i++)
            {
                beatmap.HitObjects.Add(new TypeBeatHitObject
                {
                    StartTime = built[i].StartTime,
                    LineIndex = i,
                    Line = built[i],
                    Granularity = TimingGranularity.Word,
                });
            }

            return beatmap;
        }

        [Test]
        public void TestSpaceSkipsInGapNotWhileTyping()
        {
            AddUntilStep("gameplay started", () => Player.GameplayClockContainer.CurrentTime > 0);

            AddAssert("one deferred instrumental overlay exists", () => Player.ChildrenOfType<SkipOverlay>().Count(o => o.IsDeferred) == 1);

            // Contiguity sanity: the gap lives INSIDE line 0's window (its EndTime is line 1's start).
            AddAssert("line 0 window runs to line 1 start", () => playfield.Engine.Lines[0].EndTime == playfield.Engine.Lines[1].StartTime);

            // While the opening line is being typed the skip machinery must be dormant, and a Space
            // press (Space is a typeable character) must NOT skip — it belongs to the typing surface.
            AddUntilStep("line 0 active", () => playfield.Engine.ActiveLineIndex == 0);
            AddAssert("overlay dormant during typing", () => !instrumentalOverlay.InSkipPeriod && !instrumentalOverlay.IsButtonVisible);
            AddStep("press Space while typing", () => InputManager.Key(Key.Space));
            AddAssert("no skip while typing", () => instrumentalOverlay.SkipCount == 0);
            AddAssert("clock did not jump", () => Player.GameplayClockContainer.CurrentTime < 10000);

            // Finish the line. It stays ACTIVE (the real-map gap state) but becomes input-inert.
            AddStep("type 'a'", () => InputManager.Key(Key.A));
            AddStep("type 'b'", () => InputManager.Key(Key.B));
            AddAssert("line 0 complete but still active", () => playfield.Engine.IsLineComplete && playfield.Engine.ActiveLineIndex == 0);

            // Once the vocals have ended (+ settle) the overlay opens its skip period, while the
            // completed line is STILL the active line.
            AddUntilStep("in the instrumental gap", () =>
                playfield.Engine.ActiveLineIndex == 0
                && playfield.Engine.IsLineComplete
                && Player.GameplayClockContainer.CurrentTime > 3000
                && Player.GameplayClockContainer.CurrentTime < 10500);

            AddUntilStep("overlay skip period open", () => instrumentalOverlay.InSkipPeriod);
            AddUntilStep("overlay button shown", () => instrumentalOverlay.IsButtonVisible);

            // A real Space press in the gap must skip through the full input stack, landing the
            // player at the run-up before line 1 (skip target 11000 = activation 14000 - 3000).
            AddStep("press Space in the gap", () => InputManager.Key(Key.Space));
            AddUntilStep("clock seeked past the gap", () => Player.GameplayClockContainer.CurrentTime >= 10900);
            AddAssert("exactly one skip recorded", () => instrumentalOverlay.SkipCount == 1);

            // Clean up like the intro overlay: no ghost overlay lingering or reappearing.
            AddUntilStep("overlay closed after skip", () => !instrumentalOverlay.InSkipPeriod && !instrumentalOverlay.IsButtonVisible);
            AddWaitStep("let a few frames pass", 5);
            AddAssert("overlay stays closed", () => !instrumentalOverlay.InSkipPeriod && !instrumentalOverlay.IsButtonVisible);
            AddAssert("no second skip", () => instrumentalOverlay.SkipCount == 1);
            AddAssert("gameplay still live", () => !playfield.Engine.IsFinished && !Player.GameplayState.HasFailed);

            // The song then reaches line 1 normally: line 0 seals at the boundary with nothing
            // missed (it was fully typed) and line 1 takes over.
            AddUntilStep("line 1 becomes active", () => playfield.Engine.ActiveLineIndex == 1);
        }
    }
}
