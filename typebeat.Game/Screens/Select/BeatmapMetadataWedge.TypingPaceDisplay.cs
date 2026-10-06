// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Localisation;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.Drawables;
using typebeat.Game.Graphics;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Overlays;
using osuTK;

namespace typebeat.Game.Screens.Select
{
    public partial class BeatmapMetadataWedge
    {
        /// <summary>
        /// The map's typing pace: a WPM curve over its length, with the peak, target and average
        /// WPM spelled out beside it. Unlike the rest of this wedge the data is LOCAL, computed from
        /// the selected beatmap's own lyric lines, so it needs no online lookup.
        /// </summary>
        public partial class TypingPaceDisplay : CompositeDrawable
        {
            private readonly PaceGraph wpmGraph;
            private readonly PaceRow peakRow;
            private readonly PaceRow targetRow;
            private readonly PaceRow averageRow;

            /// <summary>Width reserved to the right of the graph for the pace readouts.</summary>
            private const float readout_width = 110f;

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

                    // Peak, target, average: one unit, three readings, hardest first.
                    peakRow.SetValue(value.PeakWpm);
                    targetRow.SetValue(value.TargetWpm);
                    averageRow.SetValue(value.AverageWpm);
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
                                    Child = wpmGraph = new PaceGraph { RelativeSizeAxes = Axes.Both },
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
                                        targetRow = new PaceRow(@"Target"),
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

            /// <summary>
            /// One labelled "&lt;label&gt; &lt;n&gt; WPM" line of the readout. It used to print a
            /// CPM beside the WPM; that column is gone, because a CPM here is its WPM times five
            /// exactly and the space it took is what makes room for the third row.
            /// </summary>
            private partial class PaceRow : CompositeDrawable
            {
                private const float label_width = 52f;

                private readonly OsuSpriteText wpmText;

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
                            wpmText = new OsuSpriteText { Font = OsuFont.Style.Caption1 },
                        },
                    };
                }

                public void SetValue(double wpm) => wpmText.Text = $@"{wpm:0} WPM";

                [BackgroundDependencyLoader]
                private void load(OverlayColourProvider colourProvider)
                {
                    wpmText.Colour = colourProvider.Content2;
                }
            }
        }
    }
}
