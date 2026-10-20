// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Resources;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Localisation;

namespace typebeat.Game.Localisation
{
    public class ResourceManagerLocalisationStore : ILocalisationStore
    {
        private readonly Dictionary<string, string?> lookupCache = new Dictionary<string, string?>();
        private readonly Dictionary<string, ResourceManager> resourceManagers = new Dictionary<string, ResourceManager>();

        public ResourceManagerLocalisationStore(string cultureCode)
        {
            EffectiveCulture = new CultureInfo(cultureCode);
        }

        public void Dispose()
        {
        }

        public string? Get(string lookup)
        {
            lock (lookupCache)
            {
                if (lookupCache.TryGetValue(lookup, out string? cached))
                    return cached;
            }

            string? result = getInternal(lookup);

            lock (lookupCache)
            {
                // It's important to cache both lookup successes and failures here.
                // The strings cannot really change under the game, so there's no risk of having a lookup fail now but succeed at some point later,
                // and lookup failures are *more* costly than successes as they incur a scan of *all* strings in resources.
                lookupCache[lookup] = result;
            }

            return result;
        }

        private string? getInternal(string lookup)
        {
            string[] split = lookup.Split(':');

            if (split.Length < 2)
                return null;

            string ns = split[0];
            string key = split[1];

            lock (resourceManagers)
            {
                if (!resourceManagers.TryGetValue(ns, out var manager))
                {
                    var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();

                    // Traverse backwards through periods in the namespace to find a matching assembly.
                    string assemblyName = ns;

                    while (!string.IsNullOrEmpty(assemblyName))
                    {
                        var matchingAssembly = loadedAssemblies.FirstOrDefault(asm => asm.GetName().Name == assemblyName);

                        if (matchingAssembly != null)
                        {
                            resourceManagers[ns] = manager = new ResourceManager(ns, matchingAssembly);
                            break;
                        }

                        int lastIndex = Math.Max(0, assemblyName.LastIndexOf('.'));
                        assemblyName = assemblyName.Substring(0, lastIndex);
                    }
                }

                if (manager == null)
                    return null;

                // When using the English culture, prefer the fallbacks rather than osu-resources baked strings.
                // They are guaranteed to be up-to-date, and is also what a developer expects to see when making changes to `xxxStrings.cs` files.
                if (EffectiveCulture.Name == @"en")
                    return null;

                try
                {
                    return Rebrand(manager.GetString(key, EffectiveCulture));
                }
                catch (MissingManifestResourceException)
                {
                    // in the case the manifest is missing, it is likely that the user is adding code-first implementations of new localisation namespaces.
                    // it's fine to ignore this as localisation will fallback to default values.
                    return null;
                }
            }
        }

        /// <summary>
        /// The upstream brand as it appears in the translated strings.
        /// </summary>
        public const string UPSTREAM_BRAND = @"osu!";

        /// <summary>
        /// Our brand, the same word the English fallbacks in this folder were renamed to.
        /// </summary>
        public const string BRAND = @"type!beat";

        /// <summary>
        /// Every non-English string comes from the satellite assemblies of the resources package,
        /// which are upstream's translations and still say "osu!". Only the English fallbacks in the
        /// <c>*Strings.cs</c> files were renamed, so a translated UI would otherwise carry the upstream
        /// brand on hundreds of strings. The rewrite happens here, once, where translations are read.
        /// <para>
        /// Every occurrence is rewritten, compounds included (osu!stable, osu!direct, osu!lazer,
        /// osu!supporter, osu!store): the English fallbacks keep none of them, they were renamed the
        /// same way ("type!beatstable", "type!beatsupporter"), so a translation now reads exactly as the
        /// English it translates. The match is case-insensitive because a few translations capitalise
        /// the brand at the start of a sentence ("Osu!"), and it ignores what follows, because many
        /// languages inflect the brand in place ("osu!n", "osu!-Installation") or run it straight into
        /// the next word (CJK), and a "standing alone" rule would leave those saying osu!.
        /// </para>
        /// </summary>
        public static string? Rebrand(string? translated)
        {
            if (string.IsNullOrEmpty(translated))
                return translated;

            return translated.Replace(UPSTREAM_BRAND, BRAND, StringComparison.OrdinalIgnoreCase);
        }

        public Task<string?> GetAsync(string lookup, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Get(lookup));
        }

        public Stream GetStream(string name)
        {
            throw new NotImplementedException();
        }

        public IEnumerable<string> GetAvailableResources()
        {
            throw new NotImplementedException();
        }

        public CultureInfo EffectiveCulture { get; }
    }
}
