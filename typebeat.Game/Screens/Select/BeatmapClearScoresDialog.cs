// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using typebeat.Game.Beatmaps;
using typebeat.Game.Localisation;
using typebeat.Game.Overlays.Dialog;
using typebeat.Game.Scoring;

namespace typebeat.Game.Screens.Select
{
    public partial class BeatmapClearScoresDialog : DeletionDialog
    {
        [Resolved]
        private ScoreManager scoreManager { get; set; } = null!;

        public BeatmapClearScoresDialog(BeatmapInfo beatmapInfo, Action? onCompletion = null)
        {
            BodyText = DialogStrings.BeatmapClearScoresBodyText(beatmapInfo.GetDisplayTitle());
            DangerousAction = () =>
            {
                Task.Run(() => scoreManager.Delete(beatmapInfo))
                    .ContinueWith(_ => onCompletion?.Invoke());
            };
        }
    }
}
