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

            // Finishing it PARKS the caret past the end of line 0: the rush bound (backlog 218) does
            // not open entry into line 1 until 12500 (its activation, 14000, less the 1500 ms drag
            // grace it mirrors), and the song is still inside line 0's window. A complete line takes
            // no input, so Space is still the skip key rather than a typing key, through the key
            // handler's own IsLineComplete fall-through.
            AddAssert("line 0 done, caret parked past its end", () =>
                playfield.Engine.ActiveLineIndex == 0
                && playfield.Engine.IsLineComplete
                && playfield.Engine.NextUnsealedLineIndex == 0);

            // Once the vocals have ended (+ settle) the overlay opens its skip period, with the
            // player parked on a finished line and the song still on it too. The song's window never
            // closes here (the windows are contiguous), which is why neither that nor the caret's
            // own line can be what tells the key handler a skip is possible: being FINISHED is.
            AddAssert("the song's own window is still open", () => playfield.Engine.SongWindowOpen);

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

            // The song then reaches line 1 normally: the rush bound opens at 12500, the deferred roll
            // hands the parked caret over, and line 0 seals at the boundary with nothing missed (it
            // was fully typed).
            AddUntilStep("line 1 becomes active", () => playfield.Engine.ActiveLineIndex == 1);
        }

        /// <summary>
        /// THE INTERACTION BACKLOG 241 HAD TO GET RIGHT: a player who presses Enter part way through
        /// the line before a long instrumental must still be able to skip it. Driven end to end on
        /// the same decoder-shaped map as the test above, from a state the test above cannot reach:
        /// the caret is parked past the end of an INCOMPLETE line, with one character typed and one
        /// still owed.
        ///
        /// <para>That state passes through the key handler by exactly the same route a finished line
        /// does, because the skip parks the caret rather than inventing a state of its own, and
        /// nothing narrower covers it: the handler's Space carve-out wants an UNTOUCHED active line,
        /// which a player who typed a character and then gave up is not, and the song's own window
        /// never closes here (the decoder's windows are contiguous). Both of those are asserted
        /// below, so the test cannot quietly start passing through some other door.</para>
        /// </summary>
        [Test]
        public void TestSpaceStillSkipsAfterALineSkip()
        {
            AddUntilStep("gameplay started", () => Player.GameplayClockContainer.CurrentTime > 0);
            AddUntilStep("line 0 active", () => playfield.Engine.ActiveLineIndex == 0);

            AddStep("type 'a'", () => InputManager.Key(Key.A));
            AddAssert("one character in, one still owed", () =>
                playfield.Engine.Lines[0].Cells[0].State == CellState.Correct
                && playfield.Engine.Lines[0].Cells[1].State == CellState.Untyped);

            AddStep("give the line up with Enter", () => InputManager.Key(Key.Enter));

            AddAssert("the caret parked past the end of an unfinished line", () =>
                playfield.Engine.ActiveLineIndex == 0
                && playfield.Engine.IsLineComplete
                && playfield.Engine.NextUnsealedLineIndex == 0
                && playfield.Engine.Lines[0].Cells[1].State == CellState.Untyped);

            // Neither of the narrower conditions the handler tests for holds in this state, so the
            // skip below cannot be passing for either of those reasons.
            AddAssert("the narrow Space carve-out cannot fire here", () => !playfield.Engine.ActiveLineUntouched);
            AddAssert("and the song's own window never closes", () => playfield.Engine.SongWindowOpen);

            AddUntilStep("overlay skip period open", () => instrumentalOverlay.InSkipPeriod);
            AddUntilStep("overlay button shown", () => instrumentalOverlay.IsButtonVisible);

            AddStep("press Space in the gap", () => InputManager.Key(Key.Space));
            AddUntilStep("clock seeked past the gap", () => Player.GameplayClockContainer.CurrentTime >= 10900);
            AddAssert("exactly one skip recorded", () => instrumentalOverlay.SkipCount == 1);

            // And the run carries on: the deferred roll hands the parked caret to line 1 at 12500,
            // and the line they gave up seals with its one missed cell, at its own deadline.
            AddUntilStep("line 1 becomes active", () => playfield.Engine.ActiveLineIndex == 1);
            AddUntilStep("the abandoned cell is missed at the seal", () =>
                playfield.Engine.Lines[0].Cells[1].State == CellState.Missed);
            AddAssert("gameplay still live", () => !Player.GameplayState.HasFailed);
        }
    }
}
