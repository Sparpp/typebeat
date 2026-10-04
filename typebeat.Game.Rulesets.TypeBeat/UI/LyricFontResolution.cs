// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using osu.Framework.Logging;
using typebeat.Game.Beatmaps;
using typebeat.Game.Graphics.Fonts;
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

        /// <summary>
        /// Resolves a map's gameplay font using the player's settings. The precedence is shared
        /// with <see cref="Resolve"/>; editor lyrics use <see cref="ResolveForEditor"/> instead.
        /// </summary>
        public static string? ResolveConfigured(TypeBeatRulesetConfigManager? config, LyricFontManager? fontManager,
                                                string? mapFamily, Func<string?>? registerMapFile)
        {
            if (fontManager == null)
                return null;

            string? playerFamily = config?.Get<string>(TypeBeatRulesetSetting.LyricFont);
            bool useMapFonts = config?.Get<bool>(TypeBeatRulesetSetting.UseMapFonts) ?? true;
            return Resolve(playerFamily, useMapFonts, mapFamily, fontManager.EnsureRegistered, registerMapFile);
        }

        public static string? ResolveForMap(TypeBeatRulesetConfigManager? config, LyricFontManager? fontManager,
                                           string? mapFamily, string? mapFontFile, WorkingBeatmap? workingBeatmap)
        {
            if (fontManager == null)
                return null;

            bool useMapFonts = config?.Get<bool>(TypeBeatRulesetSetting.UseMapFonts) ?? true;
            string? resolved = ResolveConfigured(config, fontManager, mapFamily,
                () => registerMapFontFile(fontManager, workingBeatmap, mapFontFile));

            if (resolved == null && useMapFonts && !string.IsNullOrEmpty(mapFamily))
                Logger.Log($"The map's lyric font '{mapFamily}' could not be resolved on this machine; using the built-in font.");

            return resolved;
        }

        /// <summary>Editor lyrics use only the player's typing font, when enabled, or the built-in editor font.</summary>
        public static string? ResolveForEditor(TypeBeatRulesetConfigManager? config, LyricFontManager? fontManager)
        {
            if (fontManager == null || config?.Get<bool>(TypeBeatRulesetSetting.UseTypingFontInEditor) == false)
                return null;

            return Resolve(config?.Get<string>(TypeBeatRulesetSetting.LyricFont), false, null, fontManager.EnsureRegistered, null);
        }

        private static string? registerMapFontFile(LyricFontManager fontManager, WorkingBeatmap? workingBeatmap, string? fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return null;

            string? storagePath = workingBeatmap?.BeatmapSetInfo?.GetPathForFile(fileName);

            if (workingBeatmap == null || storagePath == null)
            {
                Logger.Log($"The map names a bundled lyric font '{fileName}' that its set does not carry; falling back.");
                return null;
            }

            // The storage path identifies the set-owned file used by the framework's file store.
            // Register through WorkingBeatmap so missing, corrupt, or unreadable resources follow
            // the same safe fallback path as gameplay.
            string key = $"MapFont-{storagePath.Split('/')[^1]}";
            return fontManager.EnsureRegisteredFromStream(key, () => workingBeatmap.GetStream(storagePath)) ? key : null;
        }

        private static bool isDefault(string? family)
            => string.IsNullOrWhiteSpace(family)
               || family.Equals(TypeBeatRulesetConfigManager.LYRIC_FONT_DEFAULT, StringComparison.Ordinal);
    }

    /// <summary>
    /// Memoises the editor's typing font until the selected family or editor toggle changes,
    /// avoiding font registration and lookup on every frame. Map fonts do not affect this cache.
    /// </summary>
    public sealed class EditorLyricFontResolutionCache
    {
        private bool initialized;
        private TypeBeatRulesetConfigManager? lastConfig;
        private LyricFontManager? lastFontManager;
        private string? lastPlayerFamily;
        private bool lastUseTypingFont;
        private string? resolvedFamily;

        public string? ResolveForEditor(TypeBeatRulesetConfigManager? config, LyricFontManager? fontManager)
        {
            string? playerFamily = config?.Get<string>(TypeBeatRulesetSetting.LyricFont);
            bool useTypingFont = config?.Get<bool>(TypeBeatRulesetSetting.UseTypingFontInEditor) ?? true;

            if (initialized
                && ReferenceEquals(config, lastConfig)
                && ReferenceEquals(fontManager, lastFontManager)
                && string.Equals(playerFamily, lastPlayerFamily, StringComparison.Ordinal)
                && useTypingFont == lastUseTypingFont)
            {
                return resolvedFamily;
            }

            initialized = true;
            lastConfig = config;
            lastFontManager = fontManager;
            lastPlayerFamily = playerFamily;
            lastUseTypingFont = useTypingFont;
            resolvedFamily = LyricFontResolution.ResolveForEditor(config, fontManager);
            return resolvedFamily;
        }
    }
}
