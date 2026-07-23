// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Ported verbatim from type!beat TypeBeat.Game/UI/LyricLineDisplay.cs.
// SpriteText -> OsuSpriteText (fork bans bare SpriteText); constant names restyled.

using System;
using System.Collections.Generic;
using osu.Framework.Allocation;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using osuTK;

namespace typebeat.Game.Rulesets.TypeBeat.UI
{
    /// <summary>
    /// Renders one <see cref="TypingLine"/> as per-cell <see cref="OsuSpriteText"/>s at the
    /// font's natural (proportional) advances — every glyph is measured individually, so the
    /// caret/sweep math never assumes a constant advance. Per-cell colouring, judgement
    /// feedback (Perfect pop / Wrong shake), and the sung-position underline sweep. State is
    /// read pull-based via <see cref="RefreshCell"/> — no engine reference is held.
    /// </summary>
    public partial class LyricLineDisplay : CompositeDrawable
    {
        private const float design_width = 1366f;
        private const float max_width_fraction = 0.9f;

        public TypingLine Line { get; }

        private readonly float requestedFontSize;

        /// <summary>Resolved gameplay-font family (null/empty = the built-in lyric font). Set at construction;
        /// the owning stage decides the value and guarantees the family is registered before it is used.</summary>
        private readonly string? fontFamily;

        private Container content = null!;
        private Box sweepTrack = null!;
        private Box sweepFill = null!;
        private Box sweepGlow = null!;
        private OsuSpriteText[] cells = Array.Empty<OsuSpriteText>();
        private float[] advances = Array.Empty<float>();

        /// <summary>Per-cell alpha driven purely by judgement state (Missed dims to 0.4, else 1);
        /// the flashlight window multiplies on top of this so the two never clobber each other.</summary>
        private float[] cellStateAlpha = Array.Empty<float>();

        /// <summary>Content-local left edge of each cell; length = Cells.Count + 1 (last entry = end of line).</summary>
        private float[] cellX = { 0f };

        private float contentScale = 1f;
        private float glyphHeight;

        /// <summary>Reference advance (content-local px) — a measured letter's width, used as the
        /// fallback for glyphs that produced no measurement. Valid after load.</summary>
        public float CharWidth { get; private set; }

        /// <summary>Content-local width of the whole line (before the auto-shrink scale).</summary>
        public float FullSweepWidth => cellX[^1];

        /// <summary>Current sung-sweep fill width in content-local px.</summary>
        public float SweepFillWidth => sweepFill.IsNotNull() ? sweepFill.Width : 0f;

        /// <summary>Effective on-screen height of a glyph row (after auto-shrink scaling).</summary>
        public float LineHeight => glyphHeight * contentScale;

        /// <summary>Effective on-screen advance of a specific cell (after auto-shrink scaling) —
        /// the width a cell-covering caret style (block/outline/underline) spans there. Advances
        /// are proportional, so this varies per cell; past-the-end uses the last cell's width.</summary>
        public float CellWidthAt(int cellIndex)
        {
            if (advances.Length == 0)
                return CharWidth * contentScale;

            return advances[Math.Clamp(cellIndex, 0, advances.Length - 1)] * contentScale;
        }

        public LyricLineDisplay(TypingLine line, float fontSize = TypeBeatStyle.LYRIC_FONT_SIZE, string? fontFamily = null)
        {
            Line = line;
            requestedFontSize = fontSize;
            this.fontFamily = fontFamily;
            AutoSizeAxes = Axes.Both;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            int n = Line.Cells.Count;
            cells = new OsuSpriteText[n];
            cellStateAlpha = new float[n];
            Array.Fill(cellStateAlpha, 1f);

            content = new Container { AutoSizeAxes = Axes.Both };

            sweepTrack = new Box
            {
                Colour = TypeBeatStyle.SungAccent.Opacity(0.20f),
                Height = 3,
            };
            sweepFill = new Box
            {
                Colour = TypeBeatStyle.SungAccent.Opacity(0.60f),
                Height = 3,
                Width = 0,
            };
            sweepGlow = new Box
            {
                Colour = TypeBeatStyle.SungAccent,
                Height = 3,
                Width = 6,
                Origin = Anchor.TopCentre,
                Alpha = 0,
            };

            content.Add(sweepTrack);
            content.Add(sweepFill);
            content.Add(sweepGlow);

            for (int i = 0; i < n; i++)
            {
                var cell = new OsuSpriteText
                {
                    Font = TypeBeatStyle.Lyric(requestedFontSize, fontFamily),
                    Text = Line.Cells[i].Expected.ToString(),
                    Colour = TypeBeatStyle.UntypedChar,
                    Anchor = Anchor.TopLeft,
                    Origin = Anchor.Centre,
                    // Drop shadow (OsuSpriteText enables Shadow by default, but faintly): darken it
                    // so glyphs stay legible over a beatmap video/image, not just the flat panel.
                    ShadowColour = TypeBeatStyle.TextShadow,
                    ShadowOffset = TypeBeatStyle.TEXT_SHADOW_OFFSET,
                };
                cells[i] = cell;
                content.Add(cell);
            }

            InternalChild = content;
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            measureAndLayout();

            for (int i = 0; i < cells.Length; i++)
                RefreshCell(i);

            SetSungPosition(0);
        }

        private void measureAndLayout()
        {
            int n = cells.Length;

            // Reference advance from a loaded non-space glyph; fall back to an estimate
            // if the layout has not produced a width yet.
            float refAdvance = 0f;

            for (int i = 0; i < n; i++)
            {
                if (Line.Cells[i].IsTypeable && Line.Cells[i].Expected != ' ' && cells[i].DrawWidth > 0.1f)
                {
                    refAdvance = cells[i].DrawWidth;
                    break;
                }
            }

            if (refAdvance <= 0.1f)
                refAdvance = requestedFontSize * 0.6f;

            glyphHeight = 0f;

            for (int i = 0; i < n; i++)
                glyphHeight = Math.Max(glyphHeight, cells[i].DrawHeight);

            if (glyphHeight <= 0.1f)
                glyphHeight = requestedFontSize;

            CharWidth = refAdvance;
            cellX = new float[n + 1];
            advances = new float[Math.Max(n, 1)];

            // Natural proportional layout: every cell advances by its own measured glyph width.
            // A glyph that yielded no measurement (unloaded, or a space on fonts whose lone-space
            // SpriteText measures empty) falls back to a sensible estimate.
            float x = 0f;

            for (int i = 0; i < n; i++)
            {
                float a = cells[i].DrawWidth > 0.1f
                    ? cells[i].DrawWidth
                    : Line.Cells[i].Expected == ' ' ? refAdvance * 0.55f : refAdvance;
                cellX[i] = x;
                advances[i] = a;
                x += a;
            }

            cellX[n] = x;

            // Auto-shrink guard: keep the rendered line within 90% of the design width.
            float total = cellX[n];
            float maxWidth = design_width * max_width_fraction;
            contentScale = total > maxWidth && total > 0f ? maxWidth / total : 1f;
            content.Scale = new Vector2(contentScale);

            for (int i = 0; i < n; i++)
                cells[i].Position = new Vector2(cellX[i] + advances[i] * 0.5f, glyphHeight * 0.5f);

            sweepTrack.Width = cellX[n];
            sweepTrack.Y = sweepFill.Y = sweepGlow.Y = glyphHeight + 6f;
        }

        /// <summary>Display-local caret anchor for a cell; <c>cellIndex == Cells.Count</c> is the end of the line.</summary>
        public Vector2 PositionOfCell(int cellIndex)
        {
            int i = Math.Clamp(cellIndex, 0, cellX.Length - 1);
            return new Vector2(cellX[i] * contentScale, 0f);
        }

        /// <summary>Display-local point for a fractional (sung) cell index.</summary>
        public Vector2 SungPositionPoint(double fractionalCellIndex) => new Vector2(localXFor(fractionalCellIndex) * contentScale, 0f);

        private float localXFor(double fractionalCellIndex)
        {
            double f = Math.Clamp(fractionalCellIndex, 0, cellX.Length - 1);
            int lo = (int)Math.Floor(f);
            int hi = Math.Min(lo + 1, cellX.Length - 1);
            float frac = (float)(f - lo);
            return cellX[lo] + (cellX[hi] - cellX[lo]) * frac;
        }

        public void RefreshCell(int cellIndex)
        {
            if (cellIndex < 0 || cellIndex >= cells.Length)
                return;

            var cell = cells[cellIndex];

            switch (Line.Cells[cellIndex].State)
            {
                case CellState.Correct:
                    cell.Colour = TypeBeatStyle.TypedChar;
                    cellStateAlpha[cellIndex] = 1f;
                    break;

                case CellState.Wrong:
                    // Expected glyph shown in error red (not the typed char).
                    cell.Colour = TypeBeatStyle.ErrorChar;
                    cellStateAlpha[cellIndex] = 1f;
                    break;

                case CellState.Missed:
                    cell.Colour = TypeBeatStyle.UntypedChar;
                    cellStateAlpha[cellIndex] = 0.4f;
                    break;

                default: // Untyped, AutoSkipped
                    cell.Colour = TypeBeatStyle.UntypedChar;
                    cellStateAlpha[cellIndex] = 1f;
                    break;
            }

            // Compose the state alpha with the flashlight window (a no-op multiplier of 1 when the
            // mod is off) so a state refresh can never undo the window's hiding, or vice versa.
            applyCellAlpha(cellIndex, animate: false);
        }

        public void PlayJudgementFeedback(CharJudgement judgement)
        {
            int i = judgement.CellIndex;
            if (i < 0 || i >= cells.Length)
                return;

            var cell = cells[i];

            if (judgement.Type == JudgementType.Perfect)
            {
                cell.ScaleTo(1.25f).Then().ScaleTo(1f, 120, Easing.OutQuint);
            }
            else if (judgement.Type == JudgementType.WrongChar)
            {
                float baseX = cellX[i] + advances[i] * 0.5f;
                cell.MoveToX(baseX - 2f, 25)
                    .Then().MoveToX(baseX + 2f, 25)
                    .Then().MoveToX(baseX, 15, Easing.OutQuint);
            }
        }

        public void SetSungPosition(double fractionalCellIndex)
        {
            if (sweepFill.IsNull())
                return;

            float localX = localXFor(fractionalCellIndex);
            sweepFill.Width = localX;
            sweepGlow.X = localX;
            sweepGlow.Alpha = localX > 0.5f ? 0.9f : 0f;
        }

        public void SetLineDim(float dimAmount)
        {
            if (content.IsNull())
                return;

            float alpha = Math.Clamp(1f - dimAmount, 0f, 1f);
            content.FadeTo(alpha, 150, Easing.OutQuint);
        }

        // --- Flashlight mod: per-character visibility window ---
        // The mod lights only a fixed run of COUNTABLE chars (typeable and not a space) either side
        // of the caret head; spaces and punctuation inside that run stay lit but do not spend the
        // budget, and everything past it (including the whole of the two inactive stack lines) is
        // hidden. The stage drives this each frame off the engine caret, so it slides with both
        // correct and wrong-advancing input and stays purely visual (judgement is untouched).

        private const float flashlight_soft_alpha = 0.35f;
        private const double flashlight_fade_ms = 90;

        private bool flashlightEnabled;
        private int flashlightCaret = -1;   // caret cell index the window is centred on; < 0 = hide the whole line
        private int flashlightRadius = -1;
        private float[] flashlightAlphas = Array.Empty<float>();

        /// <summary>Light the window centred on the caret. Cheap to call every frame: it only
        /// recomputes and re-fades when the caret or radius actually moves.</summary>
        public void SetFlashlightWindow(int caretCellIndex, int radius)
        {
            if (flashlightEnabled && flashlightCaret == caretCellIndex && flashlightRadius == radius)
                return;

            flashlightEnabled = true;
            flashlightCaret = caretCellIndex;
            flashlightRadius = radius;
            flashlightAlphas = ComputeWindowAlphas(Line.Cells, caretCellIndex, radius, flashlight_soft_alpha);

            reapplyFlashlight();

            // The active line keeps its sung sweep; restore it in case this line was hidden while upcoming.
            if (sweepTrack.IsNotNull())
            {
                sweepTrack.FadeTo(1f, flashlight_fade_ms);
                sweepFill.FadeTo(1f, flashlight_fade_ms);
            }
        }

        /// <summary>Hide the whole line: the inactive stack lines, plus every line during pre-roll
        /// and the dead zones between lines (you must not be able to read ahead of the caret).</summary>
        public void HideForFlashlight()
        {
            if (flashlightEnabled && flashlightCaret < 0)
                return;

            flashlightEnabled = true;
            flashlightCaret = -1;
            flashlightRadius = -1;

            reapplyFlashlight();

            if (sweepTrack.IsNotNull())
            {
                sweepTrack.FadeTo(0f, flashlight_fade_ms);
                sweepFill.FadeTo(0f, flashlight_fade_ms);
                sweepGlow.FadeTo(0f, flashlight_fade_ms);
            }
        }

        private void reapplyFlashlight()
        {
            for (int i = 0; i < cells.Length; i++)
                applyCellAlpha(i, animate: true);
        }

        private void applyCellAlpha(int i, bool animate)
        {
            float target = cellStateAlpha[i] * flashlightFactor(i);

            if (animate)
                cells[i].FadeTo(target, flashlight_fade_ms, Easing.OutQuint);
            else
                cells[i].Alpha = target;
        }

        private float flashlightFactor(int i)
        {
            if (!flashlightEnabled)
                return 1f;
            if (flashlightCaret < 0)
                return 0f;
            return i < flashlightAlphas.Length ? flashlightAlphas[i] : 0f;
        }

        /// <summary>
        /// Per-cell visibility multipliers for the flashlight window: 1 for a lit char, a soft value
        /// for the outermost lit char on a side that has more hidden line beyond it, 0 for a hidden
        /// char. The budget counts only COUNTABLE cells (typeable and not a space), so
        /// <paramref name="radius"/> lit chars reach each side of the caret regardless of how many
        /// spaces or punctuation marks sit between them; a space/punctuation cell strictly inside the
        /// lit span stays lit, one at or past the edge is hidden. Pure (no drawable state) so the
        /// window math is unit-testable.
        /// </summary>
        public static float[] ComputeWindowAlphas(IReadOnlyList<TypingCell> cells, int caretCellIndex, int radius, float softAlpha)
        {
            int n = cells.Count;
            var result = new float[n];

            if (n == 0 || radius <= 0)
                return result;

            // pref[i] = countable cells strictly before i; pref[n] = total countable in the line.
            int[] pref = new int[n + 1];
            for (int i = 0; i < n; i++)
                pref[i + 1] = pref[i] + (isCountable(cells[i]) ? 1 : 0);

            int total = pref[n];
            int caret = Math.Clamp(caretCellIndex, 0, n);
            int caretBudget = pref[caret];     // countable chars strictly left of the caret head
            int lo = caretBudget - radius;     // leftmost lit countable slot (inclusive)
            int hi = caretBudget + radius - 1; // rightmost lit countable slot (inclusive)

            for (int i = 0; i < n; i++)
            {
                if (isCountable(cells[i]))
                {
                    int slot = pref[i];

                    if (slot < lo || slot > hi)
                        continue; // hidden

                    // Soften the outermost lit char on a side only when a hidden char lies just
                    // beyond it, so the window fades into darkness but a real line start/end (no
                    // more line beyond) stays a hard, full-alpha edge.
                    bool fadesLeft = slot == lo && slot - 1 >= 0;
                    bool fadesRight = slot == hi && slot + 1 <= total - 1;
                    result[i] = fadesLeft || fadesRight ? softAlpha : 1f;
                }
                else
                {
                    // Space / punctuation: lit only when it sits strictly between two lit countable
                    // chars, so leading/trailing marks and the space just past the edge stay hidden.
                    int leftSlot = pref[i] - 1;
                    int rightSlot = pref[i];
                    bool leftLit = leftSlot >= 0 && leftSlot >= lo && leftSlot <= hi;
                    bool rightLit = rightSlot <= total - 1 && rightSlot >= lo && rightSlot <= hi;
                    result[i] = leftLit && rightLit ? 1f : 0f;
                }
            }

            return result;
        }

        private static bool isCountable(TypingCell cell) => cell.IsTypeable && cell.Expected != ' ';

        // --- Test-support accessors (public so cross-assembly test scenes can assert) ---

        public int CellCount => cells.Length;

        public float CellAlpha(int index) => index >= 0 && index < cells.Length ? cells[index].Alpha : 0f;
    }
}
