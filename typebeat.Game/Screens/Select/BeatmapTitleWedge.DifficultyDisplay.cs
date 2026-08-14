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

                    updateDifficultyStatistics();
                    applyCountStatistics();

                    if (m.NewValue.Any())
                    {
                        settingChangeTracker = new ModSettingChangeTracker(m.NewValue);
                        settingChangeTracker.SettingChanged += _ =>
                        {
                            updateDifficultyStatistics();
                            applyCountStatistics();
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

                Task.Run(() =>
                {
                    // This can take time as it is a synchronous task.
                    // TODO: We're calling `GetPlayableBeatmap` multiple times every map load at song select.
                    var playableBeatmap = beatmap.Value.GetPlayableBeatmap(ruleset.Value);
                    var statistics = playableBeatmap.GetStatistics().ToList();

                    Schedule(() =>
                    {
                        if (cancellationToken.IsCancellationRequested)
                            return;

                        countStatistics = statistics;
                        countStatisticsDisplay.FadeIn(200, Easing.OutQuint);
                        applyCountStatistics();
                    });
                }, cancellationToken);
            }

            // Re-render the cached count statistics at the currently-selected clock rate. Cheap enough
            // to run on every mod change (no beatmap reload); rate-independent stats pass through.
            private void applyCountStatistics()
            {
                double rate = 1;

                foreach (var mod in mods.Value.OfType<IApplicableToRate>())
                    rate = mod.ApplyToRate(0, rate);

                countStatisticsDisplay.Statistics = countStatistics.Select(s =>
                {
                    (string content, float? bar) = s.RateAdjusted != null ? s.RateAdjusted(rate) : (s.Content, s.BarDisplayLength);
                    return new StatisticDifficulty.Data(s.Name, bar ?? 0, bar ?? 0, 1, content);
                }).ToList();
            }

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
