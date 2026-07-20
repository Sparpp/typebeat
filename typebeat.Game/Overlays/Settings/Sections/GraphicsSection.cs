// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Graphics;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using typebeat.Game.Graphics;
using typebeat.Game.Localisation;
using typebeat.Game.Overlays.Settings.Sections.Graphics;

namespace typebeat.Game.Overlays.Settings.Sections
{
    public partial class GraphicsSection : SettingsSection
    {
        public override LocalisableString Header => GraphicsSettingsStrings.GraphicsSectionHeader;

        public override Drawable CreateIcon() => new SpriteIcon
        {
            Icon = OsuIcon.Graphics
        };

        public GraphicsSection()
        {
            Children = new Drawable[]
            {
                new LayoutSettings(),
                new RendererSettings(),
                new VideoSettings(),
                new ScreenshotSettings(),
            };
        }
    }
}
