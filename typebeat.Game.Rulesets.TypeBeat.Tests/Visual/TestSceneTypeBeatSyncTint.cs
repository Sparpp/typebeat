// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Graphics.Colour;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Tests.Visual;
using osuTK.Graphics;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// The rendered half of the SYNC TINT (backlog 87): a correctly typed char is painted on a ramp
    /// between the untyped grey and the full typed off-white according to how in sync the keypress
    /// that scored it was, so the trail behind the caret reads as brightness. The maths itself is
    /// pinned by <c>SyncTintTest</c>, which needs no font; what is pinned HERE is the wiring, that
    /// the display actually reads the engine's judged delta at the cell's own window tier and repaints
    /// with it, plus the two states that deliberately take no ramp.
    ///
    /// <para>This scene's line is a single syllable group sung from the first frame, so its UNTYPED
    /// cells wear the sung-group highlight (backlog 177) rather than the plain untyped grey. Backlog
    /// 178 demoted that highlight from the palette white to <see cref="TypeBeatStyle.SungChar"/>, a
    /// grey BELOW the ramp's floor, so this fixture re-baselines to it: "untyped" here means
    /// <c>SungChar</c>, and every point of the ramp, floor included, now differs from it. The presses
    /// below stay deliberately off the beat anyway, because that is what makes the ramp's SHAPE
    /// visible rather than just its endpoints.</para>
    ///
    /// <para>Keys are fed straight to the engine at computed times rather than through the input
    /// manager, because the whole point is a controlled delta and the real input path can only press
    /// "now". The event chain under test (ProcessKey writes the delta, raises CharJudged, the stage
    /// repaints the cell) is entirely unchanged by that.</para>
    /// </summary>
    public partial class TestSceneTypeBeatSyncTint : OsuTestScene
    {
        private const string text = "ab&cd"; // cell 2 is the freestyle slot
        private const int slot = 2;

        private DrawableTypeBeatRuleset drawableRuleset = null!;

        protected override Ruleset CreateRuleset() => new TypeBeatRuleset();

        private TypingEngine engine => ((TypeBeatPlayfield)drawableRuleset.Playfield).Engine;
        private LyricStage stage => drawableRuleset.ChildrenOfType<LyricStage>().Single();
        private LyricLineDisplay display => stage.DisplayAt(0)!;

        private TypingCell cell(int index) => engine.Lines[0].Cells[index];

        private Color4 colour(int index) => display.CellColour(index).TopLeft.SRGB;

        private static double brightness(Color4 c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;

        private static bool same(Color4 a, Color4 b) => ((ColourInfo)a).Equals((ColourInfo)b);

        /// <summary>Feed one key into cell <paramref name="index"/> at a chosen offset from its target
        /// time, so the delta the engine judges is exactly <paramref name="delta"/>.</summary>
        private void press(int index, char c, double delta) => engine.ProcessKey(c, cell(index).TargetTime + delta);

        /// <summary>A late offset worth exactly half sync quality at whatever tier the cell is judged
        /// at: q = 1 - OkLate/MehLate, and the two scale together, so this is 0.5 on every tier.</summary>
        private double halfQualityLateDelta(int index) => SyncWindows.For(cell(index).JudgeGranularity).OkLate;

        /// <summary>Far enough past the Meh window that sync quality is pinned at 0: the case that
        /// would render as the untyped grey if the ramp had no floor.</summary>
        private double hopelesslyLateDelta(int index) => SyncWindows.For(cell(index).JudgeGranularity).MehLate * 3;

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("create drawable ruleset", () =>
            {
                var ruleset = new TypeBeatRuleset();

                // Vocals stretched over five minutes: cell targets land a minute apart, so a press at
                // a chosen offset from one of them can never collide with the next.
                var line = new LyricLine
                {
                    RawText = text,
                    StartTime = 0,
                    EndTime = 600000,
                    SingEndTime = 300000,
                    Units = new[] { new TimedUnit { Text = text, StartTime = 0, EndTime = 300000 } },
                };

                var beatmap = new Beatmap
                {
                    HitObjects = new List<Rulesets.Objects.HitObject>
                    {
                        new TypeBeatHitObject
                        {
                            StartTime = line.StartTime,
                            LineIndex = 0,
                            Line = line,
                            Granularity = TimingGranularity.Word,
                        },
                    },
                };
                beatmap.BeatmapInfo.Ruleset = ruleset.RulesetInfo;

                var playable = CreateWorkingBeatmap(beatmap).GetPlayableBeatmap(ruleset.RulesetInfo, Array.Empty<Mod>());

                Child = drawableRuleset = (DrawableTypeBeatRuleset)ruleset.CreateDrawableRulesetWith(playable);
            });

            AddUntilStep("first line active", () => engine.ActiveLineIndex == 0);
            // This line is ONE syllable group covering every cell, and the scene's clock starts on
            // the first cell's target, so that group is being sung for every step in this fixture.
            // Since backlog 177 the sung group lights its UNTYPED cells whatever the playhead style
            // is, so the highlight grey, not the plain untyped grey, is what "untyped" looks like
            // here. That costs this fixture nothing: its subject is what a CORRECT cell reads as, and
            // a Correct cell rides the sync-tint ramp everywhere, a rule the highlight does not touch
            // (see LyricLineDisplay.CellFillColour).
            AddUntilStep("the line's one group is being sung", () => display.SungSyllable == 0);
            AddAssert("so every cell starts on the highlight grey", () => same(colour(0), TypeBeatStyle.SungChar));
            // Backlog 178 put the whole ramp above that grey, which is what lets the steps below
            // tell a typed cell from an untyped one at ANY quality, the floor included.
            AddAssert("and the ramp floor clears it", () => !same(LyricLineDisplay.CorrectCharColour(0), TypeBeatStyle.SungChar)
                                                           && brightness(LyricLineDisplay.CorrectCharColour(0)) > brightness(TypeBeatStyle.SungChar));
        }

        [Test]
        public void TestTheTrailIsBrightWhereThePlayerWasInSync()
        {
            AddStep("type 'a' dead on", () => press(0, 'a', 0));
            AddStep("type 'b' half a window late", () => press(1, 'b', halfQualityLateDelta(1)));

            // Both chars leave the highlight grey when they repaint, since the whole ramp is above
            // it; waiting on the late one is enough, because it is the later of the two writes.
            AddUntilStep("both chars repainted", () => !same(colour(1), TypeBeatStyle.SungChar)
                                                      && cell(0).JudgedDelta != null);

            AddAssert("both landed correct", () => cell(0).State == CellState.Correct && cell(1).State == CellState.Correct);

            // The headline: two chars in the same state, in the same word, painted differently
            // because one press was in sync and the other was not.
            AddAssert("the dead-on char is the full typed off-white", () => same(colour(0), TypeBeatStyle.TypedChar));
            AddAssert("the late char is duller", () => brightness(colour(1)) < brightness(colour(0)));
            AddAssert("but still brighter than the grey end of the ramp", () => brightness(colour(1)) > brightness(TypeBeatStyle.UntypedChar));

            // Independently computed: the display must have picked the delta up at the CELL's own
            // window tier, which is the only way this lands on exactly half quality.
            AddAssert("the late char sits at exactly half the ramp", () => same(colour(1), LyricLineDisplay.CorrectCharColour(0.5)));

            // Unchanged, which in this scene means still wearing the sung group's highlight grey
            // rather than the plain untyped one: the point is that the ramp reached neither of the
            // two chars it did not score.
            AddAssert("an untyped char ahead of the caret is unchanged", () => same(colour(4), TypeBeatStyle.SungChar));
            AddAssert("and it did not pick up the late char's tint", () => !same(colour(4), colour(1)));
        }

        [Test]
        public void TestTheWorstCorrectCharStillReadsAsTyped()
        {
            AddStep("type 'a' hopelessly late", () => press(0, 'a', hopelesslyLateDelta(0)));

            AddUntilStep("char repainted", () => cell(0).State == CellState.Correct && !same(colour(0), TypeBeatStyle.SungChar));

            AddAssert("it judged Lagging, not a miss", () =>
                SyncWindows.For(cell(0).JudgeGranularity).Classify(cell(0).JudgedDelta!.Value) == JudgementType.Lagging);

            // The floor is the whole reason this test exists: quality is pinned at 0 here, so an
            // unfloored ramp would paint a char the player DID type in precisely the untyped grey.
            AddAssert("it sits on the ramp floor", () => same(colour(0), LyricLineDisplay.CorrectCharColour(0)));
            AddAssert("which is not the untyped grey", () => brightness(colour(0)) > brightness(TypeBeatStyle.UntypedChar));
            // Nor an UNTYPED cell of the sung group, which here wears the highlight grey: backlog 178
            // put that grey below the floor precisely so the worst correct char still reads as typed.
            AddAssert("nor the sung group's highlight grey", () => brightness(colour(0)) > brightness(TypeBeatStyle.SungChar));
            AddAssert("and is not the full typed colour either", () => !same(colour(0), TypeBeatStyle.TypedChar));
        }

        [Test]
        public void TestAFreestyleSlotKeepsItsVioletAndTakesNoRamp()
        {
            AddStep("type up to the freestyle slot", () =>
            {
                press(0, 'a', 0);
                press(1, 'b', 0);
            });
            AddAssert("caret reached the slot", () => engine.CaretIndex == slot);

            AddStep("fill the slot hopelessly late", () => press(slot, 'x', hopelesslyLateDelta(slot)));

            AddUntilStep("slot filled", () => cell(slot).State == CellState.Correct && display.CellText(slot) == "x");

            // Deliberate exclusion: the violet says "this slot was free", an identity, not a state,
            // and lerping it towards grey would fight the thing it exists to say.
            AddAssert("slot still wears the freestyle violet", () => same(colour(slot), TypeBeatStyle.FreestyleChar));

            // ...and the exclusion is specific, not "nothing is tinted": the very next ORDINARY cell,
            // typed just as badly, does drop to the floor.
            AddStep("type the next ordinary cell just as late", () => press(3, 'c', hopelesslyLateDelta(3)));
            AddUntilStep("it repainted", () => cell(3).State == CellState.Correct && !same(colour(3), TypeBeatStyle.SungChar));
            AddAssert("the ordinary cell took the ramp floor", () => same(colour(3), LyricLineDisplay.CorrectCharColour(0)));
            AddAssert("the slot did not follow it", () => same(colour(slot), TypeBeatStyle.FreestyleChar));
        }

        [Test]
        public void TestBackspaceReturnsTheCharToItsUntypedColour()
        {
            // Half a window late, not dead on: a mid-ramp colour is distinct from both ends, so the
            // paint and the revert are each visible without leaning on either endpoint.
            AddStep("type 'a' half a window late", () => press(0, 'a', halfQualityLateDelta(0)));
            AddUntilStep("painted at half the ramp", () => same(colour(0), LyricLineDisplay.CorrectCharColour(0.5)));

            AddStep("backspace", () => engine.ProcessBackspace());
            AddAssert("cell state and delta both cleared", () => cell(0).State == CellState.Untyped && cell(0).JudgedDelta == null);

            // The ramp is gone and the cell is an ordinary untyped cell again, which in this scene
            // is one the song is on, so it goes back to the highlight grey it started at.
            AddUntilStep("char is back to its untyped colour", () => same(colour(0), TypeBeatStyle.SungChar));
        }
    }
}
