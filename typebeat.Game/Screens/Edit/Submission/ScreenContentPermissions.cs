// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Localisation;
using typebeat.Game.Graphics;
using typebeat.Game.Graphics.Containers;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Localisation;
using typebeat.Game.Overlays;

namespace typebeat.Game.Screens.Edit.Submission
{
    [LocalisableDescription(typeof(BeatmapSubmissionStrings), nameof(BeatmapSubmissionStrings.ContentPermissions))]
    public partial class ScreenContentPermissions : WizardScreen
    {
        [BackgroundDependencyLoader]
        private void load(OsuGame? game)
        {
            Content.AddRange(new Drawable[]
            {
                new OsuTextFlowContainer(cp => cp.Font = OsuFont.Default.With(size: CONTENT_FONT_SIZE))
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Anchor = Anchor.TopCentre,
                    Origin = Anchor.TopCentre,
                    Text = BeatmapSubmissionStrings.ContentPermissionsDisclaimer,
                },
                new RoundedButton
                {
                    Anchor = Anchor.TopCentre,
                    Origin = Anchor.TopCentre,
                    Width = 450,
                    Text = BeatmapSubmissionStrings.CheckContentUsageGuidelines,
                    // The type!beat website has no wiki; the DMCA / content policy page carries
                    // the upload rules. Relative paths resolve against the website root.
                    Action = () => game?.OpenUrlExternally(@"/legal/dmca"),
                },
            });
        }

        public override LocalisableString? NextStepText => BeatmapSubmissionStrings.ContentPermissionsAcknowledgement;
    }
}
