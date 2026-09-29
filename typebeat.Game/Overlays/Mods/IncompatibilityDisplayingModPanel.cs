// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Cursor;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Utils;

namespace typebeat.Game.Overlays.Mods
{
    public partial class IncompatibilityDisplayingModPanel : ModPanel, IHasCustomTooltip<Mod>
    {
        private readonly BindableBool incompatible = new BindableBool();

        [Resolved]
        private OverlayColourProvider overlayColourProvider { get; set; } = null!;

        [Resolved]
        private Bindable<IReadOnlyList<Mod>> selectedMods { get; set; } = null!;

        public IncompatibilityDisplayingModPanel(ModState modState)
            : base(modState)
        {
        }

        public IncompatibilityDisplayingModPanel(Mod mod)
            : base(mod)
        {
        }

        protected override void LoadComplete()
        {
            selectedMods.BindValueChanged(_ => updateIncompatibility(), true);
            incompatible.BindValueChanged(_ => Scheduler.AddOnce(UpdateState));
            State.AvailableForBeatmap.BindValueChanged(_ => Scheduler.AddOnce(UpdateState));
            // base call will run `UpdateState()` first time and finish transforms.
            base.LoadComplete();
        }

        private void updateIncompatibility()
        {
            incompatible.Value = selectedMods.Value.Count > 0
                                 && selectedMods.Value.All(selected => selected.GetType() != Mod.GetType())
                                 && !ModUtils.CheckCompatibleSet(selectedMods.Value.Append(Mod));
        }

        /// <summary>
        /// Greyed out: incompatible with the current selection, or unavailable on the current beatmap
        /// (backlog 331). Both read the same, the second one refuses the click as well.
        /// </summary>
        private bool greyed => incompatible.Value || !State.AvailableForBeatmap.Value;

        protected override Colour4 BackgroundColour => greyed ? ColourProvider.Background6 : base.BackgroundColour;
        protected override Colour4 ForegroundColour => greyed ? ColourProvider.Background5 : base.ForegroundColour;

        protected override void UpdateState()
        {
            base.UpdateState();
            SwitchContainer.FadeColour(greyed ? Colour4.Gray : Colour4.White, TRANSITION_DURATION, Easing.OutQuint);
        }

        #region IHasCustomTooltip

        public ITooltip<Mod> GetCustomTooltip() => new IncompatibilityDisplayingTooltip(overlayColourProvider);

        public Mod TooltipContent => Mod;

        #endregion
    }
}
