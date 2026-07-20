// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using osu.Framework.Localisation;
using typebeat.Game.Database;
using typebeat.Game.Input.Bindings;
using Realms;

namespace typebeat.Game.Overlays.Settings.Sections.Input
{
    public partial class GlobalKeyBindingsSubsection : KeyBindingsSubsection
    {
        protected override LocalisableString Header { get; }

        private readonly GlobalActionCategory category;

        // When non-null, only these actions are shown/rebindable in this subsection. The category's
        // other actions stay registered and bound as normal — this filters the settings list only,
        // so we can hide actions that are inert in a stripped ruleset without unbinding their keys.
        private readonly HashSet<GlobalAction>? allowedActions;

        public GlobalKeyBindingsSubsection(LocalisableString header, GlobalActionCategory category, IEnumerable<GlobalAction>? allowedActions = null)
        {
            Header = header;
            this.category = category;
            this.allowedActions = allowedActions?.ToHashSet();

            var defaults = GlobalActionContainer.GetDefaultBindingsFor(category);

            if (this.allowedActions != null)
                defaults = defaults.Where(b => b.Action is GlobalAction a && this.allowedActions.Contains(a));

            Defaults = defaults;
        }

        protected override IEnumerable<RealmKeyBinding> GetKeyBindings(Realm realm)
        {
            var bindings = realm.All<RealmKeyBinding>()
                                .Where(b => b.RulesetName == null && b.Variant == null)
                                .AsEnumerable().Detach();

            var actionsInSection = GlobalActionContainer.GetGlobalActionsFor(category)
                                                        .Where(a => allowedActions == null || allowedActions.Contains(a))
                                                        .Cast<int>().ToHashSet();
            return bindings.Where(kb => actionsInSection.Contains(kb.ActionInt));
        }
    }
}
