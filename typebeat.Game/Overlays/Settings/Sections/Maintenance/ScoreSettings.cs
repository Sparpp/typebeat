// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Localisation;
using typebeat.Game.Localisation;
using typebeat.Game.Scoring;

namespace typebeat.Game.Overlays.Settings.Sections.Maintenance
{
    public partial class ScoreSettings : SettingsSubsection
    {
        protected override LocalisableString Header => CommonStrings.Scores;

        private SettingsButtonV2 deleteScoresButton = null!;

        [BackgroundDependencyLoader]
        private void load(ScoreManager scores, IDialogOverlay? dialogOverlay)
        {
            Add(deleteScoresButton = new DangerousSettingsButtonV2
            {
                Text = MaintenanceSettingsStrings.DeleteAllScores,
                Action = () =>
                {
                    dialogOverlay?.Push(new MassDeleteConfirmationDialog(() =>
                    {
                        deleteScoresButton.Enabled.Value = false;
                        Task.Run(() => scores.Delete()).ContinueWith(_ => Schedule(() => deleteScoresButton.Enabled.Value = true));
                    }, DeleteConfirmationContentStrings.Scores));
                }
            });
        }
    }
}
