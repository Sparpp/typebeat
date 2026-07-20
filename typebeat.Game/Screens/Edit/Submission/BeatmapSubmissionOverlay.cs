// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Bindables;
using typebeat.Game.Beatmaps;
using typebeat.Game.Localisation;
using typebeat.Game.Overlays;

namespace typebeat.Game.Screens.Edit.Submission
{
    public partial class BeatmapSubmissionOverlay : WizardOverlay
    {
        public BeatmapSubmissionOverlay()
            : base(OverlayColourScheme.Aquamarine)
        {
        }

        [BackgroundDependencyLoader]
        private void load(IBindable<WorkingBeatmap> beatmap)
        {
            // lazer also shows a frequently-asked-questions step here; its content is all
            // osu!-specific wiki/forum links, so it is intentionally not ported.
            if (beatmap.Value.BeatmapSetInfo.OnlineID <= 0)
                AddStep<ScreenContentPermissions>();

            AddStep<ScreenSubmissionSettings>();

            Header.Title = BeatmapSubmissionStrings.BeatmapSubmissionTitle;
            Header.Description = BeatmapSubmissionStrings.BeatmapSubmissionDescription;
        }
    }
}
