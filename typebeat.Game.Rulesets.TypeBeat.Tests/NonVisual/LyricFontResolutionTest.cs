// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Graphics.Rendering.Dummy;
using osu.Framework.IO.Stores;
using typebeat.Game.Graphics.Fonts;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Edit;
using typebeat.Game.Rulesets.TypeBeat.UI;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The map-font arm of backlog 291: the resolution order for the one family string the lyric
    /// stack renders in (<see cref="LyricFontResolution"/>), the register-from-stream path a
    /// bundled font file goes through (a corrupt or missing file falls back, never failing the
    /// play), and the editor's bundling rule (OS-bundled families default the toggle OFF,
    /// everything else ON).
    /// </summary>
    [TestFixture]
    public class LyricFontResolutionTest
    {
        private const string map_family = "Map Family";
        private const string player_family = "Player Family";

        // ---- resolution order ----

        [Test]
        public void EnabledMapFontBeatsPlayerSetting()
        {
            // When map fonts are enabled, a usable map file wins over a personal font choice.
            bool fileAsked = false;

            string? resolved = LyricFontResolution.Resolve(player_family, true, map_family,
                _ => true,
                () => { fileAsked = true; return "MapFont-abc"; });

            Assert.Multiple(() =>
            {
                Assert.That(resolved, Is.EqualTo("MapFont-abc"));
                Assert.That(fileAsked, Is.True, "the map font is checked before the player's choice");
            });
        }

        [Test]
        public void UseMapFontsOffIgnoresTheMapEntirely()
        {
            bool fileAsked = false;

            string? resolved = LyricFontResolution.Resolve(player_family, false, map_family,
                _ => true,
                () => { fileAsked = true; return "MapFont-abc"; });

            Assert.Multiple(() =>
            {
                Assert.That(resolved, Is.EqualTo(player_family), "with the setting off, the player's font is used");
                Assert.That(fileAsked, Is.False);
            });
        }

        [Test]
        public void DefaultPlusOnUsesTheMapFontFileFirst()
        {
            string? resolved = LyricFontResolution.Resolve(TypeBeatRulesetConfigManager.LYRIC_FONT_DEFAULT, true, map_family,
                _ => true,
                () => "MapFont-abc");

            Assert.That(resolved, Is.EqualTo("MapFont-abc"), "the bundled file is the map font's preferred source");
        }

        [Test]
        public void AFailedFileFallsToTheFamilyName()
        {
            var asked = new List<string>();

            string? resolved = LyricFontResolution.Resolve(null, true, map_family,
                f => { asked.Add(f); return true; },
                () => null);

            Assert.Multiple(() =>
            {
                Assert.That(resolved, Is.EqualTo(map_family));
                Assert.That(asked, Is.EqualTo(new[] { map_family }));
            });
        }

        [Test]
        public void AMissingFamilyFallsThroughToTheBuiltIn()
        {
            string? resolved = LyricFontResolution.Resolve(TypeBeatRulesetConfigManager.LYRIC_FONT_DEFAULT, true, map_family,
                _ => false,
                () => null);

            Assert.That(resolved, Is.Null, "an unresolvable map font never fails the play, it falls back");
        }

        [Test]
        public void AFailedMapFontFallsThroughToThePlayerFont()
        {
            // An unusable map font yields to the player's own choice.
            string? resolved = LyricFontResolution.Resolve(player_family, true, map_family,
                f => f == player_family,
                () => null);

            Assert.That(resolved, Is.EqualTo(player_family));
        }

        [Test]
        public void AMapWithNoFontResolvesToTheBuiltIn()
        {
            string? resolved = LyricFontResolution.Resolve(TypeBeatRulesetConfigManager.LYRIC_FONT_DEFAULT, true, string.Empty,
                _ => true, null);

            Assert.That(resolved, Is.Null);
        }

        [Test]
        public void ConfiguredResolutionUsesMapSettingAndSkipsMapRegistrationWhenDisabled()
        {
            var config = (TypeBeatRulesetConfigManager)new TypeBeatRuleset().CreateConfig(null);
            var manager = new LyricFontManager(null, null);
            int registerCalls = 0;

            string? resolved = LyricFontResolution.ResolveConfigured(config, manager, map_family, () =>
            {
                registerCalls++;
                return "MapFont-configured";
            });

            Assert.Multiple(() =>
            {
                Assert.That(resolved, Is.EqualTo("MapFont-configured"));
                Assert.That(registerCalls, Is.EqualTo(1), "the map file is the first choice when map fonts are enabled");
            });

            config.GetBindable<bool>(TypeBeatRulesetSetting.UseMapFonts).Value = false;
            resolved = LyricFontResolution.ResolveConfigured(config, manager, map_family, () =>
            {
                registerCalls++;
                return "MapFont-configured";
            });

            Assert.Multiple(() =>
            {
                Assert.That(resolved, Is.Null, "with the player font left at its built-in default, a disabled map font resolves to built-in");
                Assert.That(registerCalls, Is.EqualTo(1), "a disabled map font does not open its bundled file");
            });
        }

        [Test]
        public void UseMapFontsShipsOn()
        {
            var config = (TypeBeatRulesetConfigManager)new TypeBeatRuleset().CreateConfig(null);

            Assert.That(config.GetBindable<bool>(TypeBeatRulesetSetting.UseMapFonts).Default, Is.True,
                "map fonts apply by default; the setting exists to opt out");
        }

        [Test]
        public void EditorFontCacheTracksTypingFontAndToggleIndependentlyOfGameplayMapFonts()
        {
            using var config = (TypeBeatRulesetConfigManager)new TypeBeatRuleset().CreateConfig(null);
            using var fonts = new FontStore(new DummyRenderer(), null, 100);
            var manager = new LyricFontManager(fonts, null);
            var cache = new EditorLyricFontResolutionCache();
            config.SetValue(TypeBeatRulesetSetting.LyricFont, LyricFontManager.OPEN_DYSLEXIC);
            Assert.That(config.Get<bool>(TypeBeatRulesetSetting.UseTypingFontInEditor), Is.True);
            Assert.That(cache.ResolveForEditor(config, manager), Is.EqualTo(LyricFontManager.OPEN_DYSLEXIC));

            config.SetValue(TypeBeatRulesetSetting.UseTypingFontInEditor, false);
            Assert.That(cache.ResolveForEditor(config, manager), Is.Null);
            config.SetValue(TypeBeatRulesetSetting.UseMapFonts, false);
            Assert.That(cache.ResolveForEditor(config, manager), Is.Null);
            config.SetValue(TypeBeatRulesetSetting.UseTypingFontInEditor, true);
            Assert.That(cache.ResolveForEditor(config, manager), Is.EqualTo(LyricFontManager.OPEN_DYSLEXIC));
            config.SetValue(TypeBeatRulesetSetting.UseMapFonts, true);
            Assert.That(cache.ResolveForEditor(config, manager), Is.EqualTo(LyricFontManager.OPEN_DYSLEXIC));

            config.SetValue(TypeBeatRulesetSetting.LyricFont, TypeBeatRulesetConfigManager.LYRIC_FONT_DEFAULT);
            Assert.That(cache.ResolveForEditor(config, manager), Is.Null);
            config.SetValue(TypeBeatRulesetSetting.LyricFont, "typebeat-unavailable-editor-font");
            Assert.That(cache.ResolveForEditor(config, manager), Is.Null, "an unavailable typing font falls back to the built-in editor font");
        }

        // ---- the register-from-stream path (the bundled file) ----

        [Test]
        public void ACorruptBundledFileFallsBackWithoutThrowing()
        {
            var manager = new LyricFontManager(new FontStore(new DummyRenderer(), null, 100), null);

            bool ok = true;
            Assert.DoesNotThrow(() => ok = manager.EnsureRegisteredFromStream("MapFont-corrupt", () => new MemoryStream(new byte[] { 0x00, 0x01, 0x02, 0x03 })));
            Assert.That(ok, Is.False, "a corrupt font file reports unusable so the caller falls back");
        }

        [Test]
        public void AMissingBundledFileFallsBackWithoutThrowing()
        {
            var manager = new LyricFontManager(new FontStore(new DummyRenderer(), null, 100), null);

            bool ok = true;
            Assert.DoesNotThrow(() => ok = manager.EnsureRegisteredFromStream("MapFont-missing", () => null));
            Assert.That(ok, Is.False);
        }

        [Test]
        public void AValidBundledFileRegistersUnderItsOwnKey()
        {
            var manager = new LyricFontManager(new FontStore(new DummyRenderer(), null, 100), null);

            Assert.That(manager.EnsureRegisteredFromStream("MapFont-abc123", openEmbeddedFont), Is.True);
        }

        [Test]
        public void RegistrationIsCachedPerKey()
        {
            var manager = new LyricFontManager(new FontStore(new DummyRenderer(), null, 100), null);
            int opened = 0;

            Func<Stream?> counting = () =>
            {
                opened++;
                return openEmbeddedFont();
            };

            Assert.Multiple(() =>
            {
                Assert.That(manager.EnsureRegisteredFromStream("MapFont-cached", counting), Is.True);
                Assert.That(manager.EnsureRegisteredFromStream("MapFont-cached", counting), Is.True);
                Assert.That(opened, Is.EqualTo(1), "the same file must not be re-parsed per play");
            });
        }

        // ---- the editor's bundling rule ----

        [Test]
        public void OsBundledFamiliesDefaultTheToggleOff()
        {
            // The fonts everybody has are exactly the ones whose licences forbid redistribution.
            foreach (string family in new[] { "Arial", "Segoe UI", "Helvetica Neue", "Times New Roman", "Ubuntu", "DejaVu Sans" })
            {
                Assert.That(OsBundledFontFamilies.Contains(family), Is.True, family);
                Assert.That(TypeBeatSetupSection.BundleDefaultFor(family), Is.False, family);
            }
        }

        [Test]
        public void TheTableMatchesCaseInsensitively()
        {
            Assert.Multiple(() =>
            {
                Assert.That(OsBundledFontFamilies.Contains("arial"), Is.True);
                Assert.That(OsBundledFontFamilies.Contains("SEGOE UI"), Is.True);
                Assert.That(OsBundledFontFamilies.Contains(" Arial "), Is.True, "trimmed before matching");
            });
        }

        [Test]
        public void NovelFamiliesDefaultTheToggleOn()
        {
            // The fonts nobody has (Google Fonts, itch pixel fonts) are usually OFL or freeware,
            // and bundling is the only way players see them at all.
            foreach (string family in new[] { "Minecraftia", "Comic Neue", "JetBrains Mono", "Some Bespoke Font" })
            {
                Assert.That(OsBundledFontFamilies.Contains(family), Is.False, family);
                Assert.That(TypeBeatSetupSection.BundleDefaultFor(family), Is.True, family);
            }
        }

        private static Stream? openEmbeddedFont()
        {
            // The bundled OpenDyslexic face doubles as a known-good font binary.
            var asm = typeof(LyricFontManager).Assembly;
            string name = asm.GetManifestResourceNames().First(n => n.EndsWith("OpenDyslexic-Regular.otf", StringComparison.OrdinalIgnoreCase));
            return asm.GetManifestResourceStream(name);
        }
    }
}
