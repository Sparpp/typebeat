// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using typebeat.Game.Rulesets.TypeBeat.Configuration;

namespace typebeat.Game.Rulesets.TypeBeat.UI
{
    /// <summary>
    /// THE resolution order for the one font family the lyric stack renders in (backlog 291), kept
    /// as a pure function so the order itself is pinned by tests rather than by convention:
    ///
    /// <list type="number">
    /// <item>The map's font, when the player's <see cref="TypeBeatRulesetSetting.UseMapFonts"/>
    /// setting (default ON) allows it: the bundled font file first (registered from the set's file
    /// store under a per-file key), then the declared family by name from the system fonts.</item>
    /// <item>The player's own <see cref="TypeBeatRulesetSetting.LyricFont"/> pick, when no usable map
    /// font was selected or the map font setting is off.</item>
    /// <item>The built-in lyric font (null).</item>
    /// </list>
    ///
    /// <para>Every step falls through on failure rather than failing the play: an unusable map
    /// font yields to the player's choice, then the built-in font. The registration delegates own
    /// all logging.</para>
    /// </summary>
    public static class LyricFontResolution
    {
        /// <summary>
        /// Resolves the family string the lyric displays should render in, or null for the built-in
        /// font.
        /// </summary>
        /// <param name="playerFamily">The player's <see cref="TypeBeatRulesetSetting.LyricFont"/>
        /// value; the Default sentinel or empty means no personal pick.</param>
        /// <param name="useMapFonts">The player's <see cref="TypeBeatRulesetSetting.UseMapFonts"/>
        /// value; false ignores the map's font entirely.</param>
        /// <param name="mapFamily">The map's declared family
        /// (<c>BeatmapMetadata.LyricFont</c>), empty when the map declares none.</param>
        /// <param name="ensureFamily">Registers a family by name (system fonts or the bundled
        /// OpenDyslexic) and reports whether it is usable; <c>LyricFontManager.EnsureRegistered</c>
        /// in production.</param>
        /// <param name="registerMapFile">Registers the map's bundled font file, returning the key
        /// it was registered under, or null when there is no file or it could not be loaded. Null
        /// when the caller has no file store at all.</param>
        public static string? Resolve(string? playerFamily, bool useMapFonts, string? mapFamily,
                                      Func<string, bool> ensureFamily, Func<string?>? registerMapFile)
        {
            if (useMapFonts)
            {
                string? bundled = registerMapFile?.Invoke();

                if (bundled != null)
                    return bundled;

                if (!string.IsNullOrWhiteSpace(mapFamily) && ensureFamily(mapFamily!))
                    return mapFamily;
            }

            if (!isDefault(playerFamily) && ensureFamily(playerFamily!))
                return playerFamily;

            return null;
        }

        private static bool isDefault(string? family)
            => string.IsNullOrWhiteSpace(family)
               || family.Equals(TypeBeatRulesetConfigManager.LYRIC_FONT_DEFAULT, StringComparison.Ordinal);
    }
}
