// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;
using osu.Framework.Text;
using SixLabors.Fonts;
using SixLabors.Fonts.Unicode;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace typebeat.Game.Graphics.Fonts
{
    /// <summary>
    /// An <see cref="IGlyphStore"/> that rasterises a TrueType/OpenType face at runtime with
    /// SixLabors.Fonts + ImageSharp.Drawing, instead of reading osu-framework's packaged BMFont
    /// sprite-sheet format. This is what lets the gameplay typing surface use OpenDyslexic or an
    /// arbitrary installed system font, neither of which ships in the game's BMFont resources.
    /// </summary>
    /// <remarks>
    /// Glyphs are rasterised at <see cref="render_em"/> pixels-per-em so their metrics land in the
    /// same unit space that the top-level <c>Game.Fonts</c> store normalises with its scale-adjust
    /// of 100; that keeps a <c>size:</c> value on a <see cref="osu.Framework.Graphics.Sprites.FontUsage"/>
    /// mean the same on-screen height as it does for the game's built-in fonts.
    ///
    /// Glyphs are drawn white with per-pixel coverage as the alpha channel (straight alpha), matching
    /// what <see cref="GlyphStore"/> produces, so <see cref="osu.Framework.Graphics.Sprites.SpriteText"/>
    /// tinting still works. Characters the face cannot render report <see cref="HasGlyph"/> false, which
    /// makes the framework fall through to the default-font glyph for that character (no blanks, no crash).
    /// </remarks>
    public class RuntimeFontGlyphStore : IResourceStore<TextureUpload>, IGlyphStore
    {
        /// <summary>
        /// The pixels-per-em the face is rasterised at. Chosen to match the <c>Game.Fonts</c>
        /// scale-adjust (100) so glyph metrics are already in the normalised unit space.
        /// </summary>
        private const float render_em = 100f;

        /// <summary>Transparent border (px) around every rasterised glyph so anti-aliased edges are not clipped.</summary>
        private const int glyph_padding = 1;

        public string FontName { get; }

        public float? Baseline { get; private set; }

        private readonly FontFamily family;
        private Font font;

        // SixLabors font instances hold internal layout caches that are not documented as
        // thread-safe; glyph lookups can arrive from concurrent async SpriteText loads, so all
        // measuring/rasterising is serialised through this lock.
        private readonly object renderLock = new object();

        private readonly Dictionary<char, glyphMetrics> metricsCache = new Dictionary<char, glyphMetrics>();

        public RuntimeFontGlyphStore(FontFamily family, string fontName)
        {
            this.family = family;
            FontName = fontName;
        }

        public Task LoadFontAsync() => Task.Run(ensureLoaded);

        private void ensureLoaded()
        {
            lock (renderLock)
            {
                if (font != null)
                    return;

                font = family.CreateFont(render_em);
                var h = font.FontMetrics.HorizontalMetrics;
                // Distance from the line's top down to the baseline, in render-em px. Only used for
                // cross-glyph baseline alignment; every glyph in this store shares it.
                Baseline = h.Ascender / (float)font.FontMetrics.UnitsPerEm * render_em;
            }
        }

        public bool HasGlyph(char c)
        {
            ensureLoaded();

            lock (renderLock)
            {
                if (font == null)
                    return false;

                // TryGetGlyphs returns the .notdef substitution (GlyphId 0) for unmapped code
                // points, so a real glyph must additionally have a non-zero id; that is what lets
                // the framework fall through to the default font for characters this face lacks.
                return font.TryGetGlyphs(new CodePoint(c), out var glyphs)
                       && glyphs.Count > 0
                       && glyphs[0].GlyphMetrics.GlyphId != 0;
            }
        }

        public CharacterGlyph Get(char character)
        {
            if (!HasGlyph(character))
                return null;

            var m = getMetrics(character);
            return new CharacterGlyph(character, m.XOffset, m.YOffset, m.XAdvance, Baseline ?? 0, this);
        }

        public int GetKerning(char left, char right)
        {
            // SixLabors applies kerning internally during measurement/rendering; the layout in
            // LyricLineDisplay measures each glyph's advance individually, so no extra pair kerning
            // is exposed here (mirrors a BMFont with no explicit kerning table).
            return 0;
        }

        Task<CharacterGlyph> IResourceStore<CharacterGlyph>.GetAsync(string name, CancellationToken cancellationToken) =>
            Task.Run(() => Get(name[^1]), cancellationToken);

        CharacterGlyph IResourceStore<CharacterGlyph>.Get(string name) => Get(name[^1]);

        public TextureUpload Get(string name)
        {
            if (name.Length > 1 && !name.StartsWith($"{FontName}/", StringComparison.Ordinal))
                return null;

            char c = name[^1];

            if (!HasGlyph(c))
                return null;

            return rasterise(c);
        }

        public Task<TextureUpload> GetAsync(string name, CancellationToken cancellationToken = default) =>
            Task.Run(() => Get(name), cancellationToken);

        private glyphMetrics getMetrics(char c)
        {
            ensureLoaded();

            lock (renderLock)
            {
                if (metricsCache.TryGetValue(c, out var cached))
                    return cached;

                string s = c.ToString();
                var options = new TextOptions(font);

                FontRectangle bounds = TextMeasurer.MeasureBounds(s, options);
                FontRectangle advance = TextMeasurer.MeasureAdvance(s, options);

                glyphMetrics m;

                if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
                {
                    // Whitespace / zero-ink glyph: no texture footprint, advance only.
                    m = new glyphMetrics(0, 0, 1, 1, advance.Width);
                }
                else
                {
                    int width = (int)MathF.Ceiling(bounds.Width) + glyph_padding * 2;
                    int height = (int)MathF.Ceiling(bounds.Height) + glyph_padding * 2;
                    // XOffset/YOffset place the padded glyph image relative to the line origin so
                    // the ink lands exactly where MeasureBounds reported it.
                    m = new glyphMetrics(bounds.X - glyph_padding, bounds.Y - glyph_padding, width, height, advance.Width);
                }

                metricsCache[c] = m;
                return m;
            }
        }

        private TextureUpload rasterise(char c)
        {
            var m = getMetrics(c);

            lock (renderLock)
            {
                if (m.Width <= 1 && m.Height <= 1)
                    return new TextureUpload(new Image<Rgba32>(1, 1));

                var image = new Image<Rgba32>(SixLabors.ImageSharp.Configuration.Default, m.Width, m.Height);

                // Draw the glyph white; anti-aliased coverage becomes the alpha channel (straight
                // alpha), so SpriteText's colour tint applies cleanly. The origin is shifted by the
                // measured ink bounds + padding so the glyph sits inside the padded image.
                var origin = new PointF(glyph_padding - (m.XOffset + glyph_padding), glyph_padding - (m.YOffset + glyph_padding));
                image.Mutate(ctx => ctx.DrawText(c.ToString(), font, Color.White, origin));

                return new TextureUpload(image);
            }
        }

        public Stream GetStream(string name) => throw new NotSupportedException();

        public IEnumerable<string> GetAvailableResources() => Array.Empty<string>();

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }

        private readonly record struct glyphMetrics(float XOffset, float YOffset, int Width, int Height, float XAdvance);
    }
}
