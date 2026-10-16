// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Graphics;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.UI
{
    // The hiding mods: the Flashlight window and Recite, and the one point where they compose with
    // each cell's judgement-state alpha.
    public partial class LyricLineDisplay
    {
        // --- Flashlight mod: per-character visibility window ---
        // The mod lights only a fixed run of COUNTABLE chars (typeable and not a space) either side
        // of the caret head; spaces and punctuation inside that run stay lit but do not spend the
        // budget. The window is computed at the STREAM level (the whole lyric stack read as one
        // continuous run of countable chars) by the stage, so its budget spills across line
        // boundaries: the tail of one line and the head of the next can be lit at once. Each line
        // gets its own slice as a LineWindow, applied here. Purely visual: judgement is untouched.

        private const float flashlight_soft_alpha = 0.35f;
        private const double flashlight_fade_ms = 90;

        private bool flashlightEnabled;
        private bool flashlightHidden = true;   // true = the whole line is hidden
        private bool flashlightShowSweep;
        private LineWindow flashlightWindow = LineWindow.Hidden;
        private float[] flashlightAlphas = Array.Empty<float>();

        /// <summary>Light this line's slice of the stream window. <paramref name="showSweep"/> keeps
        /// the sung underline (only the active line wants it; a line lit purely by spill does not).
        /// Cheap to call every frame: it only recomputes and re-fades when the slice actually
        /// changes.</summary>
        public void SetFlashlightWindow(LineWindow window, bool showSweep)
        {
            if (flashlightEnabled && !flashlightHidden && flashlightWindow.Equals(window) && flashlightShowSweep == showSweep)
                return;

            flashlightEnabled = true;
            flashlightHidden = false;
            flashlightWindow = window;
            flashlightShowSweep = showSweep;
            flashlightAlphas = FlashlightWindow.ComputeWindowAlphas(Line.Cells, window, flashlight_soft_alpha);

            reapplyFlashlight();

            if (sweepFill.IsNotNull())
            {
                float target = showSweep ? 1f : 0f;

                // EVERY band, not the first: the rail is segmented since backlog 228, and a band
                // left lit would leak the shape of a line the mod is meant to be hiding.
                foreach (var track in sweepTracks)
                    track.FadeTo(target, flashlight_fade_ms);

                foreach (var blend in sweepBlends)
                    blend.FadeTo(target, flashlight_fade_ms);

                sweepFill.FadeTo(target, flashlight_fade_ms);

                if (!showSweep)
                    sweepGlow.FadeTo(0f, flashlight_fade_ms);
            }
        }

        /// <summary>Hide the whole line: lines wholly outside the stream window, plus every line during
        /// pre-cue dead zones and pre-roll (you must not be able to read ahead of the caret).</summary>
        public void HideForFlashlight()
        {
            if (flashlightEnabled && flashlightHidden)
                return;

            flashlightEnabled = true;
            flashlightHidden = true;
            flashlightWindow = LineWindow.Hidden;

            reapplyFlashlight();

            if (sweepFill.IsNotNull())
            {
                foreach (var track in sweepTracks)
                    track.FadeTo(0f, flashlight_fade_ms);

                foreach (var blend in sweepBlends)
                    blend.FadeTo(0f, flashlight_fade_ms);

                sweepFill.FadeTo(0f, flashlight_fade_ms);
                sweepGlow.FadeTo(0f, flashlight_fade_ms);
            }
        }

        private void reapplyFlashlight()
        {
            for (int i = 0; i < cells.Length; i++)
                applyCellAlpha(i, animate: true);

            applySyllableMarkers(animate: true);
        }

        private void applyCellAlpha(int i, bool animate)
        {
            // THE single composition point for every reason a cell can be dimmed or hidden: the
            // judgement state and the hiding mods each contribute an independent multiplier (1 when
            // they have nothing to say), so no two of them can clobber each other and no order of
            // application exists to get wrong.
            float target = cellStateAlpha[i] * cellHidingFactor(i);

            // Ruby must never reveal a word hidden by Flashlight or Recite. A dictionary compound
            // can span several kanji, so all of its cells must be visible before its reading is.
            foreach (var (group, ruby) in furigana)
            {
                if (i < group.StartCell || i >= group.EndCellExclusive)
                    continue;
                float alpha = 0.85f;
                for (int at = group.StartCell; at < group.EndCellExclusive; at++)
                    alpha = Math.Min(alpha, cellStateAlpha[at] * cellHidingFactor(at));
                if (animate)
                    ruby.FadeTo(alpha, flashlight_fade_ms, Easing.OutQuint);
                else
                    ruby.Alpha = alpha;
            }

            if (animate)
                cells[i].FadeTo(target, flashlight_fade_ms, Easing.OutQuint);
            else
                cells[i].Alpha = target;
        }

        /// <summary>
        /// How much of cell <paramref name="i"/> the HIDING mods leave visible: the flashlight
        /// window and Recite multiplied, each 1 when its mod is off, so stacking them is exactly
        /// "hidden by either". Split out from <see cref="applyCellAlpha"/> because the syllable
        /// markers need the same answer without the judgement-state lane, which is about a cell's
        /// own history and says nothing about whether it may be read.
        /// </summary>
        private float cellHidingFactor(int i) => flashlightFactor(i) * reciteFactor(i);

        private float flashlightFactor(int i)
        {
            if (!flashlightEnabled)
                return 1f;
            if (flashlightHidden)
                return 0f;
            return i < flashlightAlphas.Length ? flashlightAlphas[i] : 0f;
        }

        // --- Recite (backlog 229) ---
        // Hide every character the player has not typed yet. Unlike the flashlight this needs no
        // geometry at all: the predicate is per-cell state, so the whole mod is one flag plus
        // HiddenByRecite below, and every state transition (a char typed reveals its cell, a
        // backspace re-hides it) falls out of RefreshCell already ending in applyCellAlpha.

        private bool reciteEnabled;

        /// <summary>
        /// Turn the Recite mod's hiding on or off for this line. Idempotent, so the stage can call
        /// it every frame. Deliberately does NOT touch the sung sweep or the glow: unlike
        /// <see cref="HideForFlashlight"/>, Recite hides the WORDS and leaves the map's playhead
        /// visible, which is the one cue a reciting player still has.
        /// </summary>
        public void SetReciteEnabled(bool enabled)
        {
            if (reciteEnabled == enabled)
                return;

            reciteEnabled = enabled;

            // Cells may not exist yet (this can be pushed before the display has loaded); that is
            // safe, because LoadComplete refreshes every cell and RefreshCell ends in applyCellAlpha.
            for (int i = 0; i < cells.Length; i++)
                applyCellAlpha(i, animate: true);

            applySyllableMarkers(animate: true);
        }

        private float reciteFactor(int i)
        {
            if (!reciteEnabled)
                return 1f;

            return i < Line.Cells.Count && HiddenByRecite(Line.Cells[i]) ? 0f : 1f;
        }

        /// <summary>
        /// The Recite hiding rule, pure so it is unit-testable without drawables: a cell is hidden
        /// iff the player has not typed it (<see cref="CellState.Untyped"/>) and it is not a
        /// FREESTYLE slot.
        ///
        /// <para>Freestyle slots stay visible because they already shimmer a random pool glyph that
        /// says nothing about the lyric (see <see cref="refreshFreestyleCell"/>): hiding them would
        /// reveal nothing but would make a whole freestyle section invisible, and the shimmer is how
        /// the player knows a free slot is there at all.</para>
        ///
        /// <para>Everything that is not Untyped is shown, including Missed, Abandoned and
        /// AutoSkipped. Those are all at or behind the caret in practice, and "upcoming" is exactly
        /// what Untyped means: a cell the caret has passed can no longer be read ahead of.</para>
        /// </summary>
        public static bool HiddenByRecite(TypingCell cell) => cell.State == CellState.Untyped && !cell.IsFreestyle;
    }
}
