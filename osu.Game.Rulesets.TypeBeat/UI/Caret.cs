// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Ported verbatim from type!beat TypeBeat.Game/UI/Caret.cs; only namespace/constant names changed.

using System;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Utils;
using osuTK;
using osuTK.Graphics;

namespace osu.Game.Rulesets.TypeBeat.UI
{
    /// <summary>
    /// Monkeytype-fidelity caret: a 3px beam that damps toward a target position,
    /// snaps on line jumps, and blinks (530ms) only while idle. The same class is
    /// reused as the sung caret (recoloured, slower damp, no blink).
    /// </summary>
    public partial class Caret : CompositeDrawable
    {
        private readonly double dampHalfTime;
        private readonly bool blinks;
        private readonly Box beam;

        private Vector2 target;
        private bool snapNextFrame;
        private double lastActivityTime = double.MinValue;

        public Caret(Color4 colour, double dampHalfTime, bool blinks)
        {
            this.dampHalfTime = dampHalfTime;
            this.blinks = blinks;

            Width = 3;
            // Beam straddles the cell boundary the caret sits on.
            Origin = Anchor.TopCentre;

            InternalChild = beam = new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = colour,
            };
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

        /// <summary>Resets the blink timer and forces the beam fully visible.</summary>
        public void NotifyTyped()
        {
            if (IsLoaded)
                lastActivityTime = Time.Current;
            beam.Alpha = 1f;
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
                beam.Alpha = 1f;
            }
            else if (moving || Time.Current - lastActivityTime < TypeBeatStyle.CARET_BLINK_PERIOD)
            {
                beam.Alpha = 1f;
            }
            else
            {
                double phase = (Time.Current - lastActivityTime - TypeBeatStyle.CARET_BLINK_PERIOD) / TypeBeatStyle.CARET_BLINK_PERIOD;
                beam.Alpha = (float)(0.5 + 0.5 * Math.Cos(phase * Math.PI * 2));
            }
        }
    }
}
