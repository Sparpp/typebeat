// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.UI;
using osuTK.Graphics;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The colour half of the lit-syllable rendering (backlog 174 stage 3, rekeyed off the sung
    /// playhead style by 175, made unconditional by 177):
    /// <see cref="LyricLineDisplay.CellFillColour"/> is the single pure function every cell fill
    /// routes through, so pinning it pins the painting.
    ///
    /// <para>There is ONE rule now rather than two presentations to choose between, which is why
    /// this fixture no longer sweeps a mode axis: the pre-174 painting, sync-tint ramp included,
    /// plus the currently sung group's UNTYPED cells lifting to <see cref="TypeBeatStyle.SungChar"/>.
    /// A player who never opens the playhead dropdown therefore sees exactly today's colours with
    /// the lit group added, and the one who sets the playhead to <c>CaretStyle.None</c> sees the
    /// same colours again: that style subtracts the caret and the sweep, neither of which is decided
    /// here.</para>
    ///
    /// <para>Backlog 178 DEMOTED that highlight from the palette white to a lighter grey. The pin
    /// below inverted with it: the highlight used to be asserted EQUAL to the typed white and to an
    /// on-the-beat correct char, and is now asserted DISTINCT from the untyped grey, the typed white
    /// and the sync ramp's floor, in contrast terms rather than by restating a hex. The Correct state
    /// still has no highlight colour of its own (backlog 176a removed the flat green it briefly had):
    /// it rides the ramp wherever it sits, and the whole ramp now sits above the highlight, so typing
    /// a char always promotes it out of the highlight.</para>
    /// </summary>
    [TestFixture]
    public class SyllableRenderColourTest
    {
        private static readonly CellState[] all_states =
            (CellState[])Enum.GetValues(typeof(CellState));

        private static Color4 fill(CellState state, bool inSung, double? quality = null, bool freestyle = false)
            => LyricLineDisplay.CellFillColour(state, freestyle, inSung, quality);

        // Colour measurement (sRGB IEC 61966-2-1 / WCAG 2.x), the same helpers SyncTintTest uses to
        // state design constraints in terms a human can check against a contrast checker.
        private static double toLinear(double channel)
            => channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);

        private static double luminance(Color4 c)
            => 0.2126 * toLinear(c.R) + 0.7152 * toLinear(c.G) + 0.0722 * toLinear(c.B);

        private static double contrast(Color4 a, Color4 b)
        {
            double la = luminance(a);
            double lb = luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        private static void assertBrighterThan(Color4 brighter, Color4 duller, string what)
        {
            Assert.That(luminance(brighter), Is.GreaterThan(luminance(duller)), what);
            Assert.That(brighter.R, Is.GreaterThan(duller.R), $"{what} (R)");
            Assert.That(brighter.G, Is.GreaterThan(duller.G), $"{what} (G)");
            Assert.That(brighter.B, Is.GreaterThan(duller.B), $"{what} (B)");
        }

        // --- the lit group ---

        [Test]
        public void SungSyllablesUntypedCellsWearTheDemotedGrey()
        {
            Assert.That(fill(CellState.Untyped, inSung: true),
                Is.EqualTo(TypeBeatStyle.SungChar), "the sung group's untyped cells lift to the highlight grey");

            // Backlog 178 in one line: it is NOT the white any more.
            Assert.That(fill(CellState.Untyped, inSung: true), Is.Not.EqualTo(TypeBeatStyle.TypedChar));
        }

        [Test]
        public void UntypedCellsOutsideTheSungGroupStayTheUntypedGrey()
        {
            // Not yet sung and already sung past are the same input here: not the current group.
            Assert.That(fill(CellState.Untyped, inSung: false),
                Is.EqualTo(TypeBeatStyle.UntypedChar));
        }

        /// <summary>
        /// The inverted pin (backlog 178). The highlight is a THIRD colour and must be readable as
        /// one against all three things it sits near, stated as contrast ratios so the assertion
        /// survives a retune of any of them. The margins are asymmetric on purpose: the band between
        /// the untyped grey and the ramp floor is only about 1.94:1 wide in total, so nothing inside
        /// it can clear both ends by more than about 1.39:1, and the split is weighted towards the
        /// untyped end because "the song is HERE" is the read the highlight exists for.
        /// </summary>
        [Test]
        public void TheHighlightIsAThirdColourDistinctFromEveryStateItSitsBetween()
        {
            var highlight = fill(CellState.Untyped, inSung: true);
            var floor = LyricLineDisplay.CorrectCharColour(0);

            Assert.That(highlight, Is.Not.EqualTo(TypeBeatStyle.UntypedChar));
            Assert.That(highlight, Is.Not.EqualTo(TypeBeatStyle.TypedChar));
            Assert.That(highlight, Is.Not.EqualTo(floor));

            // Ordered, on every channel as well as on luminance: untyped < highlight < floor < typed.
            assertBrighterThan(highlight, TypeBeatStyle.UntypedChar, "highlight vs the untyped grey");
            assertBrighterThan(floor, highlight, "the sync ramp floor vs the highlight");
            assertBrighterThan(TypeBeatStyle.TypedChar, floor, "the typed white vs the ramp floor");

            // Against the untyped grey the yardstick is the untyped-versus-Missed step the game
            // already ships and asks players to read, about 1.47:1; the highlight matches it.
            Assert.That(contrast(highlight, TypeBeatStyle.UntypedChar), Is.GreaterThan(1.4),
                "a sung cell must be tellable from a not-yet-sung one at a glance");

            // Against the typed white the margin is wide, which is the demotion itself: an untyped
            // char can never be misread as one the player already typed.
            Assert.That(contrast(TypeBeatStyle.TypedChar, highlight), Is.GreaterThan(2.4),
                "the highlight must not read as typed");

            // And it clears the ramp floor, the collision the old white had no room for at all.
            Assert.That(contrast(floor, highlight), Is.GreaterThan(1.25),
                "the worst correct char must stay tellable from a highlighted untyped one");
        }

        [Test]
        public void TypingACharAlwaysPromotesItOutOfTheHighlight()
        {
            // The consequence of putting the whole ramp above the highlight: however badly timed the
            // press was, a Correct cell is brighter than the highlight it replaced. Before 178 the
            // ramp's TOP was the highlight, so a dead-on press was invisible against it.
            foreach (double q in new[] { 0, 0.25, 0.5, 0.75, 1 })
            {
                assertBrighterThan(fill(CellState.Correct, inSung: true, quality: q),
                    fill(CellState.Untyped, inSung: true), $"a correct char at quality {q} vs the highlight");
            }
        }

        [Test]
        public void CorrectRidesTheSyncTintRampWhereverItSits()
        {
            // The ramp is not overridden inside the sung group either: an off-span press still reads
            // as the dimmer colour it earned, which is exactly the signal a flat fill would have
            // thrown away, and classic judgement keeps its whole ramp untouched.
            foreach (double q in new[] { 0, 0.25, 0.5, 0.75, 1 })
            {
                foreach (bool inSung in new[] { true, false })
                {
                    Assert.That(fill(CellState.Correct, inSung, quality: q),
                        Is.EqualTo(LyricLineDisplay.CorrectCharColour(q)), $"quality {q}, inSung={inSung}");
                }
            }

            // The cannot-arise fallback (a Correct cell with no delta): the flat typed colour, not
            // the dull ramp floor.
            Assert.That(fill(CellState.Correct, inSung: false, quality: null), Is.EqualTo(TypeBeatStyle.TypedChar));
            Assert.That(fill(CellState.Correct, inSung: true, quality: null), Is.EqualTo(TypeBeatStyle.TypedChar));
        }

        [Test]
        public void WrongIsTheClassicErrorRedInBothPositions()
        {
            Assert.That(fill(CellState.Wrong, inSung: true), Is.EqualTo(TypeBeatStyle.ErrorChar));
            Assert.That(fill(CellState.Wrong, inSung: false), Is.EqualTo(TypeBeatStyle.ErrorChar));
        }

        [Test]
        public void TheLostAndGivenUpStatesKeepTheGreyEvenInsideTheSungGroup()
        {
            // Their alphas (unchanged) carry the state; the highlight is only for characters that
            // can still be typed on time.
            foreach (var state in new[] { CellState.Missed, CellState.Abandoned, CellState.AutoSkipped })
            {
                Assert.That(fill(state, inSung: true), Is.EqualTo(TypeBeatStyle.UntypedChar), $"{state} inside the sung group");
                Assert.That(fill(state, inSung: false), Is.EqualTo(TypeBeatStyle.UntypedChar), $"{state} outside it");
            }
        }

        [Test]
        public void FreestyleKeepsItsVioletIdentityInEveryState()
        {
            // The violet says "this slot was free", an identity, not a state; neither the sync ramp
            // nor the syllable highlight may repaint it. An exclusion, not an oversight.
            foreach (var state in all_states)
            {
                foreach (bool inSung in new[] { true, false })
                {
                    Assert.That(fill(state, inSung, quality: 0.5, freestyle: true),
                        Is.EqualTo(TypeBeatStyle.FreestyleChar), $"{state}, inSung={inSung}");
                }
            }
        }

        [Test]
        public void TheSungGroupOnlyEverRepaintsUntypedCells()
        {
            // The one input the highlight added is inert in every OTHER state, along the whole ramp.
            // That is what keeps the rest of the rendering byte-identical to the pre-174 painting
            // now that the highlight is on under every playhead style rather than under one.
            foreach (var state in all_states)
            {
                if (state == CellState.Untyped)
                    continue;

                foreach (double? q in new double?[] { null, 0, 0.5, 1 })
                {
                    Assert.That(fill(state, inSung: true, quality: q),
                        Is.EqualTo(fill(state, inSung: false, quality: q)),
                        $"{state}, quality {q?.ToString() ?? "null"}");
                }
            }
        }
    }
}
