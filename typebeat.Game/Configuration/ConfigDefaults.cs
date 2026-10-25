// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Bindables;

namespace typebeat.Game.Configuration
{
    /// <summary>
    /// The one implementation of "reset these settings to their defaults", shared by the game's
    /// <see cref="OsuConfigManager"/> and every ruleset's config manager (Maintenance, "Reset all
    /// settings to defaults").
    /// </summary>
    public static class ConfigDefaults
    {
        /// <summary>
        /// Puts every bindable in <paramref name="store"/> back on its default, except the lookups
        /// <paramref name="keep"/> names. Returns how many were reset.
        ///
        /// <para>Two passes, because one setting can disable another (the indicator alignment slider is
        /// disabled while character timing is on), and a disabled bindable refuses a new value. The first
        /// pass resets everything it can, which puts the disabling setting back on its default; the
        /// second retries what was disabled. Anything still disabled after that is disabled BY a
        /// default, so it already holds the value that default forces and is left alone.</para>
        /// </summary>
        public static int Reset<TLookup>(IEnumerable<KeyValuePair<TLookup, IBindable>> store, Func<TLookup, bool> keep)
            where TLookup : struct, Enum
        {
            var pending = store.Where(p => !keep(p.Key)).Select(p => p.Value).ToList();
            int reset = 0;

            for (int pass = 0; pass < 2 && pending.Count > 0; pass++)
            {
                var deferred = new List<IBindable>();

                foreach (var bindable in pending)
                {
                    if (bindable.Disabled)
                    {
                        deferred.Add(bindable);
                        continue;
                    }

                    setDefault(bindable);
                    reset++;
                }

                pending = deferred;
            }

            return reset;
        }

        // Bindable<T>.SetDefault is generic-typed and the store holds the untyped interface, so it is
        // found by name. Every config bindable derives from Bindable<T>, which declares it.
        private static void setDefault(IBindable bindable) =>
            bindable.GetType().GetMethod(nameof(Bindable<int>.SetDefault), Type.EmptyTypes)!.Invoke(bindable, null);
    }
}
