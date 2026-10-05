// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.Text;
using typebeat.Game.Graphics.Sprites;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class SurrogatePlaceholderGlyphStoreTest
    {
        private const float size = 10;

        [Test]
        public void EmojiLaysOutAsOnePlaceholder()
        {
            var fonts = new FakeGlyphStore(SurrogatePlaceholderGlyphStore.PLACEHOLDER);
            var characters = layout(new SurrogatePlaceholderGlyphStore(fonts), "a\U0001F600b");

            Assert.That(characters.Select(c => c.Glyph.Character), Is.EqualTo(new[] { 'a', SurrogatePlaceholderGlyphStore.PLACEHOLDER, '\uDE00', 'b' }));

            // The low half's quad is still drawn (inflated for anti-aliasing), so it must borrow the
            // transparent space glyph rather than anything with ink, which showed as a stray pixel.
            Assert.That(fonts.Requested, Does.Contain(' '));
            Assert.That(fonts.Requested, Does.Not.Contain('?'));

            // The low half takes no space, so 'b' sits right after the placeholder.
            Assert.That(characters[2].Width, Is.Zero);
            Assert.That(characters[2].XAdvance, Is.Zero);
            Assert.That(characters[3].DrawRectangle.X, Is.EqualTo(characters[1].DrawRectangle.X + size));
        }

        [Test]
        public void WithoutPlaceholderGlyphEmojiFallsBackToOneQuestionMark()
        {
            var characters = layout(new SurrogatePlaceholderGlyphStore(new FakeGlyphStore()), "a\U0001F600b");

            Assert.That(characters.Where(c => c.Width > 0).Select(c => c.Glyph.Character), Is.EqualTo(new[] { 'a', '?', 'b' }));
        }

        [Test]
        public void UnwrappedStoreDrawsAQuestionMarkPerHalf()
        {
            // What the framework does on its own, which the wrapper exists to avoid.
            var characters = layout(new FakeGlyphStore(SurrogatePlaceholderGlyphStore.PLACEHOLDER), "a\U0001F600b");

            Assert.That(characters.Select(c => c.Glyph.Character), Is.EqualTo(new[] { 'a', '?', '?', 'b' }));
        }

        [Test]
        public void OrdinaryTextPassesThrough()
        {
            var characters = layout(new SurrogatePlaceholderGlyphStore(new FakeGlyphStore(SurrogatePlaceholderGlyphStore.PLACEHOLDER)), "abc");

            Assert.That(characters.Select(c => c.Glyph.Character), Is.EqualTo("abc".ToCharArray()));
        }

        private static TextBuilderGlyph[] layout(ITexturedGlyphLookupStore store, string text)
        {
            var builder = new TextBuilder(store, new FontUsage("Fake", size));
            builder.AddText(text);
            return builder.Characters.ToArray();
        }

        /// <summary>Every BMP character is one unit wide; surrogate halves are missing, as in any real font.</summary>
        private class FakeGlyphStore : ITexturedGlyphLookupStore
        {
            public readonly List<char> Requested = new List<char>();

            private readonly char? placeholder;

            public FakeGlyphStore(char? placeholder = null)
            {
                this.placeholder = placeholder;
            }

            public ITexturedCharacterGlyph? Get(string? fontName, char character)
            {
                Requested.Add(character);

                if (char.IsSurrogate(character) || (character == SurrogatePlaceholderGlyphStore.PLACEHOLDER && placeholder == null))
                    return null;

                return new FakeGlyph(character);
            }

            public Task<ITexturedCharacterGlyph?> GetAsync(string fontName, char character) => Task.FromResult(Get(fontName, character));
        }

        private class FakeGlyph : ITexturedCharacterGlyph
        {
            public FakeGlyph(char character)
            {
                Character = character;
            }

            public Texture Texture => null!;
            public float Width => 1;
            public float Height => 1;
            public float XOffset => 0;
            public float YOffset => 0;
            public float XAdvance => 1;
            public float Baseline => 1;
            public char Character { get; }

            public float GetKerning<T>(T lastGlyph) where T : ICharacterGlyph => 0;
        }
    }
}
