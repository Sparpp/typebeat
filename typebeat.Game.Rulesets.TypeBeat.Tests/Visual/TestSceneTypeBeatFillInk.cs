// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.IO.Stores;
using SixLabors.Fonts;
using typebeat.Game.Graphics.Fonts;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// The bottom-to-top fill ends on each letter's OWN ink (backlog 467): a short letter's fill tops out
    /// lower than a tall one's, and a descender's starts lower, in the built-in lyric font and in a font
    /// rasterised at runtime (a player's chosen font, a map font) alike.
    /// </summary>
    public partial class TestSceneTypeBeatFillInk : OsuTestScene
    {
        [Resolved]
        private FontStore fontStore { get; set; } = null!;

        [TestCase(null)]
        [TestCase(LyricFontManager.OPEN_DYSLEXIC)]
        public void EachLetterFillsOverItsOwnInk(string? family)
        {
            AddStep("load the font", () =>
            {
                if (family != null)
                    fontStore.AddTextureSource(openDyslexicStore());
            });

            AddAssert("short letters top out lower than tall ones", () =>
            {
                var c = ink('c', family);
                var o = ink('o', family);
                var t = ink('t', family);
                var h = ink('h', family);
                var p = ink('p', family);

                return c.Top > t.Top + 2 && c.Top > h.Top + 2
                       && System.Math.Abs(c.Top - o.Top) < 3
                       && p.Bottom > c.Bottom + 2
                       && c.Top > 0;
            });

            // OpenDyslexic's descenders reach below the font-size box: the fill must follow the ink
            // there, or the tail of a 'p' would never fill.
            AddAssert("a fill covers ink outside the box at full progress, and none of it at zero", () =>
            {
                var p = ink('p', family);
                var box = new osuTK.Vector2(20, size);
                var empty = LyricLineDisplay.IndicatorMaskGeometry(Configuration.JudgementIndicatorMode.BottomToTopFill, box, 0, p.Top, p.Bottom);
                var nearlyFull = LyricLineDisplay.IndicatorMaskGeometry(Configuration.JudgementIndicatorMode.BottomToTopFill, box, 0.999f, p.Top, p.Bottom);
                return empty.Position.Y >= p.Bottom - 0.01f
                       && nearlyFull.Position.Y + nearlyFull.Size.Y >= p.Bottom - 0.01f
                       && nearlyFull.Position.Y <= p.Top + (p.Bottom - p.Top) * 0.002f;
            });
        }

        private static float size => TypeBeatStyle.LYRIC_FONT_SIZE;

        private (float Top, float Bottom) ink(char letter, string? family)
        {
            var span = LyricLineDisplay.InkExtent(fontStore, sprite(family), letter.ToString());
            Assert.That(span, Is.Not.Null, $"no ink measured for '{letter}'");
            return span!.Value;
        }

        private OsuSpriteText sprite(string? family)
        {
            // The box is the font size (full glyph height); only the font and layout settings are read.
            return new OsuSpriteText { Font = TypeBeatStyle.Lyric(size, family) };
        }

        private static RuntimeFontGlyphStore openDyslexicStore()
        {
            var asm = typeof(LyricFontManager).Assembly;
            string name = asm.GetManifestResourceNames().First(n => n.EndsWith("OpenDyslexic-Regular.otf", System.StringComparison.OrdinalIgnoreCase));

            using var stream = asm.GetManifestResourceStream(name);
            var collection = new FontCollection().Add(stream!, System.Globalization.CultureInfo.InvariantCulture);
            return new RuntimeFontGlyphStore(collection, LyricFontManager.OPEN_DYSLEXIC);
        }
    }
}
