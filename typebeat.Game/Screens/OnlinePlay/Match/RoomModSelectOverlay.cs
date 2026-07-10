// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using typebeat.Game.Online.Rooms;
using typebeat.Game.Overlays;
using typebeat.Game.Overlays.Mods;
using typebeat.Game.Rulesets;
using typebeat.Game.Rulesets.Mods;

namespace typebeat.Game.Screens.OnlinePlay.Match
{
    public partial class RoomModSelectOverlay : UserModSelectOverlay
    {
        public Bindable<PlaylistItem?> SelectedItem { get; } = new Bindable<PlaylistItem?>();

        [Resolved]
        private RulesetStore rulesets { get; set; } = null!;

        private readonly List<Mod> roomRequiredMods = new List<Mod>();

        public RoomModSelectOverlay()
            : base(OverlayColourScheme.Plum)
        {
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            SelectedItem.BindValueChanged(v =>
            {
                roomRequiredMods.Clear();

                if (v.NewValue is PlaylistItem item)
                {
                    var rulesetInstance = rulesets.GetRuleset(item.RulesetID)?.CreateInstance();
                    Debug.Assert(rulesetInstance != null);
                    roomRequiredMods.AddRange(item.RequiredMods.Select(m => m.ToMod(rulesetInstance)));
                }

                ActiveMods.Value = ComputeActiveMods();
            }, true);
        }

        protected override IReadOnlyList<Mod> ComputeActiveMods() => roomRequiredMods.Concat(base.ComputeActiveMods()).ToList();
    }
}
