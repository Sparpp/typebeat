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
    /// sung caret (recoloured, slower damp, no blink); each instance carries its own
    /// <see cref="Style"/>, and the two are fed from separate user settings, so the heads can
    /// differ in shape as well as in identity.
    ///
    /// <para><see cref="CaretStyle.Highlight"/> is the one member this class cannot draw, because it
    /// is not a shape: it is the sung playhead's "no head at all" choice, handled by hiding this
    /// drawable in <c>LyricStage</c>. It is still accepted here, drawing nothing.</para>
    /// </summary>
    public partial class Caret : CompositeDrawable
    {
        /// <summary>Rendering style, per instance. <c>LyricStage</c> binds the typing caret's to the
        /// user's CaretStyle setting and the sung playhead's to SungCaretStyle, so the two are
        /// independent; left unbound (bare test scenes) it stays on the initialiser below.</summary>
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

        /// <summary>On-screen advance of the cell the caret covers (cell-covering styles only).
        /// The initialiser is only a placeholder for the window before the owner has measured
        /// anything; every real value arrives through <see cref="SetCellWidth"/>.</summary>
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

        /// <summary>
        /// Sets the on-screen width of the covered cell (block/outline/underline sizing). The value
        /// is recorded even while the style is <see cref="CaretStyle.Line"/>, which has no use for it,
        /// so a later switch to a cell-covering style builds from a real measurement instead of the
        /// placeholder initialiser.
        ///
        /// <para>The skip test is against what is DRAWN, not against the stored width. Those two part
        /// company in <see cref="CaretStyle.Line"/> (the visual holds the beam width while the stored
        /// width is a cell width), so skipping on "stored width unchanged" only holds because
        /// <see cref="applyStyle"/> re-reads the stored width on every style change; comparing the
        /// visual removes the dependence on that ordering entirely.</para>
        /// </summary>
        public void SetCellWidth(float width)
        {
            if (width <= 0)
                return;

            cellWidth = width;

            // Line is the fixed 3px beam and never takes a cell width, which is what keeps it
            // byte-identical to the pre-setting behaviour.
            if (Style.Value != CaretStyle.Line && !Precision.AlmostEquals(visual.Width, width))
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

                case CaretStyle.Highlight:
                    // Defensive only. Highlight means "no head at all, light the sung syllable
                    // group instead", so a caret should never be ASKED to draw it: LyricStage hides
                    // the sung head outright while that style is selected, and the typing caret's
                    // dropdown does not offer it. If one does arrive here anyway it must draw
                    // NOTHING rather than fall out of the switch onto whatever the previous style
                    // left behind, hence the alpha-0 child: Update() drives the container's own
                    // alpha, so the invisibility has to live on the shape.
                    visual.X = 0;
                    visual.Width = beam_width;
                    visual.Add(new Box { RelativeSizeAxes = Axes.Both, Alpha = 0f });
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

        // --- Test-support accessors (public so cross-assembly test scenes can assert) ---

        /// <summary>Width of the drawn shape: the 3px beam in <see cref="CaretStyle.Line"/>, the
        /// covered cell's on-screen advance in every other style.</summary>
        public float VisualWidth => visual.Width;
    }
}
