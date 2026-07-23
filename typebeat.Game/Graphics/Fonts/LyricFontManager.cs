// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using osu.Framework.IO.Stores;
using osu.Framework.Logging;
using osu.Framework.Platform;
using SixLabors.Fonts;

namespace typebeat.Game.Graphics.Fonts
{
    /// <summary>
    /// Owns the accessibility fonts for the gameplay typing surface: the bundled OpenDyslexic face
    /// and any face the player picks from their installed system fonts. Both are rasterised at
    /// runtime through <see cref="RuntimeFontGlyphStore"/> and registered into the game's shared
    /// <see cref="FontStore"/>, so a <see cref="osu.Framework.Graphics.Sprites.FontUsage"/> that
    /// names the family resolves like any built-in font.
    /// </summary>
    /// <remarks>
    /// Registration is lazy and idempotent: a family is only rasterised/registered the first time it
    /// is requested (enumerating and loading every installed font up front would bloat every glyph
    /// lookup, since <see cref="FontStore"/> scans its stores linearly). Anything that fails to load
    /// is remembered as unavailable and the caller falls back to the game's default font.
    /// </remarks>
    public class LyricFontManager
    {
        /// <summary>Family name used to select the bundled OpenDyslexic face.</summary>
        public const string OPEN_DYSLEXIC = "OpenDyslexic";

        /// <summary>Drop-in location (relative to the game data dir) an OpenDyslexic .otf/.ttf may be placed at,
        /// used when the bundled copy is absent.</summary>
        public const string OPEN_DYSLEXIC_DROP_IN = "Fonts/OpenDyslexic-Regular.otf";

        private readonly FontStore? fonts;
        private readonly Storage? storage;

        private readonly object sync = new object();

        // family name -> whether it is registered and usable. Absent = not yet attempted.
        private readonly Dictionary<string, bool> registered = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private readonly FontCollection openDyslexicCollection = new FontCollection();
        private FontFamily? openDyslexicFamily;
        private bool openDyslexicProbed;

        private IReadOnlyList<string>? systemFamilyCache;

        public LyricFontManager(FontStore? fonts, Storage? storage)
        {
            this.fonts = fonts;
            this.storage = storage;
        }

        /// <summary>Whether the bundled/drop-in OpenDyslexic face could be loaded on this machine.</summary>
        public bool IsOpenDyslexicAvailable
        {
            get
            {
                lock (sync)
                    return probeOpenDyslexic() != null;
            }
        }

        /// <summary>
        /// The installed system font families, de-duplicated and alphabetically sorted. Empty if the
        /// platform font enumeration is unavailable.
        /// </summary>
        public IReadOnlyList<string> GetSystemFontFamilies()
        {
            lock (sync)
            {
                if (systemFamilyCache != null)
                    return systemFamilyCache;

                try
                {
                    systemFamilyCache = SystemFonts.Families
                                                   .Select(f => f.Name)
                                                   .Where(n => !string.IsNullOrWhiteSpace(n))
                                                   .Distinct(StringComparer.OrdinalIgnoreCase)
                                                   .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                                                   .ToArray();
                }
                catch (Exception e)
                {
                    Logger.Error(e, "Could not enumerate system fonts for the lyric font picker.");
                    systemFamilyCache = Array.Empty<string>();
                }

                return systemFamilyCache;
            }
        }

        /// <summary>
        /// Ensures the given family is rasterised and registered into the game font store. Returns
        /// whether the family is usable; a false result means the caller should fall back to the
        /// default font. The empty string / default sentinel resolves to the built-in font and is
        /// reported as unavailable so callers keep their default.
        /// </summary>
        public bool EnsureRegistered(string? family)
        {
            if (string.IsNullOrWhiteSpace(family))
                return false;

            lock (sync)
            {
                if (registered.TryGetValue(family, out bool already))
                    return already;

                bool ok;

                try
                {
                    FontFamily? resolved = family.Equals(OPEN_DYSLEXIC, StringComparison.OrdinalIgnoreCase)
                        ? probeOpenDyslexic()
                        : resolveSystemFamily(family);

                    if (resolved == null || fonts == null)
                        ok = false;
                    else
                    {
                        var store = new RuntimeFontGlyphStore(resolved.Value, family);
                        fonts.AddTextureSource(store);
                        ok = true;
                    }
                }
                catch (Exception e)
                {
                    Logger.Error(e, $"Failed to load lyric font '{family}'; falling back to the default font.");
                    ok = false;
                }

                registered[family] = ok;
                return ok;
            }
        }

        private FontFamily? resolveSystemFamily(string family)
            => SystemFonts.TryGet(family, out var f) ? f : null;

        private FontFamily? probeOpenDyslexic()
        {
            if (openDyslexicProbed)
                return openDyslexicFamily;

            openDyslexicProbed = true;

            // Prefer the copy bundled with the game assembly; fall back to a user-supplied drop-in
            // in the game data dir. Either lets OpenDyslexic work fully offline.
            try
            {
                using var embedded = openEmbeddedOpenDyslexic();

                if (embedded != null)
                    openDyslexicFamily = openDyslexicCollection.Add(embedded, CultureInfo.InvariantCulture);
                else if (storage != null && storage.Exists(OPEN_DYSLEXIC_DROP_IN))
                {
                    using var dropIn = storage.GetStream(OPEN_DYSLEXIC_DROP_IN);
                    if (dropIn != null)
                        openDyslexicFamily = openDyslexicCollection.Add(dropIn, CultureInfo.InvariantCulture);
                }
            }
            catch (Exception e)
            {
                Logger.Error(e, "Could not load the OpenDyslexic font.");
                openDyslexicFamily = null;
            }

            return openDyslexicFamily;
        }

        private static Stream? openEmbeddedOpenDyslexic()
        {
            var asm = typeof(LyricFontManager).Assembly;
            string? name = asm.GetManifestResourceNames()
                              .FirstOrDefault(n => n.EndsWith("OpenDyslexic-Regular.otf", StringComparison.OrdinalIgnoreCase));

            return name == null ? null : asm.GetManifestResourceStream(name);
        }
    }
}
