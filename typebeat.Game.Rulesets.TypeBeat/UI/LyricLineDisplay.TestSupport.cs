// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Graphics.Colour;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using osuTK;

namespace typebeat.Game.Rulesets.TypeBeat.UI
{
    // Test-support accessors, read by the ruleset's test scenes to assert what is drawn. Internal: the
    // ruleset grants its test assembly InternalsVisibleTo (Properties/AssemblyInfo.cs), and nothing
    // in the game reads them.
    public partial class LyricLineDisplay
    {
        internal int CellCount => cells.Length;

        internal float CellAlpha(int index) => index >= 0 && index < cells.Length ? cells[index].Alpha : 0f;

        /// <summary>The glyph currently rendered in a cell: the shimmer substitute for an open
        /// freestyle slot, the pressed char once it is filled, the authored char otherwise.</summary>
        internal string CellText(int index) => index >= 0 && index < cells.Length ? cells[index].Text.ToString() : string.Empty;

        /// <summary>The width-matched glyph pool this line's freestyle cells shimmer through.</summary>
        internal IReadOnlyList<char> ShimmerPool => shimmerPool;

        /// <summary>Current alpha of the sung underline sweep's rail and its filled part. Test
        /// support for the one thing that separates the two hiding mods:
        /// <see cref="HideForFlashlight"/> fades these out along with the characters, and Recite
        /// must NOT, because the sweep is the map playhead a reciting player is left reading. A
        /// width alone cannot pin it: a faded sweep still advances.
        ///
        /// <para>The rail is several drawables since backlog 228 (one band per word, see
        /// <see cref="buildPaceTracks"/>), and both flashlight seams fade every band together, so
        /// any one band would do. It reports the MAXIMUM anyway, deliberately: "the rail is
        /// visible" is true if ANY of it is, so a regression that left one band lit under the
        /// flashlight and one that faded a single band under Recite both move this number, which a
        /// fixed band index would miss in one direction or the other.</para></summary>
        internal float SweepTrackAlpha
        {
            get
            {
                float alpha = 0f;

                foreach (var track in sweepTracks)
                    alpha = Math.Max(alpha, track.Alpha);

                foreach (var blend in sweepBlends)
                    alpha = Math.Max(alpha, blend.Alpha);

                return alpha;
            }
        }

        internal float SweepFillAlpha => sweepFill.IsNotNull() ? sweepFill.Alpha : 0f;

        internal float UnrevealedPaceBarWidth => unrevealedPaceBar.IsNotNull() ? unrevealedPaceBar.Width : 0f;
        internal float UnrevealedPaceBarAlpha => unrevealedPaceBar.IsNotNull() ? unrevealedPaceBar.Alpha : 0f;
        internal ColourInfo UnrevealedPaceBarColour => unrevealedPaceBar.Colour;
        internal osu.Framework.Graphics.Primitives.Quad UnrevealedPaceBarScreenQuad => unrevealedPaceBar.ScreenSpaceDrawQuad;

        /// <summary>The colour a cell is currently drawn in; test support for the freestyle tint.</summary>
        internal ColourInfo CellColour(int index) =>
            index >= 0 && index < cells.Length ? cells[index].Colour : ColourInfo.SingleColour(osuTK.Graphics.Color4.White);

        /// <summary>The whole line's on-screen width with every cell occupying its slot (after the
        /// auto-shrink scale). The display's own <c>DrawWidth</c> must equal this at all times, even
        /// when the flashlight has hidden most cells; a smaller <c>DrawWidth</c> means the layout
        /// collapsed to the lit run and the line would re-centre. Test support for the stability pin.</summary>
        internal float FullOnScreenWidth => FullSweepWidth * contentScale;

        /// <summary>Screen-space centre of a cell's glyph; test support for asserting a char does not
        /// move as the flashlight window changes.</summary>
        internal Vector2 CellScreenPosition(int index) =>
            index >= 0 && index < cells.Length ? cells[index].ScreenSpaceDrawQuad.Centre : Vector2.Zero;

        /// <summary>Screen-space quad of a cell's glyph sprite; test support for the lyric size scene
        /// (backlog 334), which pins every adornment against the text it belongs to.</summary>
        internal osu.Framework.Graphics.Primitives.Quad CellScreenQuad(int index) =>
            index >= 0 && index < cells.Length ? cells[index].ScreenSpaceDrawQuad : default;

        /// <summary>Screen-space quad of the sweep rail's first band (every band shares its Y and
        /// thickness); test support for the lyric size scene.</summary>
        internal osu.Framework.Graphics.Primitives.Quad RailScreenQuad => sweepTracks.Length > 0 ? sweepTracks[0].ScreenSpaceDrawQuad : default;

        /// <summary>Screen-space quad of the syllable marker at <paramref name="cellIndex"/>, or the
        /// default quad when no marker sits there; test support for the lyric size scene.</summary>
        internal osu.Framework.Graphics.Primitives.Quad SyllableMarkerScreenQuad(int cellIndex) =>
            markerIndexAt(cellIndex) is int k ? syllableMarkers[k].ScreenSpaceDrawQuad : default;

        /// <summary>First cell of the painted retype selection (see <see cref="SetSelection"/>).</summary>
        internal int SelectionStart { get; private set; }

        /// <summary>One past the last cell of the painted retype selection; equal to
        /// <see cref="SelectionStart"/> when nothing is selected.</summary>
        internal int SelectionEnd { get; private set; }

        /// <summary>Whether the selection highlight is actually being drawn right now.</summary>
        internal bool SelectionVisible => selectionBox.IsNotNull() && selectionBox.Alpha > 0f;

        /// <summary>How many word gaps this line has a dot drawable for (see
        /// <see cref="ComputeSpaceErrorDots"/>); test support.</summary>
        internal int SpaceErrorDotCount => gapDots.Length;

        /// <summary>How many PACE BANDS this line's underline is cut into (see
        /// <see cref="buildPaceTracks"/>); 1 for a display built with no hue. Test support.</summary>
        internal int PaceTrackCount => sweepTracks.Length;

        /// <summary>The colour a pace band is actually drawn in; test support for the hue.</summary>
        internal ColourInfo PaceTrackColour(int index) =>
            index >= 0 && index < sweepTracks.Length ? sweepTracks[index].Colour : ColourInfo.SingleColour(osuTK.Graphics.Color4.White);

        /// <summary>One pace band's own alpha, which the flashlight drives; test support for "every
        /// band fades", which is the band-array half of the pin
        /// <see cref="SweepTrackAlpha"/> carries for the rail as a whole.</summary>
        internal float PaceTrackAlpha(int index) => index >= 0 && index < sweepTracks.Length ? sweepTracks[index].Alpha : 0f;

        /// <summary>A pace band's full logical width, including its halves of neighbouring blends.</summary>
        internal float PaceTrackWidth(int index) => index >= 0 && index < trackBands.Length
            ? cellX[trackBands[index].EndCellExclusive] - cellX[trackBands[index].StartCell] : 0f;

        /// <summary>A pace band's full logical left edge.</summary>
        internal float PaceTrackX(int index) => index >= 0 && index < trackBands.Length
            ? cellX[trackBands[index].StartCell] : 0f;

        /// <summary>The half-open cell range a pace band covers; test support.</summary>
        internal (int StartCell, int EndCellExclusive) PaceTrackRange(int index) =>
            index >= 0 && index < trackBands.Length ? (trackBands[index].StartCell, trackBands[index].EndCellExclusive) : (0, 0);

        /// <summary>The whole line's current dim alpha (see <see cref="SetLineDim"/>), which every
        /// child including the pace bands is multiplied by. Test support for the preview-line pin.</summary>
        internal float ContentAlpha => content.IsNotNull() ? content.Alpha : 0f;

        /// <summary>Whether the space error dot for the gap at <paramref name="cellIndex"/> is
        /// actually being drawn right now; false for any cell that is not a gap. Test support.</summary>
        internal bool SpaceErrorDotVisibleAt(int cellIndex)
        {
            for (int k = 0; k < gapCells.Length; k++)
            {
                if (gapCells[k] == cellIndex)
                    return gapDots[k].Alpha > 0f;
            }

            return false;
        }

        /// <summary>How many syllable markers this line has a drawable for; test support.</summary>
        internal int SyllableMarkerCount => syllableMarkers.Length;

        /// <summary>Whether the syllable marker at <paramref name="cellIndex"/> is actually being
        /// drawn right now; false for any cell that carries no marker. Test support.</summary>
        internal bool SyllableMarkerVisibleAt(int cellIndex) =>
            markerIndexAt(cellIndex) is int k && syllableMarkers[k].Alpha > 0f;

        /// <summary>Display-local X of the syllable marker at <paramref name="cellIndex"/>, in the
        /// same space <see cref="PositionOfCell"/> reports so the two can be compared directly;
        /// <see cref="float.NaN"/> when no marker sits there. Test support for the pin that a mark
        /// lands on the character EDGE (the inter-character gap) and not inside a cell.</summary>
        internal float SyllableMarkerLocalX(int cellIndex) =>
            markerIndexAt(cellIndex) is int k ? syllableMarkers[k].X * contentScale : float.NaN;

        private int? markerIndexAt(int cellIndex)
        {
            for (int k = 0; k < markerCells.Length; k++)
            {
                if (markerCells[k] == cellIndex)
                    return k;
            }

            return null;
        }

        internal float RevealedPaceWidth => revealedPaceWidth;
        internal float JudgementBarAlpha(int index) => index >= 0 && index < judgementColourBars.Length ? judgementColourBars[index].Alpha : 0;
        internal ColourInfo JudgementBarColour(int index) => judgementColourBars[index].Colour;
        internal osu.Framework.Graphics.Primitives.Quad JudgementBarScreenQuad(int index) => judgementColourBars[index].ScreenSpaceDrawQuad;

        internal JudgementIndicatorMode JudgementIndicator => judgementIndicator;
        internal float FloatingBarAlpha(int index) => index >= 0 && index < floatingBars.Length ? floatingBars[index].BarAlpha : 0;
        internal ColourInfo FloatingBarColour(int index) => floatingBars[index].Colour;
        internal osu.Framework.Graphics.Primitives.Quad FloatingBarScreenQuad(int index) => floatingBars[index].ScreenSpaceDrawQuad;
        internal float PaceBlendWidth(int index) => sweepBlends[index].Width;
        internal osu.Framework.Graphics.Primitives.Quad PaceTrackScreenQuad(int index) => sweepTracks[index].ScreenSpaceDrawQuad;

        /// <summary>Rendered masked glyph fraction and bounds, for visual regression checks.</summary>
        internal float CellColourFillFraction(int index) => index >= 0 && index < glyphColourFills.Length && glyphColourFills[index].Alpha > 0
            ? glyphColourFills[index].FillFraction : 0;

        internal float CellColourFillAlpha(int index) => index >= 0 && index < glyphColourFills.Length ? glyphColourFills[index].Alpha : 0;

        internal ColourInfo CellColourFillColour(int index) => glyphColourFills[index].FillColour;

        internal osu.Framework.Graphics.Primitives.Quad CellColourFillClipQuad(int index) => glyphColourFills[index].ClipScreenQuad;

        /// <summary>The group index last highlighted; -1 also covers ungrouped character timing.</summary>
        internal int SungSyllable => sungSyllable;

        /// <summary>Unquantized highlight progress, shared by the brightness fade and glyph fill.</summary>
        internal float CellSungHighlightAmount(int index) => index >= 0 && index < litAmounts.Length ? litAmounts[index] : 0;

        internal float CellVisualScale(int index) => index >= 0 && index < cells.Length ? cells[index].Scale.X : 1f;
    }
}
