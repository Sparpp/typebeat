// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Localisation;
using typebeat.Game.Graphics;
using typebeat.Game.Online;
using typebeat.Game.Online.Chat;

namespace typebeat.Game.Overlays.Toolbar
{
    /// <summary>
    /// Opens the website's beatmap listing in the browser — the fork's replacement for the
    /// in-game beatmap listing overlay (M3 posture: web content lives on the website).
    /// </summary>
    public partial class ToolbarWebsiteButton : ToolbarButton
    {
        [Resolved]
        private ILinkHandler linkHandler { get; set; } = null!;

        [BackgroundDependencyLoader]
        private void load()
        {
            TooltipMain = new LocalisableString("beatmap listing");
            TooltipSub = new LocalisableString("browse and download maps on the website");
            SetIcon(OsuIcon.Beatmap);

            // A relative path resolves against the configured WebsiteUrl and, being the trusted
            // domain, opens without the external-link warning dialog.
            Action = () => linkHandler.HandleLink(new LinkDetails(LinkAction.External, "/beatmapsets"));
        }
    }
}
