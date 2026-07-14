// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System.Collections.Generic;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets;
using typebeat.Game.Rulesets.Mods;

namespace typebeat.Game.Screens
{
    public class OsuScreenDependencies : DependencyContainer
    {
        public Bindable<WorkingBeatmap> Beatmap { get; }

        public Bindable<RulesetInfo> Ruleset { get; }

        public Bindable<IReadOnlyList<Mod>> Mods { get; }

        public OsuScreenDependencies(bool requireLease, IReadOnlyDependencyContainer parent)
            : base(parent)
        {
            if (requireLease)
            {
                Beatmap = parent.Get<LeasedBindable<WorkingBeatmap>>()?.GetBoundCopy();

                if (Beatmap == null)
                {
                    Cache(Beatmap = parent.Get<Bindable<WorkingBeatmap>>().BeginLease(false));
                    CacheAs(Beatmap);
                }

                Ruleset = parent.Get<LeasedBindable<RulesetInfo>>()?.GetBoundCopy();

                if (Ruleset == null)
                {
                    Cache(Ruleset = parent.Get<Bindable<RulesetInfo>>().BeginLease(true));
                    CacheAs(Ruleset);
                }

                Mods = parent.Get<LeasedBindable<IReadOnlyList<Mod>>>()?.GetBoundCopy();

                if (Mods == null)
                {
                    Cache(Mods = parent.Get<Bindable<IReadOnlyList<Mod>>>().BeginLease(true));
                    CacheAs(Mods);
                }
            }
            else
            {
                Beatmap = (parent.Get<LeasedBindable<WorkingBeatmap>>() ?? parent.Get<Bindable<WorkingBeatmap>>()).GetBoundCopy();
                Ruleset = (parent.Get<LeasedBindable<RulesetInfo>>() ?? parent.Get<Bindable<RulesetInfo>>()).GetBoundCopy();
                Mods = (parent.Get<LeasedBindable<IReadOnlyList<Mod>>>() ?? parent.Get<Bindable<IReadOnlyList<Mod>>>()).GetBoundCopy();
            }
        }
    }
}
