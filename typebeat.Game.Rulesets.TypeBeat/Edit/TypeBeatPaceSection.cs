// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Localisation;
using osu.Framework.Logging;
using osu.Framework.Threading;
using typebeat.Game.Beatmaps;
using typebeat.Game.Graphics;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Overlays;
using typebeat.Game.Rulesets.Objects;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit.Setup;
using osuTK;
using osuTK.Graphics;

namespace typebeat.Game.Rulesets.TypeBeat.Edit
{
    /// <summary>
    /// The map's typing pace in the editor's setup screen (backlog 506): the same WPM graph and rows
    /// as the website's "Map stats" WPM tab (peak, target and average WPM, and characters per word),
    /// computed from the map being edited and refreshed as its lyrics change. It sits beside the
    /// type!beat section, so the setup screen's second row has two columns like its first.
    /// </summary>
    public partial class TypeBeatPaceSection : SetupSection
    {
        /// <summary>The website's bar colour (--violet in site.css, which is the lime accent).</summary>
        private static readonly Color4 bar_colour = Color4Extensions.FromHex(@"c9f24d");

        /// <summary>How long the lyrics have to stay still before the figures are recomputed.</summary>
        private const double refresh_debounce_ms = 300;

        /// <summary>A non-zero bar is never invisible next to the peak, as on the website.</summary>
        private const float min_visible_fraction = 0.03f;

        public override LocalisableString Title => "WPM";

        [Resolved]
        private OverlayColourProvider colourProvider { get; set; } = null!;

        private Container graph = null!;
        private FillFlowContainer rows = null!;
        private OsuSpriteText emptyNote = null!;
        private Container populated = null!;

        private ScheduledDelegate? pendingRefresh;
        private int refreshVersion;

        [BackgroundDependencyLoader]
        private void load()
        {
            Children = new Drawable[]
            {
                new Container
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Masking = true,
                    CornerRadius = 5,
                    CornerExponent = FormControlBackground.CORNER_EXPONENT,
                    Children = new Drawable[]
                    {
                        new Box
                        {
                            RelativeSizeAxes = Axes.Both,
                            Colour = colourProvider.Background4.Darken(0.1f),
                        },
                        new Container
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Padding = new MarginPadding(15),
                            Children = new Drawable[]
                            {
                                emptyNote = new OsuSpriteText
                                {
                                    Text = "No pace graph yet: the map needs more timed lyrics.",
                                    Font = OsuFont.Default.With(size: 16),
                                    Colour = colourProvider.Content2.Opacity(0.6f),
                                },
                                populated = new Container
                                {
                                    RelativeSizeAxes = Axes.X,
                                    AutoSizeAxes = Axes.Y,
                                    Alpha = 0,
                                    Child = new FillFlowContainer
                                    {
                                        RelativeSizeAxes = Axes.X,
                                        AutoSizeAxes = Axes.Y,
                                        Direction = FillDirection.Vertical,
                                        Children = new Drawable[]
                                        {
                                            new Container
                                            {
                                                RelativeSizeAxes = Axes.X,
                                                Height = 96,
                                                Children = new Drawable[]
                                                {
                                                    graph = new Container { RelativeSizeAxes = Axes.Both },
                                                    new Box
                                                    {
                                                        RelativeSizeAxes = Axes.X,
                                                        Height = 1,
                                                        Anchor = Anchor.BottomLeft,
                                                        Origin = Anchor.BottomLeft,
                                                        Colour = colourProvider.Content2.Opacity(0.25f),
                                                    },
                                                },
                                            },
                                            new Container
                                            {
                                                RelativeSizeAxes = Axes.X,
                                                AutoSizeAxes = Axes.Y,
                                                Margin = new MarginPadding { Top = 4, Bottom = 12 },
                                                Children = new Drawable[]
                                                {
                                                    new OsuSpriteText
                                                    {
                                                        Text = "first word",
                                                        Font = OsuFont.Default.With(size: 13),
                                                        Colour = colourProvider.Content2.Opacity(0.5f),
                                                    },
                                                    new OsuSpriteText
                                                    {
                                                        Text = "last word",
                                                        Anchor = Anchor.TopRight,
                                                        Origin = Anchor.TopRight,
                                                        Font = OsuFont.Default.With(size: 13),
                                                        Colour = colourProvider.Content2.Opacity(0.5f),
                                                    },
                                                },
                                            },
                                            rows = new FillFlowContainer
                                            {
                                                RelativeSizeAxes = Axes.X,
                                                AutoSizeAxes = Axes.Y,
                                                Direction = FillDirection.Vertical,
                                                Spacing = new Vector2(0, 8),
                                            },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            };
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            Beatmap.HitObjectAdded += onChanged;
            Beatmap.HitObjectRemoved += onChanged;
            Beatmap.HitObjectUpdated += onChanged;
            Beatmap.BeatmapReprocessed += refreshSoon;

            refresh();
        }

        private void onChanged(HitObject _) => refreshSoon();

        private void refreshSoon()
        {
            pendingRefresh?.Cancel();
            pendingRefresh = Scheduler.AddDelayed(refresh, refresh_debounce_ms);
        }

        /// <summary>
        /// Recomputes off the update thread from a snapshot of the lines. A line is an immutable record
        /// that an edit replaces wholesale, so the snapshot cannot change under the computation, and a
        /// result that an even newer edit has overtaken is dropped.
        /// </summary>
        private void refresh()
        {
            int version = ++refreshVersion;
            var objects = Beatmap.HitObjects.OfType<TypeBeatHitObject>().ToList();

            Task.Run(() =>
            {
                TypingPaceProfile? profile = null;
                double? charsPerWord = null;

                try
                {
                    var snapshot = new TypeBeatBeatmap { HitObjects = objects };
                    profile = snapshot.GetTypingPace();

                    // Chars per word over the lines as written, the quantity the website divides out
                    // of the stored character and word counts.
                    bool literate = objects.Any(h => h.Literate);
                    var stats = LyricPaceStatistics.Compute(objects.Select(h => h.Line), literate);
                    charsPerWord = stats.WordCount > 0 ? stats.AverageCharsPerWord : null;
                }
                catch (System.Exception e)
                {
                    // A half-edited sheet the measurement cannot read shows the empty note, not a crash.
                    Logger.Log($"WPM section could not measure the map: {e.Message}");
                }

                Schedule(() =>
                {
                    if (version == refreshVersion)
                        show(profile, charsPerWord);
                });
            });
        }

        private void show(TypingPaceProfile? profile, double? charsPerWord)
        {
            float peak = profile == null ? 0 : (float)profile.WpmCurve.DefaultIfEmpty(0).Max();

            if (profile == null || peak <= 0)
            {
                populated.Alpha = 0;
                emptyNote.Alpha = 1;
                return;
            }

            emptyNote.Alpha = 0;
            populated.Alpha = 1;

            drawBars(profile.WpmCurve, peak);

            rows.Children = new Drawable[]
            {
                new StatRow("Peak WPM", whole(profile.PeakWpm)),
                new StatRow("Target WPM", whole(profile.TargetWpm)),
                new StatRow("Average WPM", whole(profile.AverageWpm)),
            };

            if (charsPerWord is double cpw)
                rows.Add(new StatRow("Chars/word", cpw.ToString("0.0", CultureInfo.InvariantCulture)));
        }

        private static string whole(double wpm) => wpm.ToString("0", CultureInfo.InvariantCulture);

        /// <summary>
        /// One bar per curve point, each a share of the plot's height with the tallest at the top.
        /// The FIRST highest bar is the lit peak (ties go to the earlier moment, as on the website); a
        /// point no window starts in is a flat stub on the baseline, never a small value.
        /// </summary>
        private void drawBars(IReadOnlyList<double> curve, float peak)
        {
            int count = curve.Count;
            int peakIndex = -1;

            for (int i = 0; i < count && peakIndex < 0; i++)
            {
                if ((float)curve[i] >= peak)
                    peakIndex = i;
            }

            var bars = new Drawable[count];

            for (int i = 0; i < count; i++)
            {
                double value = curve[i];
                bool empty = value <= 0;

                bars[i] = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    RelativePositionAxes = Axes.X,
                    X = (float)i / count,
                    Width = 1f / count,
                    Padding = new MarginPadding { Horizontal = 0.5f },
                    Child = new Box
                    {
                        Anchor = Anchor.BottomLeft,
                        Origin = Anchor.BottomLeft,
                        RelativeSizeAxes = empty ? Axes.X : Axes.Both,
                        Height = empty ? 2 : System.Math.Max(min_visible_fraction, (float)value / peak),
                        Colour = empty ? colourProvider.Content2.Opacity(0.25f) : bar_colour,
                        Alpha = empty || i == peakIndex ? 1 : 0.72f,
                    },
                };
            }

            graph.Children = bars;
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            if (Beatmap != null)
            {
                Beatmap.HitObjectAdded -= onChanged;
                Beatmap.HitObjectRemoved -= onChanged;
                Beatmap.HitObjectUpdated -= onChanged;
                Beatmap.BeatmapReprocessed -= refreshSoon;
            }
        }

        /// <summary>One "label ... value" row, the website's stat row.</summary>
        private partial class StatRow : CompositeDrawable
        {
            public StatRow(string label, string value)
            {
                RelativeSizeAxes = Axes.X;
                AutoSizeAxes = Axes.Y;

                InternalChildren = new Drawable[]
                {
                    new OsuSpriteText
                    {
                        Text = label,
                        Font = OsuFont.Default.With(size: 16),
                        Alpha = 0.6f,
                    },
                    new OsuSpriteText
                    {
                        Text = value,
                        Anchor = Anchor.TopRight,
                        Origin = Anchor.TopRight,
                        Font = OsuFont.Default.With(size: 16, weight: FontWeight.Bold),
                    },
                };
            }
        }
    }
}
