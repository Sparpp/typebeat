// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Graphics.Colour;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Tests.Visual;
using osuTK.Graphics;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// The rendered half of the syllable-timing experiment (backlog 174 stage 3). The colour maths
    /// is pinned by <c>SyllableRenderColourTest</c>; what is pinned HERE is the time-driven wiring:
    /// the stage finds the group whose sung span contains the current time (exactly where the old
    /// playhead would be), feeds it to the display's <c>SetSungSyllable</c> seam, and the group's
    /// untyped cells light white, moving group by group as the song advances. The playhead itself
    /// is gone: the sung caret never shows and the underline sweep never fills, while the typing
    /// caret, correct green and typo red carry the player's own state.
    ///
    /// <para>A real player scene (not a bare drawable ruleset) because the assertions are WALL-TIME
    /// windows: they need a gameplay clock that starts at zero, like the cue-after-gap scene. The
    /// engine flag is set in the first step, before the clock reaches the line, honouring its
    /// set-before-the-first-keypress era rule; NUnit leaves it off by default, so the scene turns it
    /// on explicitly.</para>
    /// </summary>
    public partial class TestSceneTypeBeatSyllableRender : PlayerTestScene
    {
        protected override Ruleset CreatePlayerRuleset() => new TypeBeatRuleset();

        private TypeBeatPlayfield playfield => (TypeBeatPlayfield)Player.DrawableRuleset.Playfield;

        private LyricStage stage => Player.ChildrenOfType<LyricStage>().Single();

        private LyricLineDisplay display => stage.DisplayAt(0)!;

        private double now => Player.GameplayClockContainer.CurrentTime;

        private Color4 colour(int index) => display.CellColour(index).TopLeft.SRGB;

        private static bool same(Color4 a, Color4 b) => ((ColourInfo)a).Equals((ColourInfo)b);

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var beatmap = new Beatmap
            {
                HitObjects = new List<Rulesets.Objects.HitObject>(),
            };

            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.BeatmapInfo.Metadata.Artist = "Test";
            beatmap.BeatmapInfo.Metadata.Title = "SyllableRender";

            // One line, one subtimed word: "open door", cells o0 p1 e2 n3 _4 d5 o6 o7 r8. The
            // syllabifier splits o|pen, and the even index spread over [3000, 9000] puts the chars
            // at 3000/4500/6000/7500, so the groups (each ending where the next begins) are:
            //   0: "o"    cells [0,1)  sung [3000, 4500]
            //   1: "pen"  cells [1,4)  sung [4500, 9000]
            //   2: "door" cells [5,9)  sung [9000, 40000]
            // The space cell 4 is in no group. Second-scale spans because the clock is real time;
            // every window assertion below is bounded well inside its group.
            var line = new LyricLine
            {
                RawText = "open door",
                StartTime = 500,
                EndTime = 60000,
                SingEndTime = 40000,
                Units = new[]
                {
                    new TimedUnit { Text = "open", StartTime = 3000, EndTime = 9000 },
                    new TimedUnit { Text = "door", StartTime = 9000, EndTime = 40000 },
                },
            };

            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = line.StartTime,
                LineIndex = 0,
                Line = line,
                Granularity = TimingGranularity.Line,
            });

            return beatmap;
        }

        [Test]
        public void TestTheHighlightFollowsTheSongAndThePlayheadIsGone()
        {
            // NUnit leaves the experiment off (DrawableTypeBeatRuleset.createEngine); rendering
            // follows the ENGINE's flag live, so setting it here, before the clock reaches the
            // line, is the era-legal switch-on.
            AddStep("enable syllable timing", () => playfield.Engine.SyllableTiming = true);
            AddUntilStep("gameplay started", () => now > 0);
            AddUntilStep("line 0 active", () => playfield.Engine.ActiveLineIndex == 0);

            // Group 0, just "o": its one untyped cell lights white; nothing else does.
            AddUntilStep("'o' lights white while sung", () => same(colour(0), TypeBeatStyle.TypedChar) && now < 4300);
            AddAssert("its own word's later cells stay grey", () =>
                same(colour(1), TypeBeatStyle.UntypedChar)
                && same(colour(2), TypeBeatStyle.UntypedChar)
                && same(colour(3), TypeBeatStyle.UntypedChar));
            AddAssert("'door' stays grey", () => same(colour(5), TypeBeatStyle.UntypedChar) && same(colour(8), TypeBeatStyle.UntypedChar));

            // The playhead is gone, the typing caret is not.
            AddAssert("sung caret hidden, typing caret shown", () => !stage.SungCaretVisible && stage.PlayerCaretVisible);
            AddAssert("underline sweep never fills", () => display.SweepFillWidth == 0);

            // Group 1: the highlight moves to "pen" and releases the still-untyped "o" back to grey
            // (already sung past reads exactly like not yet sung).
            AddUntilStep("'pen' lights and 'o' releases", () =>
                same(colour(1), TypeBeatStyle.TypedChar)
                && same(colour(0), TypeBeatStyle.UntypedChar)
                && now < 8600);

            // The player's own state paints over the time highlight: correct is the flat green,
            // a typed-through typo the classic red.
            AddStep("type 'o' correctly, then 'x' for 'p'", () =>
            {
                playfield.Engine.ProcessKey('o', 5000);
                playfield.Engine.ProcessKey('x', 5100);
            });
            AddUntilStep("'o' painted the flat green", () => same(colour(0), TypeBeatStyle.SyllableCorrectChar));
            AddAssert("'p' painted the classic error red", () => same(colour(1), TypeBeatStyle.ErrorChar));

            // Group 2: a whole multi-cell group lights at once; the space cell, in no group, never
            // lights.
            AddUntilStep("'door' lights as one group", () =>
                same(colour(5), TypeBeatStyle.TypedChar)
                && same(colour(6), TypeBeatStyle.TypedChar)
                && same(colour(7), TypeBeatStyle.TypedChar)
                && same(colour(8), TypeBeatStyle.TypedChar));
            AddAssert("the space cell stays grey", () => same(colour(4), TypeBeatStyle.UntypedChar));
            AddAssert("still no playhead", () => !stage.SungCaretVisible && display.SweepFillWidth == 0);
        }
    }
}
