// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Localisation;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Online;
using typebeat.Game.Online.Rooms;

namespace typebeat.Game.Screens.OnlinePlay.Components
{
    public abstract partial class ReadyButton : RoundedButton
    {
        public new readonly BindableBool Enabled = new BindableBool();

        private readonly IBindable<BeatmapAvailability> availability = new Bindable<BeatmapAvailability>();

        [BackgroundDependencyLoader]
        private void load(OnlinePlayBeatmapAvailabilityTracker beatmapTracker)
        {
            availability.BindTo(beatmapTracker.Availability);
            availability.BindValueChanged(_ => updateState());

            Enabled.BindValueChanged(_ => updateState(), true);
        }

        private void updateState() =>
            base.Enabled.Value = availability.Value.State == DownloadState.LocallyAvailable && Enabled.Value;

        public override LocalisableString TooltipText
        {
            get
            {
                if (base.Enabled.Value)
                    return string.Empty;

                if (availability.Value.State != DownloadState.LocallyAvailable)
                    return "Beatmap not downloaded";

                return string.Empty;
            }
        }
    }
}
