// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Shaders;
using osu.Framework.Graphics.Textures;
using osuTK;

namespace typebeat.Game.Rulesets.TypeBeat.UI
{
    /// <summary>A continuous strip whose shared vertical edges carry song-time position and opacity.</summary>
    internal partial class HoldApproachRamp : Drawable
    {
        private const double entry_duration = 100;
        private readonly List<float> positions = new List<float>();
        private RampVertex[] vertices = Array.Empty<RampVertex>();
        private int vertexCount;
        private float letterWidth;
        private float trackWidth;
        private float thickness;
        private Texture texture = null!;
        private IShader shader = null!;

        internal ReadOnlySpan<RampVertex> Vertices => vertices.AsSpan(0, vertexCount);

        internal readonly record struct RampVertex(Vector2 Position, float Alpha);

        public HoldApproachRamp()
        {
            AlwaysPresent = true;
            BypassAutoSizeAxes = Axes.Both;
        }

        [BackgroundDependencyLoader]
        private void load(IRenderer renderer, ShaderManager shaders)
        {
            texture = renderer.WhitePixel;
            shader = shaders.Load(VertexShaderDescriptor.TEXTURE_2, FragmentShaderDescriptor.TEXTURE);
        }

        public static (float OffsetY, float Alpha) Sample(double time, double landingTime, double fadeInDuration, double releaseWindowEnd, float rise)
        {
            if (!double.IsFinite(time))
                return (rise, 0);

            float progress = LyricLineDisplay.SyllableFadeInProgress(time, landingTime, fadeInDuration);
            float entry = LyricLineDisplay.SyllableFadeInProgress(time, landingTime - fadeInDuration + entry_duration, entry_duration);
            return (rise * (1 - progress), entry * LyricLineDisplay.IndicatorRelease(time, releaseWindowEnd));
        }

        public void UpdateRamp(double time, double start, double end, float letterWidth, float trackWidth,
                               double fadeInDuration, double releaseWindowEnd, float rise, float thickness)
        {
            this.letterWidth = letterWidth;
            this.trackWidth = trackWidth;
            this.thickness = thickness;
            vertexCount = 0;
            Invalidate(Invalidation.DrawNode);
            float width = letterWidth + trackWidth;
            if (!double.IsFinite(time) || end <= start || width <= 0 || thickness <= 0)
                return;

            Size = new Vector2(width, rise + thickness);
            // Four screen pixels per segment, plus exact geometric and opacity bends. Capacity is
            // reused across song-time changes, including the draw nodes' independent snapshots.
            float screenWidth = Vector2.Distance(ToScreenSpace(Vector2.Zero), ToScreenSpace(new Vector2(width, 0)));
            int segments = Math.Max(1, (int)Math.Ceiling(screenWidth / 4));
            int capacity = segments + 6;
            if (positions.Capacity < capacity)
                positions.Capacity = capacity;
            if (vertices.Length < capacity)
                Array.Resize(ref vertices, capacity);
            positions.Clear();
            for (int i = 0; i <= segments; i++)
                positions.Add(width * i / segments);
            positions.Add(letterWidth);
            float touchdown = addBend(time);
            float approachStart = addBend(time + fadeInDuration);
            float entryComplete = addBend(time + fadeInDuration - entry_duration);
            positions.Sort();

            foreach (float x in positions)
            {
                if (vertexCount > 0 && x == vertices[vertexCount - 1].Position.X)
                    continue;
                double landing = x <= letterWidth || trackWidth <= 0 ? start : start + (x - letterWidth) / trackWidth * (end - start);
                // Preserve exact bend times: converting float X back to time can round across the
                // instantaneous touchdown when the approach duration is zero.
                if (x == touchdown)
                    landing = time;
                else if (x == approachStart)
                    landing = time + fadeInDuration;
                else if (x == entryComplete)
                    landing = time + fadeInDuration - entry_duration;
                var sample = Sample(time, landing, fadeInDuration, releaseWindowEnd, rise);
                vertices[vertexCount++] = new RampVertex(new Vector2(x, sample.OffsetY), sample.Alpha);
            }

            float addBend(double landing)
            {
                if (landing > start && landing < end)
                {
                    float x = letterWidth + (float)((landing - start) / (end - start)) * trackWidth;
                    positions.Add(x);
                    return x;
                }
                return float.NaN;
            }
        }

        /// <summary>Interpolates the same shared edges consumed by the draw node, rather than re-evaluating timing.</summary>
        public (Vector2 Position, float Alpha) ScreenSample(float trackFraction)
        {
            if (vertexCount == 0)
                return (ToScreenSpace(Vector2.Zero), 0);

            float x = letterWidth + Math.Clamp(trackFraction, 0, 1) * trackWidth;
            for (int i = 1; i < vertexCount; i++)
            {
                RampVertex right = vertices[i];
                if (right.Position.X < x)
                    continue;
                RampVertex left = vertices[i - 1];
                float fraction = (x - left.Position.X) / (right.Position.X - left.Position.X);
                Vector2 position = Vector2.Lerp(left.Position, right.Position, fraction);
                float alpha = left.Alpha + (right.Alpha - left.Alpha) * fraction;
                return (ToScreenSpace(position), alpha * DrawColourInfo.Colour.AverageColour.Linear.A);
            }

            return (ToScreenSpace(vertices[vertexCount - 1].Position), vertices[vertexCount - 1].Alpha * DrawColourInfo.Colour.AverageColour.Linear.A);
        }

        protected override DrawNode CreateDrawNode() => new RampDrawNode(this);

        private sealed class RampDrawNode : DrawNode
        {
            private new HoldApproachRamp Source => (HoldApproachRamp)base.Source;
            private RampVertex[] vertices = Array.Empty<RampVertex>();
            private int vertexCount;
            private float thickness;
            private Texture texture = null!;
            private IShader shader = null!;

            public RampDrawNode(HoldApproachRamp source)
                : base(source)
            {
            }

            public override void ApplyState()
            {
                base.ApplyState();
                texture = Source.texture;
                shader = Source.shader;
                thickness = Source.thickness;
                vertexCount = Source.vertexCount;
                if (vertices.Length < Source.vertices.Length)
                    Array.Resize(ref vertices, Source.vertices.Length);
                Source.Vertices.CopyTo(vertices);
            }

            protected override void Draw(IRenderer renderer)
            {
                base.Draw(renderer);
                if (vertexCount < 2)
                    return;

                shader.Bind();
                Vector2 offset = new Vector2(0, thickness);
                for (int i = 1; i < vertexCount; i++)
                {
                    RampVertex left = vertices[i - 1];
                    RampVertex right = vertices[i];
                    var colour = DrawColourInfo.Colour;
                    colour.ApplyChild(ColourInfo.GradientHorizontal(Colour4.White.Opacity(left.Alpha), Colour4.White.Opacity(right.Alpha)));
                    renderer.DrawQuad(texture, new Quad(
                        Vector2Extensions.Transform(left.Position, DrawInfo.Matrix),
                        Vector2Extensions.Transform(right.Position, DrawInfo.Matrix),
                        Vector2Extensions.Transform(left.Position + offset, DrawInfo.Matrix),
                        Vector2Extensions.Transform(right.Position + offset, DrawInfo.Matrix)), colour);
                }
                shader.Unbind();
            }
        }
    }
}
