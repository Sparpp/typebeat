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
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Tests.Visual;
using osuTK.Graphics;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Backlog 185: a typo that lands on a WORD GAP is DIMMED, so the gap keeps reading as a word
    /// boundary through a burst of them. Display only. Nothing about the keystroke, the judgement,
    /// the caret or the replay moves, which is why every assertion below is about an alpha and a
    /// colour and none is about a score.
    ///
    /// <para>The problem this pins the fix for: since backlog 181 a wrong letter on the gap is typed
    /// THROUGH and drawn as the typed character in error red (see
    /// <see cref="LyricLineDisplay.CellGlyph"/>), because a red space is nothing at all. At full
    /// brightness that glyph fills the boundary, and after eight or nine consecutive typos
    /// playtesters could no longer see where words started and ended: every gap in the line had a
    /// letter sitting in it, reading exactly like an ordinary character.</para>
    ///
    /// <para>The fix rides the per-cell STATE ALPHA lane, not the fill colour, so the error still
    /// wears <see cref="TypeBeatStyle.ErrorChar"/> and reads as an error, and the dimming composes
    /// multiplicatively with the flashlight window instead of fighting it. The LADDER is the point,
    /// and the last test below draws all of it on screen at once: a full-brightness Wrong LYRIC cell
    /// (which still shows the character the line is made of), the gap typo below it, and a Missed
    /// cell below that.</para>
    ///
    /// <para>Keys are fed straight to the engine at chosen times rather than through the input
    /// manager, exactly as <c>TestSceneTypeBeatSyncTint</c> does and for the same reason: the real
    /// input path can only press "now". The chain under test (ProcessKey raises CharJudged, the
    /// stage repaints the cell, RefreshCell writes the state alpha) is entirely unchanged by
    /// that.</para>
    /// </summary>
    public partial class TestSceneTypeBeatGapTypoDim : OsuTestScene
    {
        private const string text = "ab cd"; // cells: a0 b1 _2 c3 d4
        private const int gap = 2;

        /// <summary>Past the line's deadline, so the seal loop runs and un-typed cells go Missed.</summary>
        private const double line_end = 600000;

        private DrawableTypeBeatRuleset drawableRuleset = null!;

        protected override Ruleset CreateRuleset() => new TypeBeatRuleset();

        private TypingEngine engine => ((TypeBeatPlayfield)drawableRuleset.Playfield).Engine;
        private LyricStage stage => drawableRuleset.ChildrenOfType<LyricStage>().Single();
        private LyricLineDisplay display => stage.DisplayAt(0)!;

        private TypingCell cell(int index) => engine.Lines[0].Cells[index];

        private Color4 colour(int index) => display.CellColour(index).TopLeft.SRGB;

        private static bool same(Color4 a, Color4 b) => ((ColourInfo)a).Equals((ColourInfo)b);

        /// <summary>Type one character into cell <paramref name="index"/>, on that cell's own target.</summary>
        private void press(int index, char c) => engine.ProcessKey(c, cell(index).TargetTime);

        // The same cached-per-ShortName manager the playfield resolves, shared across the whole
        // fixture, so the setting the input model reads is written explicitly rather than inherited
        // from whatever ran before.
        private TypeBeatRulesetConfigManager config => (TypeBeatRulesetConfigManager)RulesetConfigs.GetConfigFor(new TypeBeatRuleset())!;

        [SetUpSteps]
        public void SetUpSteps()
        {
            // Word skipping OFF, which is the shipped default and the arm where a gap typo carries
            // the caret on into the next word. The dimming is deliberately blind to this setting
            // (the other arm PARKS the caret on the same Wrong gap cell, so the glyph sits under the
            // caret instead of behind it), and pinning the default arm pins the harder-to-see one:
            // a typo the player has already typed past.
            AddStep("word skipping off", () => config.SetValue(TypeBeatRulesetSetting.SpaceSkipsWord, false));

            AddStep("create drawable ruleset", () =>
            {
                var ruleset = new TypeBeatRuleset();

                // Vocals stretched over minutes, so cell targets land far apart and the line never
                // runs out of time under its own clock: the one test that needs a seal asks for it.
                var line = new LyricLine
                {
                    RawText = text,
                    StartTime = 0,
                    EndTime = line_end,
                    SingEndTime = 300000,
                    Units = new[]
                    {
                        new TimedUnit { Text = "ab", StartTime = 0, EndTime = 150000 },
                        new TimedUnit { Text = "cd", StartTime = 150000, EndTime = 300000 },
                    },
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

            // The fixture's own shape and the input model it is read under, asserted rather than
            // trusted: without the gap era flag the press below would be rejected and never reach a
            // cell at all, so this test would pass vacuously on an unchanged display.
            AddAssert("the gap sits at cell 2, under the live input model", () =>
                cell(gap).Expected == ' '
                && display.CellCount == text.Length
                && engine.WrongInputOnWordGaps
                && engine.StrictSpaces
                && !engine.SpaceSkipsWord);

            AddAssert("and every cell starts at full brightness", () =>
                Enumerable.Range(0, display.CellCount).All(i => display.CellAlpha(i) == 1f));
        }

        /// <summary>Type "ab" cleanly, then spoil the gap with the next word's first letter.</summary>
        private void typeUpToASpoiledGap()
        {
            AddStep("type 'ab', then 'c' on the gap", () =>
            {
                press(0, 'a');
                press(1, 'b');
                press(gap, 'c');
            });

            AddUntilStep("the gap took the typo and shows it", () =>
                cell(gap).State == CellState.Wrong && display.CellText(gap) == "c");
        }

        /// <summary>
        /// The headline. The typo is on screen, in the error red, showing the character that went
        /// into it, and DIMMED: the boundary is still legible as a boundary.
        /// </summary>
        [Test]
        public void TestAGapTypoIsDimmed()
        {
            typeUpToASpoiledGap();

            AddAssert("the gap typo is dimmed to the gap alpha", () => display.CellAlpha(gap), () => Is.EqualTo(LyricLineDisplay.WRONG_GAP_ALPHA));

            // The colour lane is untouched, which is what keeps the error reading as an error: this
            // is the same red a lyric typo wears, at less than full opacity.
            AddAssert("wearing the same error red a lyric typo does", () => same(colour(gap), TypeBeatStyle.ErrorChar));

            // Scoped to the one cell: the correctly typed characters either side of the gap keep
            // every bit of their brightness, so the dimming reads as a property of the typo and not
            // of the neighbourhood it happened in.
            AddAssert("the chars either side are untouched", () =>
                display.CellAlpha(0) == 1f && display.CellAlpha(1) == 1f
                && display.CellAlpha(gap + 1) == 1f && display.CellAlpha(gap + 2) == 1f);
        }

        /// <summary>
        /// Backspacing the typo takes the dimming away with it. Nothing restores the alpha
        /// explicitly: the cell goes back to <see cref="CellState.Untyped"/> and the default arm of
        /// the state-alpha switch is full brightness, which is exactly why the dimming lives on that
        /// lane rather than being written into the cell by hand.
        /// </summary>
        [Test]
        public void TestBackspacingAGapTypoRestoresFullBrightness()
        {
            typeUpToASpoiledGap();
            AddAssert("dimmed to start with", () => display.CellAlpha(gap), () => Is.EqualTo(LyricLineDisplay.WRONG_GAP_ALPHA));

            AddStep("backspace", () => engine.ProcessBackspace());
            AddAssert("the gap is open again", () => cell(gap).State == CellState.Untyped && cell(gap).TypedChar == null);

            AddUntilStep("and back to full brightness, showing a space", () =>
                display.CellAlpha(gap) == 1f && display.CellText(gap) == " ");
        }

        /// <summary>
        /// The exclusion, and it is the whole reason the predicate is
        /// <c>Expected == ' ' &amp;&amp; State == Wrong</c> and nothing looser. A Wrong LYRIC cell
        /// shows the character the LINE is made of (reddened, so a mistyped line still reads as the
        /// line it was meant to be), and dimming that would dim the lyric itself.
        ///
        /// <para>Both ways of producing one are pinned, because the second is the trap: a SPACE
        /// typed inside a word is, under <see cref="TypingEngine.StrictSpaces"/> with word skipping
        /// off, an ordinary typo on a lyric cell (backlog 184). It has a space involved and it is
        /// still not a gap typo, so it keeps full brightness.</para>
        /// </summary>
        [Test]
        public void TestAWrongLyricCellIsNotDimmed()
        {
            AddStep("type 'z' where 'a' was expected", () => press(0, 'z'));
            AddUntilStep("cell 0 took the typo", () => cell(0).State == CellState.Wrong);

            AddAssert("it keeps full brightness", () => display.CellAlpha(0), () => Is.EqualTo(1f));
            AddAssert("still showing its own lyric character, reddened", () =>
                display.CellText(0) == "a" && same(colour(0), TypeBeatStyle.ErrorChar));

            AddStep("backspace, then press SPACE inside the word", () =>
            {
                engine.ProcessBackspace();
                press(0, ' ');
            });

            AddUntilStep("the space landed on the lyric cell as a typo", () =>
                cell(0).State == CellState.Wrong && cell(0).TypedChar == ' ');

            AddAssert("a mid-word space typo is not dimmed either", () => display.CellAlpha(0), () => Is.EqualTo(1f));
            AddAssert("and it too still shows the lyric character", () => display.CellText(0) == "a");
        }

        /// <summary>
        /// The whole ladder, on screen at once, which is the only place the new constant's VALUE
        /// means anything: a gap typo is dimmer than a full-brightness wrong cell (so the boundary
        /// reads through a burst) and brighter than a Missed one (because it is not a lost
        /// character, it is a live claim on the gap that one backspace takes back, the standing
        /// <see cref="LyricLineDisplay.ABANDONED_ALPHA"/> reasoning).
        ///
        /// <para>The seal is asked for directly, at a time past the line's deadline, and the alphas
        /// are read inside that same step: <c>LineSealed</c> repaints the line synchronously, so
        /// what is captured is what the cells were actually drawn at.</para>
        /// </summary>
        [Test]
        public void TestTheGapTypoSitsBetweenAFullWrongAndAMissedCell()
        {
            typeUpToASpoiledGap();

            float gapAlpha = -1, missedAlpha = -1, correctAlpha = -1;
            CellState missedState = CellState.Untyped;

            AddStep("run the line out of time", () =>
            {
                // Past the deadline AND past the drag grace the unpinned caret gets (backlog 208
                // made that the default): the player is still ON this line, so the seal is deferred
                // by FLETCHER_DRAG_GRACE_MS before the untyped cells become misses.
                engine.Update(line_end + 1 + TypingEngine.FLETCHER_DRAG_GRACE_MS);

                gapAlpha = display.CellAlpha(gap);
                missedAlpha = display.CellAlpha(gap + 1);
                correctAlpha = display.CellAlpha(0);
                missedState = cell(gap + 1).State;
            });

            AddAssert("the un-typed cell after the gap really did miss", () => missedState, () => Is.EqualTo(CellState.Missed));

            AddAssert("a correct char is full brightness", () => correctAlpha, () => Is.EqualTo(1f));
            AddAssert("a missed char is the missed alpha", () => missedAlpha, () => Is.EqualTo(LyricLineDisplay.MISSED_ALPHA));
            AddAssert("the gap typo is the gap alpha", () => gapAlpha, () => Is.EqualTo(LyricLineDisplay.WRONG_GAP_ALPHA));

            AddAssert("which sits strictly between the two", () => missedAlpha < gapAlpha && gapAlpha < correctAlpha);
        }
    }
}
