// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Rendering.Vertices;
using osu.Framework.Graphics.Shaders;
using osu.Framework.Utils;
using osuTK;

namespace typebeat.Game.Beatmaps.Drawables
{
    /// <summary>
    /// A typing pace curve as 100 rounded bars that ease towards new data: song select's metadata wedge and the
    /// beatmap set overlay draw the same graph. <see cref="Data"/> is heights from 0 to 1, first point first.
    /// </summary>
    public partial class PaceGraph : Drawable
    {
        private readonly float[] displayedData = new float[100];

        private float[] data = new float[100];

        public float[] Data
        {
            get => data;
            set
            {
                data = value;
                Invalidate(Invalidation.DrawNode);
            }
        }

        private IShader shader = null!;

        [BackgroundDependencyLoader]
        private void load(ShaderManager shaders)
        {
            shader = shaders.Load(VertexShaderDescriptor.TEXTURE_2, "FastCircle");
        }

        protected override void Update()
        {
            base.Update();

            bool changed = false;

            for (int i = 0; i < displayedData.Length; i++)
            {
                float before = displayedData[i];
                float value = data.ElementAtOrDefault(i);
                displayedData[i] = (float)Interpolation.DampContinuously(displayedData[i], value, 40, Time.Elapsed);
                changed |= displayedData[i] != before;
            }

            if (changed)
                Invalidate(Invalidation.DrawNode);
        }

        protected override DrawNode CreateDrawNode() => new GraphDrawNode(this);

        // todo: consider integrating this with BarGraph
        // this is different from BarGraph since this displays each bar with corner radii applied.
        private class GraphDrawNode : DrawNode
        {
            private readonly PaceGraph source;

            private Vector2 drawSize;
            private float[] displayedData = null!;
            private IShader shader = null!;
            private IVertexBatch<TexturedVertex2D>? quadBatch;

            public GraphDrawNode(PaceGraph source)
                : base(source)
            {
                this.source = source;
            }

            public override void ApplyState()
            {
                base.ApplyState();

                drawSize = source.DrawSize;
                displayedData = source.displayedData;
                shader = source.shader;
            }

            protected override void Draw(IRenderer renderer)
            {
                base.Draw(renderer);

                const float spacing_constant = 1.5f;

                float position = 0;
                float barWidth = drawSize.X / displayedData.Length / spacing_constant;

                float totalSpacing = drawSize.X - barWidth * displayedData.Length;
                float spacing = totalSpacing / (displayedData.Length - 1);

                quadBatch ??= renderer.CreateQuadBatch<TexturedVertex2D>(displayedData.Length * 4, 1);
                shader.Bind();

                for (int i = 0; i < displayedData.Length; i++)
                {
                    float barHeight = MathF.Max(drawSize.Y * displayedData[i], barWidth);

                    drawBar(renderer, position, barWidth, barHeight);

                    position += barWidth + spacing;
                }

                shader.Unbind();
            }

            private void drawBar(IRenderer renderer, float position, float width, float height)
            {
                // Since bars have corner radius, to avoid masking usage and draw all bars in a single draw call
                // we are using FastCircle implementation.
                // Not using FastCircle directly to minimize drawable count.

                RectangleF drawRectangle = new RectangleF(new Vector2(position, drawSize.Y - height), new Vector2(width, height));
                Vector4 textureRectangle = new Vector4(0, 0, drawRectangle.Width, drawRectangle.Height);
                Quad screenSpaceDrawQuad = Quad.FromRectangle(drawRectangle) * DrawInfo.Matrix;

                var blend = new Vector2(Math.Min(drawRectangle.Width, drawRectangle.Height) / Math.Min(screenSpaceDrawQuad.Width, screenSpaceDrawQuad.Height));

                quadBatch?.AddAction(new TexturedVertex2D(renderer)
                {
                    Position = screenSpaceDrawQuad.BottomLeft,
                    TexturePosition = new Vector2(0, drawRectangle.Height),
                    TextureRect = textureRectangle,
                    BlendRange = blend,
                    Colour = DrawColourInfo.Colour.BottomLeft.SRGB,
                });
                quadBatch?.AddAction(new TexturedVertex2D(renderer)
                {
                    Position = screenSpaceDrawQuad.BottomRight,
                    TexturePosition = new Vector2(drawRectangle.Width, drawRectangle.Height),
                    TextureRect = textureRectangle,
                    BlendRange = blend,
                    Colour = DrawColourInfo.Colour.BottomRight.SRGB,
                });
                quadBatch?.AddAction(new TexturedVertex2D(renderer)
                {
                    Position = screenSpaceDrawQuad.TopRight,
                    TexturePosition = new Vector2(drawRectangle.Width, 0),
                    TextureRect = textureRectangle,
                    BlendRange = blend,
                    Colour = DrawColourInfo.Colour.TopRight.SRGB,
                });
                quadBatch?.AddAction(new TexturedVertex2D(renderer)
                {
                    Position = screenSpaceDrawQuad.TopLeft,
                    TexturePosition = Vector2.Zero,
                    TextureRect = textureRectangle,
                    BlendRange = blend,
                    Colour = DrawColourInfo.Colour.TopLeft.SRGB,
                });
            }

            protected override void Dispose(bool isDisposing)
            {
                base.Dispose(isDisposing);

                quadBatch?.Dispose();
            }
        }
    }
}
