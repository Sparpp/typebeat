// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Bindables;

namespace typebeat.Game.Configuration
{
    /// <summary>
    /// A config whose settings can be found by their lookup member's NAME, the same name the config
    /// persists them under (game.ini keys, ruleset setting rows). Gameplay presets (backlog 484) address
    /// settings this way, so a preset code names a setting exactly as stably as the stored setting does.
    /// </summary>
    public interface INamedSettingSource
    {
        /// <summary>The names of every setting this config holds.</summary>
        IEnumerable<string> SettingNames { get; }

        /// <summary>
        /// A bound copy of the setting stored under <paramref name="name"/> (the lookup member's exact
        /// name), or null when there is none.
        /// </summary>
        IBindable? FindSetting(string name);
    }

    /// <summary>The one implementation of <see cref="INamedSettingSource"/> over a config's store.</summary>
    public static class NamedSettings
    {
        public static IEnumerable<string> NamesIn<TLookup>(IDictionary<TLookup, IBindable> store)
            where TLookup : struct, Enum
            => store.Keys.Select(k => k.ToString()).ToArray();

        public static IBindable? Find<TLookup>(IDictionary<TLookup, IBindable> store, string name)
            where TLookup : struct, Enum
        {
            // Not Enum.TryParse alone: it also takes "12" or "A, B", which name no setting.
            if (Array.IndexOf(Enum.GetNames<TLookup>(), name) < 0)
                return null;

            return store.TryGetValue(Enum.Parse<TLookup>(name), out var bindable) ? bindable.GetBoundCopy() : null;
        }
    }
}
