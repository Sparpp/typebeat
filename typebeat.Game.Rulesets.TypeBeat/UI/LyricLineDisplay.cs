// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Ported verbatim from type!beat TypeBeat.Game/UI/LyricLineDisplay.cs.
// SpriteText -> OsuSpriteText (fork bans bare SpriteText); constant names restyled.

using System;
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
                    cell.Alpha = 1f;
                    break;

                case CellState.Wrong:
                    // Expected glyph shown in error red (not the typed char).
                    cell.Colour = TypeBeatStyle.ErrorChar;
                    cell.Alpha = 1f;
                    break;

                case CellState.Missed:
                    cell.Colour = TypeBeatStyle.UntypedChar;
                    cell.Alpha = 0.4f;
                    break;

                default: // Untyped, AutoSkipped
                    cell.Colour = TypeBeatStyle.UntypedChar;
                    cell.Alpha = 1f;
                    break;
            }
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
    }
}
