// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using typebeat.Game.Beatmaps;
using typebeat.Game.Overlays.Dialog;

namespace typebeat.Game.Screens.Select
{
    public partial class BeatmapDeleteDialog : DeletionDialog
    {
        private readonly BeatmapSetInfo beatmapSet;

        public BeatmapDeleteDialog(BeatmapSetInfo beatmapSet)
        {
            this.beatmapSet = beatmapSet;
            BodyText = beatmapSet.Metadata.GetDisplayTitleRomanisable(false);
        }

        [BackgroundDependencyLoader]
        private void load(BeatmapManager beatmapManager)
        {
            DangerousAction = () => beatmapManager.Delete(beatmapSet);
        }
    }
}
