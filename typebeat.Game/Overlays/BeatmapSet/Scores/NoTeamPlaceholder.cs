// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Localisation;
using osuTK;

namespace typebeat.Game.Overlays.BeatmapSet.Scores
{
    public partial class NoTeamPlaceholder : Container
    {
        public NoTeamPlaceholder()
        {
            AutoSizeAxes = Axes.Both;
            Child = new FillFlowContainer
            {
                AutoSizeAxes = Axes.Both,
                Direction = FillDirection.Vertical,
                Spacing = new Vector2(0, 20),
                Children = new Drawable[]
                {
                    new OsuSpriteText
                    {
                        Anchor = Anchor.TopCentre,
                        Origin = Anchor.TopCentre,
                        Text = LeaderboardStrings.NoTeam,
                    },
                }
            };
        }
    }
}
