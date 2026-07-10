// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using osu.Framework.Bindables;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Localisation;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Screens.OnlinePlay;

namespace typebeat.Game.Overlays.Mods
{
    public partial class SelectAllModsButton : ShearedButton
    {
        private readonly Bindable<IReadOnlyList<Mod>> selectedMods = new Bindable<IReadOnlyList<Mod>>();
        private readonly Bindable<Dictionary<ModType, IReadOnlyList<ModState>>> availableMods = new Bindable<Dictionary<ModType, IReadOnlyList<ModState>>>();
        private readonly Bindable<string> searchTerm = new Bindable<string>();

        public SelectAllModsButton(FreeModSelectOverlay modSelectOverlay)
        {
            Width = ModSelectOverlay.BUTTON_WIDTH;

            Text = CommonStrings.SelectAll;
            Action = modSelectOverlay.SelectAll;

            selectedMods.BindTo(modSelectOverlay.SelectedMods);
            availableMods.BindTo(modSelectOverlay.AvailableMods);
            searchTerm.BindTo(modSelectOverlay.SearchTextBox.Current);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            selectedMods.BindValueChanged(_ => Scheduler.AddOnce(updateEnabledState));
            availableMods.BindValueChanged(_ => Scheduler.AddOnce(updateEnabledState));
            searchTerm.BindValueChanged(_ => Scheduler.AddOnce(updateEnabledState));
            updateEnabledState();
        }

        private void updateEnabledState()
        {
            Enabled.Value = availableMods.Value
                                         .SelectMany(pair => pair.Value)
                                         .Where(modState => modState.ValidForSelection.Value)
                                         .Any(modState => !modState.Active.Value && modState.Visible);
        }
    }
}
