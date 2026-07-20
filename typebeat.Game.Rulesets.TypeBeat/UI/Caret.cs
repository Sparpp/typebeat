// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Ported verbatim from type!beat TypeBeat.Game/UI/Caret.cs; only namespace/constant names changed.

using System;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Utils;
using osuTK;
using osuTK.Graphics;
using typebeat.Game.Rulesets.TypeBeat.Configuration;

namespace typebeat.Game.Rulesets.TypeBeat.UI
{
    /// <summary>
    /// Monkeytype-fidelity caret that damps toward a target position, snaps on line jumps,
    /// and blinks (530ms) only while idle. Renders in any of monkeytype's styles
    /// (<see cref="CaretStyle"/>): the classic 3px beam straddling the cell boundary, or a
    /// block/outline/underline covering the current cell. The same class is reused as the
    /// sung caret (recoloured, slower damp, no blink, always a beam).
    /// </summary>
    public partial class Caret : CompositeDrawable
    {
        /// <summary>Rendering style; bind to config for the player caret, leave at Line for the sung caret.</summary>
        public readonly Bindable<CaretStyle> Style = new Bindable<CaretStyle>(CaretStyle.Line);

        private const float beam_width = 3;
        private const float outline_thickness = 2;
        private const float underline_height = 3;
        private const float block_alpha = 0.4f;

        private readonly Color4 colour;
        private readonly double dampHalfTime;
        private readonly bool blinks;

        /// <summary>The style-built visual; blink/idle modulates its alpha.</summary>
        private readonly Container visual;

        /// <summary>On-screen advance of the cell the caret sits on (cell-covering styles only).</summary>
        private float cellWidth = 14f;

        private Vector2 target;
        private bool snapNextFrame;
        private double lastActivityTime = double.MinValue;

        public Caret(Color4 colour, double dampHalfTime, bool blinks)
        {
            this.colour = colour;
            this.dampHalfTime = dampHalfTime;
            this.blinks = blinks;

            Width = beam_width;
            // The drawable's Position stays the cell's left boundary point in every style:
            // the beam straddles it (as the original 3px caret always did), the cell styles
            // extend rightward from it.
            Origin = Anchor.TopCentre;

            InternalChild = visual = new Container
            {
                RelativeSizeAxes = Axes.Y,
                Colour = colour,
            };

            applyStyle();
            Style.BindValueChanged(_ => applyStyle());
        }

        /// <summary>Sets the on-screen width of the current cell (block/outline/underline sizing).</summary>
        public void SetCellWidth(float width)
        {
            if (width <= 0 || Precision.AlmostEquals(width, cellWidth))
                return;

            cellWidth = width;

            if (Style.Value != CaretStyle.Line)
                visual.Width = width;
        }

        private void applyStyle()
        {
            visual.Clear();

            // Reset outline-only state so styles don't leak into each other.
            visual.Masking = false;
            visual.BorderThickness = 0;

            switch (Style.Value)
            {
                case CaretStyle.Line:
                    visual.X = 0;
                    visual.Width = beam_width;
                    visual.Add(new Box { RelativeSizeAxes = Axes.Both });
                    break;

                case CaretStyle.Block:
                    // From the boundary (drawable-local +1.5 = the cell's left edge) across the cell.
                    visual.X = beam_width / 2;
                    visual.Width = cellWidth;
                    visual.Add(new Box { RelativeSizeAxes = Axes.Both, Alpha = block_alpha });
                    break;

                case CaretStyle.Outline:
                    visual.X = beam_width / 2;
                    visual.Width = cellWidth;
                    visual.Masking = true;
                    visual.BorderThickness = outline_thickness;
                    visual.BorderColour = colour;
                    // An invisible-but-present child is required for the masked border to draw.
                    visual.Add(new Box { RelativeSizeAxes = Axes.Both, Alpha = 0, AlwaysPresent = true });
                    break;

                case CaretStyle.Underline:
                    visual.X = beam_width / 2;
                    visual.Width = cellWidth;
                    visual.Add(new Box
                    {
                        RelativeSizeAxes = Axes.X,
                        Height = underline_height,
                        Anchor = Anchor.BottomLeft,
                        Origin = Anchor.BottomLeft,
                    });
                    break;
            }
        }

        /// <summary>Damped move toward <paramref name="position"/>; snaps if the jump exceeds ~one line height.</summary>
        public void MoveToTarget(Vector2 position)
        {
            float lineHeight = DrawHeight > 0 ? DrawHeight : 40f;

            if ((position - Position).Length > lineHeight * 1.5f)
            {
                SnapTo(position);
                return;
            }

            target = position;
        }

        public void SnapTo(Vector2 position)
        {
            target = position;
            Position = position;
            snapNextFrame = true;
        }

        /// <summary>Resets the blink timer and forces the visual fully visible.</summary>
        public void NotifyTyped()
        {
            if (IsLoaded)
                lastActivityTime = Time.Current;
            visual.Alpha = 1f;
        }

        protected override void Update()
        {
            base.Update();

            double elapsed = Time.Elapsed;

            if (snapNextFrame)
            {
                Position = target;
                snapNextFrame = false;
            }
            else
            {
                float x = (float)Interpolation.DampContinuously(Position.X, target.X, dampHalfTime, elapsed);
                float y = (float)Interpolation.DampContinuously(Position.Y, target.Y, dampHalfTime, elapsed);
                Position = new Vector2(x, y);
            }

            bool moving = (Position - target).Length > 0.75f;

            if (!blinks)
            {
                visual.Alpha = 1f;
            }
            else if (moving || Time.Current - lastActivityTime < TypeBeatStyle.CARET_BLINK_PERIOD)
            {
                visual.Alpha = 1f;
            }
            else
            {
                double phase = (Time.Current - lastActivityTime - TypeBeatStyle.CARET_BLINK_PERIOD) / TypeBeatStyle.CARET_BLINK_PERIOD;
                visual.Alpha = (float)(0.5 + 0.5 * Math.Cos(phase * Math.PI * 2));
            }
        }
    }
}
