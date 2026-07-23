// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using SixLabors.Fonts;
using typebeat.Game.Graphics.Fonts;
using typebeat.Game.Rulesets.TypeBeat.Configuration;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Covers the accessibility typing-font feature (backlog 19): the config round-trip for the
    /// <see cref="TypeBeatRulesetSetting.LyricFont"/> setting, the system-font enumeration shape, the
    /// bundled OpenDyslexic availability, and the runtime glyph store's rasterisation + missing-glyph
    /// fallback. Visual placement is confirmed manually.
    /// </summary>
    [TestFixture]
    public class LyricFontTest
    {
        [Test]
        public void LyricFontDefaultsToBuiltInSentinel()
        {
            var config = (TypeBeatRulesetConfigManager)new TypeBeatRuleset().CreateConfig(null);

            Assert.That(config.GetBindable<string>(TypeBeatRulesetSetting.LyricFont).Default,
                Is.EqualTo(TypeBeatRulesetConfigManager.LYRIC_FONT_DEFAULT));
        }

        [Test]
        public void LyricFontRoundTripsThroughConfig()
        {
            var config = (TypeBeatRulesetConfigManager)new TypeBeatRuleset().CreateConfig(null);
            var bindable = config.GetBindable<string>(TypeBeatRulesetSetting.LyricFont);

            bindable.Value = LyricFontManager.OPEN_DYSLEXIC;
            Assert.That(config.GetBindable<string>(TypeBeatRulesetSetting.LyricFont).Value, Is.EqualTo(LyricFontManager.OPEN_DYSLEXIC));

            bindable.Value = "Some Installed Family";
            Assert.That(config.GetBindable<string>(TypeBeatRulesetSetting.LyricFont).Value, Is.EqualTo("Some Installed Family"));
        }

        [Test]
        public void SystemFontEnumerationIsSortedDistinctAndNonEmpty()
        {
            var manager = new LyricFontManager(null, null);

            var families = manager.GetSystemFontFamilies();

            Assert.That(families, Is.Not.Null);
            Assert.That(families.Count, Is.GreaterThan(0), "expected at least one installed system font");
            // Deduplicated (case-insensitive) and alphabetically ordered.
            Assert.That(families.Select(n => n.ToLowerInvariant()).Distinct().Count(), Is.EqualTo(families.Count));
            var sorted = families.OrderBy(n => n, System.StringComparer.CurrentCultureIgnoreCase).ToArray();
            Assert.That(families, Is.EqualTo(sorted));
        }

        [Test]
        public void OpenDyslexicIsBundledAndAvailable()
        {
            var manager = new LyricFontManager(null, null);
            Assert.That(manager.IsOpenDyslexicAvailable, Is.True, "OpenDyslexic-Regular.otf should be embedded in typebeat.Game");
        }

        [Test]
        public void GlyphStoreRasterisesAndReportsMetrics()
        {
            var store = openDyslexicStore();

            Assert.That(store.HasGlyph('a'), Is.True);

            var glyph = store.Get('a');
            Assert.That(glyph, Is.Not.Null);
            Assert.That(glyph!.XAdvance, Is.GreaterThan(0));
            Assert.That(store.Baseline, Is.Not.Null.And.GreaterThan(0));

            // The texture path returns a real rasterised bitmap for the same character.
            var upload = store.Get("OpenDyslexic/a");
            Assert.That(upload, Is.Not.Null);
            Assert.That(upload!.Width, Is.GreaterThan(1));
            Assert.That(upload.Height, Is.GreaterThan(1));
        }

        [Test]
        public void GlyphStoreReportsMissingGlyphForUnsupportedCharacter()
        {
            var store = openDyslexicStore();

            // OpenDyslexic is a Latin face with no CJK coverage; the store must report the glyph
            // missing (HasGlyph false / Get null) so the framework falls back to the default font
            // rather than rendering a blank or throwing.
            Assert.That(store.HasGlyph('中'), Is.False);
            Assert.That(store.Get('中'), Is.Null);
            Assert.That(store.Get("OpenDyslexic/中"), Is.Null);
        }

        private static RuntimeFontGlyphStore openDyslexicStore()
        {
            var asm = typeof(LyricFontManager).Assembly;
            string name = asm.GetManifestResourceNames().First(n => n.EndsWith("OpenDyslexic-Regular.otf", System.StringComparison.OrdinalIgnoreCase));

            using var stream = asm.GetManifestResourceStream(name);
            Assert.That(stream, Is.Not.Null);

            var family = new FontCollection().Add(stream!, System.Globalization.CultureInfo.InvariantCulture);
            return new RuntimeFontGlyphStore(family, LyricFontManager.OPEN_DYSLEXIC);
        }
    }
}
