// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.Drawables;
using typebeat.Game.Configuration;
using typebeat.Game.Graphics;
using typebeat.Game.Graphics.Containers;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Localisation;
using typebeat.Game.Online;
using typebeat.Game.Online.Chat;
using typebeat.Game.Overlays;
using typebeat.Game.Rulesets;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Utils;
using osuTK.Graphics;

namespace typebeat.Game.Screens.Select
{
    public partial class BeatmapTitleWedge
    {
        public partial class DifficultyDisplay : CompositeDrawable
        {
            private const float border_weight = 2;

            [Resolved]
            private IBindable<WorkingBeatmap> beatmap { get; set; } = null!;

            [Resolved]
            private IBindable<RulesetInfo> ruleset { get; set; } = null!;

            [Resolved]
            private IBindable<IReadOnlyList<Mod>> mods { get; set; } = null!;

            private ModSettingChangeTracker? settingChangeTracker;

            [Resolved]
            private BeatmapDifficultyCache difficultyCache { get; set; } = null!;

            private StarRatingDisplay starRatingDisplay = null!;
            private FillFlowContainer nameLine = null!;
            private OsuSpriteText difficultyText = null!;
            private OsuSpriteText mappedByText = null!;
            private OsuHoverContainer mapperLink = null!;
            private OsuSpriteText mapperText = null!;

            private GridContainer ratingAndNameContainer = null!;
            private DifficultyStatisticsDisplay countStatisticsDisplay = null!;
            private DifficultyStatisticsDisplay difficultyStatisticsDisplay = null!;

            private CancellationTokenSource? cancellationSource;

            public DifficultyDisplay()
            {
                RelativeSizeAxes = Axes.X;
                AutoSizeAxes = Axes.Y;
            }

            [BackgroundDependencyLoader]
            private void load(OverlayColourProvider colourProvider)
            {
                Masking = true;
                CornerRadius = 10;
                Shear = OsuGame.SHEAR;

                InternalChildren = new Drawable[]
                {
                    new WedgeBackground(),
                    new FillFlowContainer
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Direction = FillDirection.Vertical,
                        Children = new Drawable[]
                        {
                            new ShearAligningWrapper(ratingAndNameContainer = new GridContainer
                            {
                                Shear = -OsuGame.SHEAR,
                                AlwaysPresent = true,
                                RelativeSizeAxes = Axes.X,
                                Height = 20,
                                Margin = new MarginPadding { Vertical = 5f },
                                Padding = new MarginPadding { Left = SongSelect.WEDGE_CONTENT_MARGIN },
                                RowDimensions = new[] { new Dimension(GridSizeMode.AutoSize) },
                                ColumnDimensions = new[]
                                {
                                    new Dimension(GridSizeMode.AutoSize),
                                    new Dimension(GridSizeMode.Absolute, 6),
                                    new Dimension(),
                                },
                                Content = new[]
                                {
                                    new[]
                                    {
                                        starRatingDisplay = new StarRatingDisplay(default, animated: true)
                                        {
                                            Anchor = Anchor.CentreLeft,
                                            Origin = Anchor.CentreLeft,
                                        },
                                        Empty(),
                                        nameLine = new FillFlowContainer
                                        {
                                            Anchor = Anchor.CentreLeft,
                                            Origin = Anchor.CentreLeft,
                                            RelativeSizeAxes = Axes.X,
                                            AutoSizeAxes = Axes.Y,
                                            Direction = FillDirection.Horizontal,
                                            Margin = new MarginPadding { Bottom = 2f },
                                            Children = new Drawable[]
                                            {
                                                difficultyText = new TruncatingSpriteText
                                                {
                                                    Anchor = Anchor.BottomLeft,
                                                    Origin = Anchor.BottomLeft,
                                                    Font = OsuFont.Style.Body.With(weight: FontWeight.SemiBold),
                                                },
                                                mappedByText = new OsuSpriteText
                                                {
                                                    Anchor = Anchor.BottomLeft,
                                                    Origin = Anchor.BottomLeft,
                                                    Text = " mapped by ",
                                                    Font = OsuFont.Style.Body,
                                                },
                                                mapperLink = new MapperLinkContainer
                                                {
                                                    AutoSizeAxes = Axes.Both,
                                                    Anchor = Anchor.BottomLeft,
                                                    Origin = Anchor.BottomLeft,
                                                    Child = mapperText = new TruncatingSpriteText
                                                    {
                                                        Shadow = true,
                                                        Font = OsuFont.Style.Body.With(weight: FontWeight.SemiBold),
                                                    },
                                                },
                                            },
                                        },
                                    }
                                },
                            }),
                            new ShearAligningWrapper(new Container
                            {
                                Shear = -OsuGame.SHEAR,
                                RelativeSizeAxes = Axes.X,
                                Height = 53,
                                Padding = new MarginPadding { Bottom = border_weight, Right = border_weight },
                                Child = new Container
                                {
                                    RelativeSizeAxes = Axes.X,
                                    AutoSizeAxes = Axes.Y,
                                    Masking = true,
                                    CornerRadius = 10 - border_weight,
                                    Shear = OsuGame.SHEAR,
                                    Children = new Drawable[]
                                    {
                                        new Box
                                        {
                                            RelativeSizeAxes = Axes.Both,
                                            Colour = colourProvider.Background5.Opacity(0.8f),
                                        },
                                        new GridContainer
                                        {
                                            RelativeSizeAxes = Axes.X,
                                            AutoSizeAxes = Axes.Y,
                                            Padding = new MarginPadding { Left = SongSelect.WEDGE_CONTENT_MARGIN, Right = 20f, Vertical = 7.5f },
                                            Shear = -OsuGame.SHEAR,
                                            RowDimensions = new[] { new Dimension(GridSizeMode.AutoSize) },
                                            ColumnDimensions = new[]
                                            {
                                                new Dimension(),
                                                new Dimension(GridSizeMode.Absolute, 30),
                                                new Dimension(GridSizeMode.AutoSize),
                                            },
                                            Content = new[]
                                            {
                                                new[]
                                                {
                                                    countStatisticsDisplay = new DifficultyStatisticsDisplay
                                                    {
                                                        RelativeSizeAxes = Axes.X,
                                                    },
                                                    Empty(),
                                                    difficultyStatisticsDisplay = new DifficultyStatisticsDisplay(autoSize: true),
                                                }
                                            },
                                        }
                                    },
                                }
                            }),
                        }
                    },
                };
            }

            protected override void LoadComplete()
            {
                base.LoadComplete();

                // it is not uncommon for the beatmap and the ruleset to change in conjunction during a single update frame.
                // in that process, it is possible for the global bindable triad (beatmap / ruleset / mods) to briefly be partially invalid in combination (e.g. mods invalid for given ruleset).
                // `updateDisplay()` will initiate a difficulty calculation, and if it is allowed to run in that invalid intermediate state, it will loudly fail.
                // therefore, all changes that may initiate a difficulty calculation are debounced until the next frame to ensure the global bindable state is fully consistent -
                // and it's what you'd want to do anyway for performance reasons.
                beatmap.BindValueChanged(_ => Scheduler.AddOnce(updateDisplay));
                ruleset.BindValueChanged(_ => Scheduler.AddOnce(updateDisplay));

                mods.BindValueChanged(m =>
                {
                    settingChangeTracker?.Dispose();

                    // A mod that rewrites the converted beatmap (Literate) moves the count
                    // statistics' OWN numbers (Words / Average WPM / Target WPM / Chars per word),
                    // so those have to be recomputed from a beatmap converted with the new mod
                    // list, exactly as the star rating and pp are priced from a converted map.
                    // updateDisplay does that, and cancels the in-flight computation on its own.
                    // Every other mod either changes the clock (handled by re-applying the cached
                    // statistics' rate) or touches only the live playfield.
                    if (shapesBeatmap(m.OldValue) != shapesBeatmap(m.NewValue))
                        Scheduler.AddOnce(updateDisplay);
                    else
                    {
                        updateDifficultyStatistics();
                        reapplyCountStatistics();
                    }

                    if (m.NewValue.Any())
                    {
                        settingChangeTracker = new ModSettingChangeTracker(m.NewValue);
                        settingChangeTracker.SettingChanged += _ =>
                        {
                            updateDifficultyStatistics();
                            reapplyCountStatistics();
                        };
                    }
                }, true);

                updateDisplay();
            }

            [Resolved]
            private ILinkHandler? linkHandler { get; set; }

            private void updateDisplay()
            {
                cancellationSource?.Cancel();
                cancellationSource = new CancellationTokenSource();

                if (beatmap.IsDefault)
                {
                    ratingAndNameContainer.FadeOut(300, Easing.OutQuint);
                    countStatisticsDisplay.FadeOut(300, Easing.OutQuint);
                }
                else
                {
                    ratingAndNameContainer.FadeIn(300, Easing.OutQuint);
                    difficultyText.Text = beatmap.Value.BeatmapInfo.DifficultyName;
                    mapperLink.Action = () => linkHandler?.HandleLink(new LinkDetails(LinkAction.OpenUserProfile, beatmap.Value.Metadata.Author));
                    mapperText.Text = beatmap.Value.Metadata.Author.Username;
                }

                starRatingDisplay.Current = (Bindable<StarDifficulty>)difficultyCache.GetBindableDifficulty(beatmap.Value.BeatmapInfo, cancellationSource.Token, SongSelect.DIFFICULTY_CALCULATION_DEBOUNCE);

                updateCountStatistics(cancellationSource.Token);
                updateDifficultyStatistics();
            }

            /// <summary>
            /// Whether the given mod list carries a mod that rewrites the converted beatmap.
            /// Literate is the only one this ruleset ships and it carries no setting, so the
            /// presence of a shaping mod is the whole of the state the count statistics depend on.
            /// </summary>
            private static bool shapesBeatmap(IEnumerable<Mod> mods)
                => ModUtils.BeatmapShapingMods(mods).Count > 0;

            // The raw per-beatmap statistics (Words / WPM / CPM), cached so a mod toggle can re-apply
            // the clock rate to the pace stats without reloading the playable beatmap.
            private IReadOnlyList<BeatmapStatistic> countStatistics = Array.Empty<BeatmapStatistic>();

            private void updateCountStatistics(CancellationToken cancellationToken)
            {
                if (beatmap.IsDefault)
                {
                    countStatistics = Array.Empty<BeatmapStatistic>();
                    countStatisticsDisplay.FadeOut(300, Easing.OutQuint);
                    return;
                }

                // Read on the update thread, before the conversion task: the mod list is the one
                // the request was made for, not whatever the bindable holds by the time it runs.
                var conversionMods = ModUtils.BeatmapShapingMods(mods.Value);
                double rate = clockRate();

                Task.Run(() =>
                {
                    // This can take time as it is a synchronous task.
                    // TODO: We're calling `GetPlayableBeatmap` multiple times every map load at song select.
                    var playableBeatmap = beatmap.Value.GetPlayableBeatmap(ruleset.Value, conversionMods);
                    var statistics = playableBeatmap.GetStatistics().ToList();

                    // Rendered HERE rather than on the update thread: a rate-adjustable row is allowed to
                    // be expensive (the target WPM goes back through the difficulty model), and it is
                    // this task's job to keep that off the frame.
                    var rendered = render(statistics, rate);

                    Schedule(() =>
                    {
                        if (cancellationToken.IsCancellationRequested)
                            return;

                        countStatistics = statistics;
                        appliedRate = rate;
                        countStatisticsDisplay.Statistics = rendered;
                        countStatisticsDisplay.FadeIn(200, Easing.OutQuint);
                    });
                }, cancellationToken);
            }

            /// <summary>The clock the selected mods play at: DT/NC 1.5x, HT 0.75x, custom rates as asked.</summary>
            private double clockRate()
            {
                double rate = 1;

                foreach (var mod in mods.Value.OfType<IApplicableToRate>())
                    rate = mod.ApplyToRate(0, rate);

                return rate;
            }

            /// <summary>The rows at that clock: rate-adjustable statistics re-read, the rest as authored.</summary>
            private static List<StatisticDifficulty.Data> render(IReadOnlyList<BeatmapStatistic> statistics, double rate)
                => statistics.Select(s =>
                {
                    (string content, float? bar) = s.RateAdjusted != null ? s.RateAdjusted(rate) : (s.Content, s.BarDisplayLength);
                    return new StatisticDifficulty.Data(s.Name, bar ?? 0, bar ?? 0, 1, content);
                }).ToList();

            /// <summary>
            /// Re-renders the cached statistics at a new clock, OFF the update thread. The rate walk is
            /// only reached when the rate actually changed, and the rows are swapped in whole once they
            /// are ready, so a toggle cannot leave half a row priced at the old clock.
            /// </summary>
            private void reapplyCountStatistics()
            {
                double rate = clockRate();

                if (rate == appliedRate || countStatistics.Count == 0)
                    return;

                appliedRate = rate;

                Task.Run(() =>
                {
                    var rendered = render(countStatistics, rate);

                    Schedule(() =>
                    {
                        if (rate == appliedRate)
                            countStatisticsDisplay.Statistics = rendered;
                    });
                });
            }

            /// <summary>The clock <see cref="countStatisticsDisplay"/> was last rendered at.</summary>
            private double appliedRate = double.NaN;

            private void updateDifficultyStatistics() => Scheduler.AddOnce(() =>
            {
                if (beatmap.IsDefault || ruleset.Value == null)
                {
                    difficultyStatisticsDisplay.Statistics = Array.Empty<StatisticDifficulty.Data>();
                    return;
                }

                Ruleset rulesetInstance = ruleset.Value.CreateInstance();

                var displayAttributes = rulesetInstance.GetBeatmapAttributesForDisplay(beatmap.Value.BeatmapInfo, mods.Value).ToList();
                difficultyStatisticsDisplay.Statistics = displayAttributes.Select(a => new StatisticDifficulty.Data(a)).ToList();
            });

            protected override void Update()
            {
                base.Update();

                difficultyText.MaxWidth = Math.Max(nameLine.DrawWidth - mappedByText.DrawWidth - mapperText.DrawWidth - 20, 0);

                // Use difficulty colour until it gets too dark to be visible against dark backgrounds.
                Color4 col = starRatingDisplay.DisplayedStars.Value >= OsuColour.STAR_DIFFICULTY_DEFINED_COLOUR_CUTOFF ? starRatingDisplay.DisplayedDifficultyTextColour : starRatingDisplay.DisplayedDifficultyColour;

                difficultyText.Colour = col;
                mappedByText.Colour = col;
                countStatisticsDisplay.AccentColour = col;
                difficultyStatisticsDisplay.AccentColour = col;
            }

            private partial class MapperLinkContainer : OsuHoverContainer
            {
                [BackgroundDependencyLoader]
                private void load(OverlayColourProvider? overlayColourProvider, OsuColour colours)
                {
                    TooltipText = ContextMenuStrings.ViewProfile;
                    IdleColour = overlayColourProvider?.Light2 ?? colours.Blue;
                }
            }
        }
    }
}
