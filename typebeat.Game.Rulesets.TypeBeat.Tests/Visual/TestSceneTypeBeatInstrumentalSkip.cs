// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
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
    /// windows are CONTIGUOUS: the line before the gap stays active (complete, input-inert) for
    /// the entire instrumental, because its window runs to the next line's start. This is the
    /// exact shape of the user's "immortal flame" / "neon rain" maps, on which two prior
    /// hole-between-lines synthetic tests falsely passed. Drives a real <see cref="Player"/> and
    /// pushes actual key presses through the whole input stack: Space while the line is being
    /// typed is consumed by typing; after the line is fully typed Space falls through to the
    /// deferred mid-song <see cref="SkipOverlay"/> and performs the skip.
    ///
    /// <para>Since backlog 208 the caret is UNPINNED by default, so finishing line 0 parks the
    /// player, untouched, at the head of line 1 while the song is still inside line 0's window.
    /// That is exactly the state <c>TypeBeatPlayfield</c>'s narrow Space fall-through is gated on
    /// (no song window open, active line untouched), so the scene now drives the shipped arm of
    /// that gate rather than the pinned one it was written against.</para>
    /// </summary>
    public partial class TestSceneTypeBeatInstrumentalSkip : PlayerTestScene
    {
        protected override Ruleset CreatePlayerRuleset() => new TypeBeatRuleset();

        private Configuration.TypeBeatRulesetConfigManager config
            => (Configuration.TypeBeatRulesetConfigManager)RulesetConfigs.GetConfigFor(new TypeBeatRuleset())!;

        /// <summary>
        /// The Space probe below has to leave the line RECOVERABLE, and with word skipping on a
        /// space pressed inside a word abandons it whole, which would end the line before it is
        /// typed. Declared rather than inherited, exactly as <c>TestSceneTypeBeatWordInput</c> does.
        /// </summary>
        public override void SetUpSteps()
        {
            AddStep("word skipping off", () => config.SetValue(Configuration.TypeBeatRulesetSetting.SpaceSkipsWord, false));
            base.SetUpSteps();
        }

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
            // from 14000, so line 0's WINDOW runs to 14000 (contiguous; no dead zone anywhere).
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
            // press (Space is a typeable character) must NOT skip; it belongs to the typing surface.
            AddUntilStep("line 0 active", () => playfield.Engine.ActiveLineIndex == 0);
            AddAssert("overlay dormant during typing", () => !instrumentalOverlay.InSkipPeriod && !instrumentalOverlay.IsButtonVisible);
            AddStep("press Space while typing", () => InputManager.Key(Key.Space));
            AddAssert("no skip while typing", () => instrumentalOverlay.SkipCount == 0);
            AddAssert("clock did not jump", () => Player.GameplayClockContainer.CurrentTime < 10000);
            AddAssert("it went into the line as a typo instead", () => playfield.Engine.Lines[0].Cells[0].State == CellState.Wrong);

            // Take the probe back, then type the line out, so what follows is a player who FINISHED
            // line 0 rather than one who spoiled its first cell.
            AddStep("backspace the probe away", () => InputManager.Key(Key.BackSpace));
            AddStep("type 'a'", () => InputManager.Key(Key.A));
            AddStep("type 'b'", () => InputManager.Key(Key.B));

            // Finishing it hands the caret straight to line 1 (rush freedom, the default since
            // backlog 208) while the SONG is still inside line 0's window. Untouched, so Space is
            // still the skip key rather than a typing key.
            AddAssert("line 0 done, caret parked untouched at the head of line 1", () =>
                playfield.Engine.ActiveLineIndex == 1
                && playfield.Engine.CaretIndex == 0
                && playfield.Engine.ActiveLineUntouched
                && playfield.Engine.NextUnsealedLineIndex == 0);

            // Once the vocals have ended (+ settle) the overlay opens its skip period, with the
            // player still parked at the head of line 1 and the song still on line 0.
            // The song's window never closes here (the windows are contiguous), which is exactly why
            // the key handler asks whether the song is on the CARET's line rather than whether any
            // window is open.
            AddAssert("the song's own window is still open", () => playfield.Engine.SongWindowOpen);

            AddUntilStep("in the instrumental gap", () =>
                playfield.Engine.ActiveLineIndex == 1
                && playfield.Engine.ActiveLineUntouched
                && !playfield.Engine.SongIsOnTheCaretsLine
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
