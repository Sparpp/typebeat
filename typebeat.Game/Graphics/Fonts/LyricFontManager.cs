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
using osu.Framework.Text;
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

        // Families added from arbitrary streams (a beatmap set's bundled font file) live here so the
        // parsed data outlives the registration call; SixLabors ties a FontFamily to its collection.
        private readonly FontCollection streamCollection = new FontCollection();

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

        /// <summary>
        /// Ensures a font parsed from an arbitrary stream (a beatmap set's bundled font file) is
        /// rasterised and registered into the game font store under <paramref name="name"/>, the
        /// caller's own lookup key (a per-file name, so two maps bundling different fonts can never
        /// collide). Modeled on the OpenDyslexic drop-in path. Returns whether the font is usable; a
        /// missing or corrupt stream logs and returns false so the caller falls back, never throwing,
        /// because a broken bundled file must not fail the play.
        /// </summary>
        public bool EnsureRegisteredFromStream(string? name, Func<Stream?> openStream)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            lock (sync)
            {
                if (registered.TryGetValue(name, out bool already))
                    return already;

                bool ok;

                try
                {
                    FontFamily? family = null;

                    using (var stream = openStream())
                    {
                        if (stream != null)
                            family = streamCollection.Add(stream, CultureInfo.InvariantCulture);
                    }

                    if (family == null || fonts == null)
                        ok = false;
                    else
                    {
                        var store = new RuntimeFontGlyphStore(family.Value, name);
                        fonts.AddTextureSource(store);
                        ok = true;
                    }
                }
                catch (Exception e)
                {
                    Logger.Error(e, $"Failed to load the bundled lyric font '{name}'; falling back.");
                    ok = false;
                }

                registered[name] = ok;
                return ok;
            }
        }

        #region Script coverage (backlog 331)

        /// <summary>The key prefix a script-coverage fallback face is registered under.</summary>
        public const string COVERAGE_FALLBACK_PREFIX = "LyricCoverage-";

        /// <summary>
        /// Faces tried first when a character has no glyph anywhere in the font store, in order: the
        /// platform faces that each carry a whole script family (Segoe UI's Greek, Cyrillic, Georgian and
        /// Armenian, Nirmala UI's Indic scripts, the CJK and hangul UI faces, the macOS and Linux
        /// equivalents). Every other installed face is tried after them, alphabetically.
        /// </summary>
        public static readonly IReadOnlyList<string> PREFERRED_COVERAGE_FAMILIES = new[]
        {
            "Segoe UI", "Nirmala UI", "Leelawadee UI", "Malgun Gothic", "Yu Gothic UI", "Microsoft YaHei UI", "Microsoft JhengHei UI",
            "Sylfaen", "Ebrima", "Gadugi", "Segoe UI Historic", "Arial Unicode MS",
            "Helvetica Neue", "Hiragino Sans", "Apple SD Gothic Neo", "PingFang SC", "Noto Sans", "Noto Sans CJK JP", "DejaVu Sans", "Arial",
        };

        private readonly Dictionary<string, RuntimeFontGlyphStore> coverageProbes = new Dictionary<string, RuntimeFontGlyphStore>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Makes every character in <paramref name="characters"/> drawable by the lyric stack (the
        /// Polyglot mod plays originals in any script): a character no store in the game's font store
        /// can draw has an installed system face that can registered as a FALLBACK, which the framework
        /// reaches for any glyph the lyric font itself lacks. Returns the characters no installed face
        /// covers either; those draw as the framework's fallback glyph, never as a blank cell.
        /// </summary>
        public IReadOnlyList<char> EnsureCoverage(IEnumerable<char> characters)
        {
            var missing = new List<char>();

            if (fonts == null)
                return missing;

            lock (sync)
            {
                foreach (char c in characters.Distinct())
                {
                    if (char.IsWhiteSpace(c) || char.IsControl(c) || char.IsSurrogate(c) || isCovered(c))
                        continue;

                    string? family = ChooseCoverageFamily(c, coverageCandidates(), familyHasGlyph);

                    if (family == null || !registerCoverage(family))
                        missing.Add(c);
                }
            }

            if (missing.Count > 0)
                Logger.Log($"No installed font can draw the lyric characters {string.Concat(missing)}; they show as the fallback glyph.");

            return missing;
        }

        /// <summary>
        /// THE CHOICE, pure so it is pinned by tests: the first of <paramref name="candidates"/> that
        /// <paramref name="hasGlyph"/> says draws <paramref name="character"/>, or null when none does.
        /// </summary>
        public static string? ChooseCoverageFamily(char character, IEnumerable<string> candidates, Func<string, char, bool> hasGlyph)
            => candidates.FirstOrDefault(family => hasGlyph(family, character));

        private IEnumerable<string> coverageCandidates()
        {
            var installed = GetSystemFontFamilies();
            var set = new HashSet<string>(installed, StringComparer.OrdinalIgnoreCase);

            return PREFERRED_COVERAGE_FAMILIES.Where(set.Contains).Concat(installed.Where(f => !PREFERRED_COVERAGE_FAMILIES.Contains(f, StringComparer.OrdinalIgnoreCase)));
        }

        private bool isCovered(char c)
        {
            try
            {
                return ((ITexturedGlyphLookupStore)fonts!).Get(string.Empty, c) != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private bool familyHasGlyph(string family, char c)
        {
            try
            {
                if (!coverageProbes.TryGetValue(family, out var probe))
                {
                    var resolved = resolveSystemFamily(family);

                    if (resolved == null)
                        return false;

                    coverageProbes[family] = probe = new RuntimeFontGlyphStore(resolved.Value, COVERAGE_FALLBACK_PREFIX + family);
                }

                return probe.HasGlyph(c);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private bool registerCoverage(string family)
        {
            string key = COVERAGE_FALLBACK_PREFIX + family;

            if (registered.TryGetValue(key, out bool already))
                return already;

            bool ok = coverageProbes.TryGetValue(family, out var store);

            if (ok)
            {
                fonts!.AddTextureSource(store!);
                Logger.Log($"Registered '{family}' as a lyric font fallback for script coverage.");
            }

            registered[key] = ok;
            return ok;
        }

        #endregion

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
