// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using typebeat.Game.Resources.Localisation.Web;

namespace typebeat.Game.Beatmaps.Drawables.Cards
{
    public partial class StoryboardIconPill : IconPill
    {
        public StoryboardIconPill()
            : base(FontAwesome.Solid.Image)
        {
        }

        public override LocalisableString TooltipText => BeatmapsetsStrings.ShowInfoStoryboard;
    }
}
