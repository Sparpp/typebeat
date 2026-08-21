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
    /// The colour half of the syllable-timing experiment's rendering (backlog 174 stage 3):
    /// <see cref="LyricLineDisplay.CellFillColour"/> is the single pure function every cell fill
    /// routes through, in BOTH timing modes, so pinning it pins the painting.
    ///
    /// <para>Two contracts live here. In syllable mode: an Untyped cell of the group currently
    /// being sung is the palette white, Untyped anywhere else the untyped grey, Correct the flat
    /// green (no sync ramp: in-span presses are all delta 0, a quality ramp would be meaningless),
    /// Wrong the classic red, and freestyle keeps its violet identity. Flag OFF: the function is
    /// byte-identical to the pre-174 painting, sync-tint ramp included, and the sung-syllable input
    /// has no effect whatsoever, which is what guarantees the experiment cannot leak into a Release
    /// build's rendering.</para>
    /// </summary>
    [TestFixture]
    public class SyllableRenderColourTest
    {
        private static readonly CellState[] all_states =
            (CellState[])Enum.GetValues(typeof(CellState));

        private static Color4 fill(CellState state, bool syllableMode, bool inSung, double? quality = null, bool freestyle = false)
            => LyricLineDisplay.CellFillColour(state, freestyle, syllableMode, inSung, quality);

        // --- Syllable mode ---

        [Test]
        public void SungSyllablesUntypedCellsAreThePaletteWhite()
        {
            Assert.That(fill(CellState.Untyped, syllableMode: true, inSung: true),
                Is.EqualTo(TypeBeatStyle.TypedChar), "the sung group's untyped cells light white");
        }

        [Test]
        public void UntypedCellsOutsideTheSungGroupStayTheUntypedGrey()
        {
            // Not yet sung and already sung past are the same input here: not the current group.
            Assert.That(fill(CellState.Untyped, syllableMode: true, inSung: false),
                Is.EqualTo(TypeBeatStyle.UntypedChar));
        }

        [Test]
        public void CorrectIsTheFlatGreenRegardlessOfSyllableOrQuality()
        {
            var green = TypeBeatStyle.SyllableCorrectChar;

            // Flat: no sync-tint ramp in syllable mode. In-span presses are all delta 0, so a
            // quality ramp would collapse to one point; out-of-span deltas exist but grading them
            // by brightness would contradict "the whole span is perfect".
            foreach (bool inSung in new[] { true, false })
            {
                Assert.That(fill(CellState.Correct, true, inSung, quality: null), Is.EqualTo(green));
                Assert.That(fill(CellState.Correct, true, inSung, quality: 0), Is.EqualTo(green));
                Assert.That(fill(CellState.Correct, true, inSung, quality: 0.5), Is.EqualTo(green));
                Assert.That(fill(CellState.Correct, true, inSung, quality: 1), Is.EqualTo(green));
            }
        }

        [Test]
        public void WrongIsTheClassicErrorRedInBothPositions()
        {
            Assert.That(fill(CellState.Wrong, true, inSung: true), Is.EqualTo(TypeBeatStyle.ErrorChar));
            Assert.That(fill(CellState.Wrong, true, inSung: false), Is.EqualTo(TypeBeatStyle.ErrorChar));
        }

        [Test]
        public void TheLostAndGivenUpStatesKeepTheGreyEvenInsideTheSungGroup()
        {
            // Their alphas (unchanged in syllable mode) carry the state; the sung highlight is only
            // for characters that can still be typed on time.
            foreach (var state in new[] { CellState.Missed, CellState.Abandoned, CellState.AutoSkipped })
            {
                Assert.That(fill(state, true, inSung: true), Is.EqualTo(TypeBeatStyle.UntypedChar), $"{state} inside the sung group");
                Assert.That(fill(state, true, inSung: false), Is.EqualTo(TypeBeatStyle.UntypedChar), $"{state} outside it");
            }
        }

        [Test]
        public void FreestyleKeepsItsVioletIdentityInEveryStateAndBothModes()
        {
            // The violet says "this slot was free", an identity, not a state; neither the sync
            // ramp nor the syllable highlight may repaint it. An exclusion, not an oversight.
            foreach (var state in all_states)
            {
                foreach (bool mode in new[] { true, false })
                {
                    foreach (bool inSung in new[] { true, false })
                    {
                        Assert.That(fill(state, mode, inSung, quality: 0.5, freestyle: true),
                            Is.EqualTo(TypeBeatStyle.FreestyleChar), $"{state}, syllableMode={mode}, inSung={inSung}");
                    }
                }
            }
        }

        // --- Flag OFF: byte-identical to the pre-174 painting ---

        [Test]
        public void FlagOffCorrectRidesTheSyncTintRampExactly()
        {
            foreach (double q in new[] { 0, 0.25, 0.5, 0.75, 1 })
            {
                Assert.That(fill(CellState.Correct, syllableMode: false, inSung: false, quality: q),
                    Is.EqualTo(LyricLineDisplay.CorrectCharColour(q)), $"quality {q}");
            }

            // The cannot-arise fallback (a Correct cell with no delta): the flat typed colour, not
            // the dull ramp floor.
            Assert.That(fill(CellState.Correct, syllableMode: false, inSung: false, quality: null),
                Is.EqualTo(TypeBeatStyle.TypedChar));
        }

        [Test]
        public void FlagOffPaintsEveryOtherStateExactlyAsToday()
        {
            Assert.That(fill(CellState.Wrong, false, false), Is.EqualTo(TypeBeatStyle.ErrorChar));

            foreach (var state in new[] { CellState.Untyped, CellState.Missed, CellState.Abandoned, CellState.AutoSkipped })
                Assert.That(fill(state, false, false), Is.EqualTo(TypeBeatStyle.UntypedChar), state.ToString());
        }

        [Test]
        public void FlagOffTheSungSyllableInputHasNoEffectAtAll()
        {
            // The one input the experiment added must be inert with the flag down, for every state
            // and along the ramp; this is what keeps flag-off rendering byte-identical to today.
            foreach (var state in all_states)
            {
                foreach (double? q in new double?[] { null, 0, 0.5, 1 })
                {
                    Assert.That(fill(state, false, inSung: true, quality: q),
                        Is.EqualTo(fill(state, false, inSung: false, quality: q)),
                        $"{state}, quality {q?.ToString() ?? "null"}");
                }
            }
        }

        // --- The green itself: legible and unmistakable ---

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

        [Test]
        public void TheGreenReadsAgainstThePlayfieldAndMatchesNoOtherVoice()
        {
            var green = TypeBeatStyle.SyllableCorrectChar;

            // Body-text legibility on the serika-dark panel (measured ~6.2:1; the >= 4.5 is the
            // contract, WCAG's AA bar for text).
            Assert.That(contrast(green, TypeBeatStyle.Background), Is.GreaterThanOrEqualTo(4.5));

            // Every colour a character (or the sweep/caret beside it) can wear must stay
            // unmistakable from it.
            foreach (var other in new[]
                     {
                         TypeBeatStyle.UntypedChar, TypeBeatStyle.TypedChar, TypeBeatStyle.ErrorChar,
                         TypeBeatStyle.Caret, TypeBeatStyle.SungAccent, TypeBeatStyle.FreestyleChar,
                     })
                Assert.That(green, Is.Not.EqualTo(other));
        }
    }
}
