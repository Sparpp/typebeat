// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Skinning;
using osuTK;

namespace typebeat.Game.Rulesets.TypeBeat.UI
{
    /// <summary>
    /// Skin layout layer for the typing readouts. Each counter is a separate serialisable drawable,
    /// so the skin editor can move and scale it without changing its neighbours. It stays inside
    /// the playfield's lyric clock container, alongside the engine ticker and lyric stage.
    /// </summary>
    public partial class TypeBeatHudOverlay : SkinnableContainer
    {
        public const string INELIGIBLE_TEXT = PerformancePointsDisplay.INELIGIBLE_TEXT;

        public TypeBeatHudOverlay(RulesetInfo? ruleset = null)
            : base(new GlobalSkinnableContainerLookup(GlobalSkinnableContainers.TypeBeatCounters, ruleset), createDefaultComponents)
        {
            RelativeSizeAxes = Axes.Both;
        }

        public static double? StarRatingFor(IBeatmap? playableBeatmap, IReadOnlyList<Mod>? mods)
            => PerformancePointsDisplay.StarRatingFor(playableBeatmap, mods);

        public bool SyncReadoutVisible => Components.OfType<TypeBeatSyncCounter>().FirstOrDefault()?.Alpha > 0;

        public string SyncReadoutText => Components.OfType<TypeBeatSyncCounter>().FirstOrDefault()?.DisplayedText ?? string.Empty;

        private static Container createDefaultComponents() => new Container
        {
            RelativeSizeAxes = Axes.Both,
            Children = new Drawable[]
            {
                new TypeBeatWpmCounter
                {
                    Anchor = Anchor.TopCentre,
                    Origin = Anchor.TopCentre,
                    Position = new Vector2(-58, 24),
                },

                new TypeBeatPpCounter
                {
                    Anchor = Anchor.TopCentre,
                    Origin = Anchor.TopCentre,
                    Position = new Vector2(58, 24),
                },
                new TypeBeatSyncCounter
                {
                    Anchor = Anchor.TopCentre,
                    Origin = Anchor.TopCentre,
                    Position = new Vector2(168, 24),
                },
                new TypeBeatRateCounter
                {
                    Anchor = Anchor.TopCentre,
                    Origin = Anchor.TopCentre,
                    Position = new Vector2(278, 24),
                },
            },
        };

    }
}
