// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Threading.Tasks;
using osu.Framework.Graphics.Textures;
using osu.Framework.Text;

namespace typebeat.Game.Graphics.Sprites
{
    /// <summary>
    /// type!beat: draws each character outside the Basic Multilingual Plane (an emoji, say) as a single
    /// <see cref="PLACEHOLDER"/>. The framework lays text out one UTF-16 char at a time, so such a
    /// character arrives as two surrogate halves that no font can draw, and the framework would show
    /// its fallback '?' for each. Here the high half takes the placeholder's glyph and the low half an
    /// invisible zero-width one; every other character passes straight through.
    /// </summary>
    /// <remarks>
    /// When no font has the placeholder the high half falls back to '?' as before, so the character
    /// still reads as one symbol. A lone high surrogate (malformed text) also draws the placeholder;
    /// a lone low one draws nothing.
    /// </remarks>
    public class SurrogatePlaceholderGlyphStore : ITexturedGlyphLookupStore
    {
        /// <summary>
        /// A bullet: Torus, Inter and Venera all carry it, so it sits at the surrounding text's size and
        /// baseline. (A square, the obvious choice, exists only in the bundled Noto CJK face and drew
        /// small, raised and crowding its neighbour.)
        /// </summary>
        public const char PLACEHOLDER = '•';

        private readonly ITexturedGlyphLookupStore store;

        public SurrogatePlaceholderGlyphStore(ITexturedGlyphLookupStore store)
        {
            this.store = store;
        }

        public ITexturedCharacterGlyph? Get(string? fontName, char character)
        {
            if (char.IsHighSurrogate(character))
                return store.Get(fontName, PLACEHOLDER);

            // The zero-width glyph still gets drawn: SpriteText inflates every glyph's quad slightly for
            // anti-aliasing, so even a zero-size one shows a sliver of its texture. It borrows the space
            // glyph's, which every font has and which is fully transparent.
            if (char.IsLowSurrogate(character))
                return store.Get(fontName, ' ') is ITexturedCharacterGlyph space ? new ZeroWidthGlyph(space, character) : null;

            return store.Get(fontName, character);
        }

        public Task<ITexturedCharacterGlyph?> GetAsync(string fontName, char character) =>
            char.IsSurrogate(character) ? Task.Run(() => Get(fontName, character)) : store.GetAsync(fontName, character);

        private class ZeroWidthGlyph : ITexturedCharacterGlyph
        {
            public Texture Texture { get; }
            public float Baseline { get; }
            public char Character { get; }

            public float Width => 0;
            public float Height => 0;
            public float XOffset => 0;
            public float YOffset => 0;
            public float XAdvance => 0;

            public ZeroWidthGlyph(ITexturedCharacterGlyph borrowed, char character)
            {
                Texture = borrowed.Texture;
                Baseline = borrowed.Baseline;
                Character = character;
            }

            public float GetKerning<T>(T lastGlyph) where T : ICharacterGlyph => 0;
        }
    }
}
