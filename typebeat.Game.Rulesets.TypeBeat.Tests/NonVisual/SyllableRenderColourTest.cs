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
    /// plus the currently sung group's UNTYPED cells lighting the palette white. A player who never
    /// opens the playhead dropdown therefore sees exactly today's colours with the lit group added,
    /// and the one who sets the playhead to <c>CaretStyle.None</c> sees the same colours again: that
    /// style subtracts the caret and the sweep, neither of which is decided here.</para>
    ///
    /// <para>The Correct state deliberately has NO highlight colour of its own (backlog 176a removed
    /// the flat green it briefly had). It rides the sync-tint ramp wherever it sits, and the ramp at
    /// quality 1 is the palette white exactly, so a char typed on the beat inside the sung span
    /// already matches the highlight. That equality is pinned below rather than left as a reading of
    /// the ramp maths.</para>
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
        public void SungSyllablesUntypedCellsAreThePaletteWhite()
        {
            Assert.That(fill(CellState.Untyped, inSung: true),
                Is.EqualTo(TypeBeatStyle.TypedChar), "the sung group's untyped cells light white");
        }

        [Test]
        public void UntypedCellsOutsideTheSungGroupStayTheUntypedGrey()
        {
            // Not yet sung and already sung past are the same input here: not the current group.
            Assert.That(fill(CellState.Untyped, inSung: false),
                Is.EqualTo(TypeBeatStyle.UntypedChar));
        }

        [Test]
        public void TheHighlightAndAnOnTheBeatCorrectCharAreTheSameWhite()
        {
            // Backlog 176a, made explicit: "it is OK for the highlight and correct colours to both
            // be white". There is no separate correct colour to keep in step with the highlight,
            // because the ramp's top IS the highlight's white, and under syllable judgement every
            // press inside the sung span is delta 0 and therefore quality 1. So the two readings of
            // "white" agree by construction rather than by a constant copied between them.
            Assert.That(LyricLineDisplay.CorrectCharColour(1), Is.EqualTo(TypeBeatStyle.TypedChar),
                "the top of the sync ramp is the palette white exactly");
            Assert.That(fill(CellState.Correct, inSung: true, quality: 1),
                Is.EqualTo(fill(CellState.Untyped, inSung: true)),
                "a correct on-the-beat char inside the sung group matches the group's own white");
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
