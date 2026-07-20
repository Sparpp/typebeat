// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using typebeat.Game.Input;
using typebeat.Game.Input.Bindings;
using typebeat.Game.Localisation;

namespace typebeat.Game.Overlays.OSD
{
    public partial class SpeedChangeToast : Toast
    {
        public SpeedChangeToast(double newSpeed)
            : base(ModSelectOverlayStrings.ModCustomisation, ToastStrings.SpeedChangedTo(newSpeed))
        {
        }

        [BackgroundDependencyLoader]
        private void load(RealmKeyBindingStore keyBindingStore)
        {
            ExtraText = keyBindingStore.GetBindingsStringFor(GlobalAction.IncreaseModSpeed) + " / " + keyBindingStore.GetBindingsStringFor(GlobalAction.DecreaseModSpeed);
        }
    }
}
