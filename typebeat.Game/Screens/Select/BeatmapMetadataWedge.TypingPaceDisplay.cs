// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Rendering.Vertices;
using osu.Framework.Graphics.Shaders;
using osu.Framework.Localisation;
using osu.Framework.Utils;
using typebeat.Game.Beatmaps;
using typebeat.Game.Graphics;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Overlays;
using osuTK;

namespace typebeat.Game.Screens.Select
{
    public partial class BeatmapMetadataWedge
    {
        /// <summary>
        /// The map's typing pace: a WPM curve over its length, with the peak and average WPM/CPM
        /// spelled out beside it. Unlike the rest of this wedge the data is LOCAL, computed from the
        /// selected beatmap's own lyric lines, so it needs no online lookup.
        /// </summary>
        public partial class TypingPaceDisplay : CompositeDrawable
        {
            private readonly GraphDrawable wpmGraph;
            private readonly PaceRow peakRow;
            private readonly PaceRow averageRow;

            /// <summary>Width reserved to the right of the graph for the peak/average readouts.</summary>
            private const float readout_width = 150f;

            /// <summary>
            /// Null leaves the last values standing: the wedge hides the whole section for a map
            /// with no pace to show, so blanking here would only be visible mid-fade.
            /// </summary>
            public TypingPaceProfile? Data
            {
                set
                {
                    if (value == null)
                        return;

                    // The curve is raw WPM; normalising it for display is this end's job. Scaling by
                    // the peak (which is by construction the curve's own maximum) keeps the tallest
                    // bar full height whatever the map's absolute speed.
                    double peak = value.PeakWpm;

                    wpmGraph.Data = value.WpmCurve.Select(v => peak <= 0 ? 0 : (float)(v / peak)).ToArray();

                    peakRow.SetValues(value.PeakWpm, value.PeakCpm);
                    averageRow.SetValues(value.AverageWpm, value.AverageCpm);
                }
            }

            public TypingPaceDisplay()
            {
                RelativeSizeAxes = Axes.X;
                AutoSizeAxes = Axes.Y;

                InternalChild = new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical,
                    Spacing = new Vector2(0f, 4f),
                    Children = new Drawable[]
                    {
                        new OsuSpriteText
                        {
                            Text = @"Typing pace",
                            Font = OsuFont.Style.Caption1.With(weight: FontWeight.SemiBold),
                            Margin = new MarginPadding { Bottom = 4f },
                        },
                        new Container
                        {
                            RelativeSizeAxes = Axes.X,
                            Height = 65f,
                            Children = new Drawable[]
                            {
                                new Container
                                {
                                    RelativeSizeAxes = Axes.Both,
                                    Padding = new MarginPadding { Right = readout_width + 15f },
                                    Child = wpmGraph = new GraphDrawable { RelativeSizeAxes = Axes.Both },
                                },
                                new FillFlowContainer
                                {
                                    Anchor = Anchor.CentreRight,
                                    Origin = Anchor.CentreRight,
                                    Width = readout_width,
                                    AutoSizeAxes = Axes.Y,
                                    Direction = FillDirection.Vertical,
                                    Spacing = new Vector2(0f, 4f),
                                    Children = new[]
                                    {
                                        peakRow = new PaceRow(@"Peak"),
                                        averageRow = new PaceRow(@"Average"),
                                    },
                                },
                            },
                        },
                    },
                };
            }

            [BackgroundDependencyLoader]
            private void load(OsuColour colours)
            {
                wpmGraph.Colour = colours.Blue1;
            }

            /// <summary>One labelled "&lt;label&gt; &lt;n&gt; WPM &lt;n&gt; CPM" line of the readout.</summary>
            private partial class PaceRow : CompositeDrawable
            {
                private const float label_width = 52f;
                private const float value_width = 52f;

                private readonly OsuSpriteText wpmText;
                private readonly OsuSpriteText cpmText;

                public PaceRow(LocalisableString label)
                {
                    RelativeSizeAxes = Axes.X;
                    AutoSizeAxes = Axes.Y;

                    InternalChild = new FillFlowContainer
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Direction = FillDirection.Horizontal,
                        Children = new Drawable[]
                        {
                            new Container
                            {
                                Width = label_width,
                                AutoSizeAxes = Axes.Y,
                                Child = new OsuSpriteText
                                {
                                    Text = label,
                                    Font = OsuFont.Style.Caption1.With(weight: FontWeight.SemiBold),
                                },
                            },
                            new Container
                            {
                                Width = value_width,
                                AutoSizeAxes = Axes.Y,
                                Child = wpmText = new OsuSpriteText { Font = OsuFont.Style.Caption1 },
                            },
                            cpmText = new OsuSpriteText { Font = OsuFont.Style.Caption1 },
                        },
                    };
                }

                public void SetValues(double wpm, double cpm)
                {
                    wpmText.Text = $@"{wpm:0} WPM";
                    cpmText.Text = $@"{cpm:0} CPM";
                }

                [BackgroundDependencyLoader]
                private void load(OverlayColourProvider colourProvider)
                {
                    wpmText.Colour = colourProvider.Content2;
                    cpmText.Colour = colourProvider.Content2;
                }
            }

            private partial class GraphDrawable : Drawable
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
                    private readonly GraphDrawable source;

                    private Vector2 drawSize;
                    private float[] displayedData = null!;
                    private IShader shader = null!;
                    private IVertexBatch<TexturedVertex2D>? quadBatch;

                    public GraphDrawNode(GraphDrawable source)
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
    }
}
