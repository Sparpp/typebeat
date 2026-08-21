// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Graphics.Colour;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Tests.Visual;
using osuTK.Graphics;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// The lit-syllable rendering (backlog 174 stage 3) and WHAT IT IS COUPLED TO, which since
    /// backlog 177 is: nothing. The group being sung lights under EVERY sung playhead style, and
    /// <see cref="CaretStyle.None"/> only takes the playhead away. The colour maths is pinned by
    /// <c>SyllableRenderColourTest</c>; what is pinned HERE is the wiring and the independence.
    ///
    /// <para>Each frame the stage finds the group whose sung span contains the current time (exactly
    /// where the playhead sits) and feeds it to the display's <c>SetSungSyllable</c> seam, so the
    /// group's untyped cells light white and move group by group as the song advances. That happens
    /// on the shipped DEFAULT style, with the caret and the underline sweep alive at the same time,
    /// which is the case backlog 175 asserted the opposite of. Under None the same group lights and
    /// the playhead is gone: caret hidden, sweep unfed.</para>
    ///
    /// <para>The engine's <c>TypingEngine.SyllableTiming</c> flag is asserted to move nothing, in
    /// both directions, because the highlight and the flag used to be the same switch: the default
    /// test turns the flag ON and gets the ordinary playhead, and the None test leaves it OFF
    /// (NUnit's default) and still gets the whole lit-group rendering, off
    /// <c>TypingLine.Syllables</c>, which is built for every line regardless of mode.</para>
    ///
    /// <para>A real player scene (not a bare drawable ruleset) because the assertions are WALL-TIME
    /// windows: they need a gameplay clock that starts at zero, like the cue-after-gap scene. The
    /// style is written through the same cached ruleset config the settings dropdown writes, so these
    /// are live mid-play switches; every test sets it explicitly rather than inheriting whatever the
    /// previous one left in the fixture-wide config.</para>
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

        /// <summary>
        /// Whether SOME syllable group is lit and rendering as lit, without pinning which one. The
        /// switching steps around it span seconds of real gameplay time, so the group being sung
        /// moves on; what must never happen is the highlight going dark because the playhead style
        /// changed under it.
        /// </summary>
        private bool aGroupIsLitAndWhite()
        {
            int g = display.SungSyllable;

            if (g < 0)
                return false;

            // Nothing is typed in this test, so every cell of the lit group is Untyped and must be
            // wearing the highlight white.
            var group = display.Line.Syllables[g];

            for (int c = group.StartCell; c < group.EndCellExclusive; c++)
            {
                if (display.Line.SyllableIndexOf(c) == g && !same(colour(c), TypeBeatStyle.TypedChar))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// The sync tint cell <paramref name="index"/> has EARNED, recomputed from the delta the
        /// engine actually stored. Backlog 176a left a Correct cell no highlight colour of its own,
        /// so this ramp is what it must wear, inside the lit group as much as outside it; deriving
        /// the expectation rather than hardcoding a swatch is what keeps the assertion about the
        /// rule instead of about one press's arithmetic.
        /// </summary>
        private Color4 earnedTint(int index)
        {
            var cell = display.Line.Cells[index];
            double delta = cell.JudgedDelta!.Value;

            return LyricLineDisplay.CorrectCharColour(Gameplay.SyncWindows.For(cell.JudgeGranularity).SyncQuality(delta));
        }

        // The same cached-per-ShortName manager the gameplay bindings read, so setting a value here
        // drives the live stage exactly as moving the settings dropdown would.
        private TypeBeatRulesetConfigManager config => (TypeBeatRulesetConfigManager)RulesetConfigs.GetConfigFor(new TypeBeatRuleset())!;

        private void setSungStyle(CaretStyle style)
            => AddStep($"song playhead style = {style}", () => config.SetValue(TypeBeatRulesetSetting.SungCaretStyle, style));

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
        public void TestTheNoPlayheadStyleKeepsTheLitGroupAndDropsThePlayhead()
        {
            // The engine flag is deliberately NOT touched: NUnit leaves the syllable-judgement
            // experiment off (DrawableTypeBeatRuleset.createEngine), so everything below is produced
            // without it, under the classic point-target judgement. That is the decoupling.
            setSungStyle(CaretStyle.None);
            AddUntilStep("gameplay started", () => now > 0);
            AddUntilStep("line 0 active", () => playfield.Engine.ActiveLineIndex == 0);
            AddAssert("syllable judgement stayed off", () => !playfield.Engine.SyllableTiming);

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

            // The player's own state paints over the time highlight: a correct char rides the sync
            // ramp (no highlight colour of its own since backlog 176a), a typed-through typo the
            // classic red. The press below is far outside its cell's point target under classic
            // judgement, so what is pinned is that it is ON the ramp, not white.
            AddStep("type 'o' correctly, then 'x' for 'p'", () =>
            {
                playfield.Engine.ProcessKey('o', 5000);
                playfield.Engine.ProcessKey('x', 5100);
            });
            AddUntilStep("'o' is Correct", () => display.Line.Cells[0].State == Gameplay.CellState.Correct);
            AddAssert("'o' painted at exactly the sync tint it earned", () => same(colour(0), earnedTint(0)));
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

        [Test]
        public void TestTheDefaultStyleKeepsThePlayheadAndLightsTheGroupTooEvenUnderSyllableJudgement()
        {
            // Two regressions in one, and they pull opposite ways. Backlog 174 let the engine flag
            // force the whole rendering, so a dev build lost its playhead whether or not anyone
            // asked: the flag is a judgement rule, so the caret and sweep must be alive here with it
            // ON. Backlog 175 then made the highlight a style, so the shipped default lost the lit
            // group: since 177 the two ride together, and this asserts BOTH at once on the style
            // every player actually gets.
            setSungStyle(TypeBeatRulesetConfigManager.DEFAULT_SUNG_CARET_STYLE);
            AddStep("judge on syllable spans", () => playfield.Engine.SyllableTiming = true);
            AddUntilStep("gameplay started", () => now > 0);
            AddUntilStep("line 0 active", () => playfield.Engine.ActiveLineIndex == 0);

            AddAssert("the default style is Line", () => stage.SungCaretStyle == CaretStyle.Line);
            AddUntilStep("sung caret is shown", () => stage.SungCaretVisible);
            AddUntilStep("the sweep is being fed", () => display.SweepFillWidth > 0);
            AddAssert("typing caret shown too", () => stage.PlayerCaretVisible);

            // Deep inside group 0's span: the group lights, alongside the playhead rather than
            // instead of it, and cells outside the group stay grey.
            AddUntilStep("well inside the first sung group", () => now > 3300 && now < 4300);
            AddAssert("group 0 is lit", () => display.SungSyllable == 0);
            AddAssert("'o' lights white while the playhead is still drawn", () =>
                same(colour(0), TypeBeatStyle.TypedChar) && stage.SungCaretVisible && display.SweepFillWidth > 0);
            AddAssert("cells outside the group stay the untyped grey", () =>
                same(colour(1), TypeBeatStyle.UntypedChar)
                && same(colour(5), TypeBeatStyle.UntypedChar));
        }

        [Test]
        public void TestFlippingTheStyleMidPlayTogglesOnlyThePlayhead()
        {
            setSungStyle(CaretStyle.Line);
            AddUntilStep("gameplay started", () => now > 0);
            AddUntilStep("line 0 active", () => playfield.Engine.ActiveLineIndex == 0);

            // Start from a LIVE playhead, so the sweep is holding a nonzero fill when the switch
            // happens. That is the ordering backlog 174 never had to handle (it relied on the sweep
            // never having been fed at all), and a stale fill would otherwise freeze on screen.
            AddUntilStep("playhead is live", () => stage.SungCaretVisible && display.SweepFillWidth > 0);
            AddUntilStep("group 0 is already lit under Line", () =>
                display.SungSyllable == 0 && same(colour(0), TypeBeatStyle.TypedChar) && now < 4300);

            setSungStyle(CaretStyle.None);
            AddAssert("the stale sweep was zeroed at once", () => display.SweepFillWidth == 0);
            AddUntilStep("sung caret hides", () => !stage.SungCaretVisible);
            AddAssert("typing caret is untouched", () => stage.PlayerCaretVisible);
            AddAssert("a group is still lit across the switch", aGroupIsLitAndWhite);

            setSungStyle(CaretStyle.Line);
            AddUntilStep("sung caret returns", () => stage.SungCaretVisible);
            AddUntilStep("the sweep is fed again", () => display.SweepFillWidth > 0);

            // The regression guarded here: leaving None used to clear the lit group, because leaving
            // the style meant leaving the highlight. It no longer does, so nothing may go dark.
            // Deliberately group-AGNOSTIC: several seconds of real time pass across these steps, so
            // which group is being sung by now is not the contract; that one still is, is.
            AddAssert("a group is still lit on the way back", aGroupIsLitAndWhite);
        }
    }
}
