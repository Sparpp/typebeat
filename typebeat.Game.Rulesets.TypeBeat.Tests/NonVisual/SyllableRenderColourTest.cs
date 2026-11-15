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
    /// Cell colours separate the current sung group from untyped text. Players can adjust the
    /// sung brightness; that preference affects untyped cells only, while correct cells retain
    /// their sync tint and every other state retains its own colour.
    /// </summary>
    [TestFixture]
    public class SyllableRenderColourTest
    {
        private static readonly CellState[] all_states =
            (CellState[])Enum.GetValues(typeof(CellState));

        private static Color4 fill(CellState state, bool inSung, double? quality = null, bool freestyle = false)
            => LyricLineDisplay.CellFillColour(state, freestyle, inSung, quality);

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

        [TestCase(0f)]
        [TestCase(25f)]
        [TestCase(50f)]
        [TestCase(75f)]
        [TestCase(100f)]
        public void SungBrightnessChangesOnlyTheUntypedHighlight(float brightness)
        {
            foreach (CellState state in all_states)
            {
                foreach (double quality in new[] { 0, 0.25, 0.5, 0.75, 1 })
                {
                    Color4 actual = LyricLineDisplay.CellFillColour(state, false, true, quality, brightness);
                    Color4 expected = state == CellState.Untyped
                        ? TypeBeatStyle.SungCharForBrightness(brightness)
                        : fill(state, inSung: false, quality);
                    Assert.That(actual, Is.EqualTo(expected), $"state {state}, quality {quality}");
                    Assert.That(LyricLineDisplay.CellFillColour(state, false, false, quality, brightness),
                        Is.EqualTo(fill(state, inSung: false, quality)), "outside the sung group the setting has no effect");
                    Assert.That(LyricLineDisplay.CellFillColour(state, true, true, quality, brightness),
                        Is.EqualTo(TypeBeatStyle.FreestyleChar), "freestyle keeps its identity colour");
                }
            }
        }

        [Test]
        public void SungBrightnessClampsAtTheUntypedAndTypedColours()
        {
            Assert.That(TypeBeatStyle.SungCharForBrightness(-10), Is.EqualTo(TypeBeatStyle.UntypedChar));
            Assert.That(TypeBeatStyle.SungCharForBrightness(110), Is.EqualTo(TypeBeatStyle.TypedChar));
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
