// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.LocalisationExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Localisation;
using typebeat.Game.Beatmaps;
using typebeat.Game.Configuration;
using typebeat.Game.Graphics;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Rulesets;
using osuTK;

namespace typebeat.Game.Screens.Select
{
    public partial class PanelBeatmapSet
    {
        /// <summary>
        /// The set's difficulty spectrum: one dot per difficulty, ordered by and coloured from star rating, beside the
        /// status pill. Ported from upstream osu!lazer's <c>PanelBeatmapSet.SpreadDisplay</c>
        /// (ppy/osu, osu.Game/Screens/Select/PanelBeatmapSet.SpreadDisplay.cs at 2716707e5c637012715c8a12da2553e292470350),
        /// with two deliberate differences:
        /// <list type="bullet">
        /// <item>No ruleset icon in front of the dots. Upstream groups the dots per ruleset and heads each group with
        /// that ruleset's icon; type!beat has one ruleset, so the icon says nothing (and was the osu! circle).</item>
        /// <item>A plain display, not a button. Upstream's click scoped song select to the set, a mode built for osu!'s
        /// converted difficulties that backlog 348 removed along with the scoping machinery; this keeps the dots only.</item>
        /// </list>
        /// Difficulties the current filter hides stay in the row as smaller, half faded dots (upstream's behaviour),
        /// fed by the carousel through <see cref="VisibleBeatmaps"/>.
        /// </summary>
        public partial class SpreadDisplay : CompositeDrawable
        {
            public Bindable<BeatmapSetInfo?> BeatmapSet { get; } = new Bindable<BeatmapSetInfo?>();

            /// <summary>
            /// The set's difficulties that pass the current filter, or <see langword="null"/> when unknown (all treated as visible).
            /// </summary>
            public IBindable<HashSet<BeatmapInfo>?> VisibleBeatmaps { get; } = new Bindable<HashSet<BeatmapInfo>?>();

            /// <summary>
            /// Above this many difficulties in a category (visible or filtered out), that category collapses to a count.
            /// Matches upstream and osu-web's beatmapset panel.
            /// </summary>
            public const int MAX_DIFFICULTIES_BEFORE_COLLAPSING = 12;

            private readonly Bindable<bool> showConvertedBeatmaps = new Bindable<bool>();

            private const double transition_duration = 200;

            [Resolved]
            private IBindable<RulesetInfo> ruleset { get; set; } = null!;

            [Resolved]
            private OsuColour colours { get; set; } = null!;

            private FillFlowContainer flow = null!;

            public SpreadDisplay()
            {
                AutoSizeAxes = Axes.X;
                Height = 14;
            }

            [BackgroundDependencyLoader]
            private void load(OsuConfigManager configManager)
            {
                InternalChild = flow = new FillFlowContainer
                {
                    AutoSizeAxes = Axes.X,
                    RelativeSizeAxes = Axes.Y,
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Direction = FillDirection.Horizontal,
                    Spacing = new Vector2(1),
                    // Upstream's button padding, kept so the dots sit where upstream draws them relative to the pill.
                    Padding = new MarginPadding { Horizontal = 5 },
                };

                configManager.BindWith(OsuSetting.ShowConvertedBeatmaps, showConvertedBeatmaps);
            }

            protected override void LoadComplete()
            {
                base.LoadComplete();

                BeatmapSet.BindValueChanged(_ => updateBeatmapSet());
                VisibleBeatmaps.BindValueChanged(_ => updateBeatmapSet());
                showConvertedBeatmaps.BindValueChanged(_ => updateBeatmapSet(), true);
                FinishTransforms(true);
            }

            private void updateBeatmapSet()
            {
                if (BeatmapSet.Value == null)
                {
                    this.FadeOut(transition_duration, Easing.OutQuint);
                    return;
                }

                flow.Clear();

                var beatmaps = BeatmapSet.Value.Beatmaps
                                         .Where(b => b.AllowGameplayWithRuleset(ruleset.Value, showConvertedBeatmaps.Value))
                                         .OrderBy(b => b.StarRating)
                                         .ToList();
                this.FadeTo(beatmaps.Count > 0 ? 1 : 0, transition_duration, Easing.OutQuint);

                if (beatmaps.Count == 0)
                    return;

                bool showVisible = VisibleBeatmaps.Value == null || VisibleBeatmaps.Value?.Count <= MAX_DIFFICULTIES_BEFORE_COLLAPSING;
                bool showHidden = beatmaps.Count <= MAX_DIFFICULTIES_BEFORE_COLLAPSING;

                // Upstream walks one such pass per ruleset group, each after its ruleset icon; with a single ruleset
                // that is one pass over the whole set and no icon.
                int overflowVisible = 0;
                int overflowHidden = 0;
                bool? lastBeatmapVisible = null;

                foreach (var beatmap in beatmaps)
                {
                    bool visible = VisibleBeatmaps.Value?.Contains(beatmap) != false;

                    if ((visible && showVisible) || (!visible && showHidden))
                    {
                        flow.Add(new Circle
                        {
                            Size = visible ? new Vector2(7, 12) : new Vector2(5, 10),
                            Alpha = visible ? 1 : 0.5f,
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                            Colour = colours.ForStarDifficulty(beatmap.StarRating),
                            Margin = new MarginPadding { Left = lastBeatmapVisible != null && lastBeatmapVisible != visible ? 1 : 0 }
                        });

                        lastBeatmapVisible = visible;
                    }
                    else
                    {
                        if (visible)
                            overflowVisible++;
                        else
                            overflowHidden++;
                    }
                }

                if (overflowVisible > 0)
                {
                    flow.Add(new OsuSpriteText
                    {
                        Anchor = Anchor.CentreLeft,
                        Origin = Anchor.CentreLeft,
                        Font = OsuFont.Style.Caption2,
                        Text = overflowVisible.ToLocalisableString(),
                    });
                }

                if (overflowHidden > 0)
                {
                    flow.Add(new OsuSpriteText
                    {
                        Anchor = Anchor.CentreLeft,
                        Origin = Anchor.CentreLeft,
                        Font = OsuFont.Style.Caption2,
                        Text = LocalisableString.Interpolate($@"+{overflowHidden}"),
                        Alpha = 0.7f,
                    });
                }
            }
        }
    }
}
