// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
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

        /// <summary>
        /// The rating the LIVE pp counter prices at. Unlike every finished surface it does not
        /// require a ranked map: a run on a local, pending or otherwise unranked map still shows
        /// what it would be worth, so a mapper testing their own map or a player on a set awaiting
        /// nomination sees a number rather than a dash. The unranked-mod and custom-rate gates
        /// stay, since those make the play worthless wherever it is set. The results screen keeps
        /// the map gate (<see cref="PerformancePointsDisplay.ForScore"/>), so the dash there is
        /// still the honest reading of what was earned.
        /// </summary>
        public static double? StarRatingFor(IBeatmap? playableBeatmap, IReadOnlyList<Mod>? mods)
            => PerformancePointsDisplay.StarRatingFor(playableBeatmap, mods, requireRankedMap: false);

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
            },
        };

    }
}
