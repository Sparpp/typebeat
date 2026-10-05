// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Text;

namespace typebeat.Game.Graphics.Sprites
{
    public partial class OsuSpriteText : SpriteText
    {
        [Obsolete("Use TruncatingSpriteText instead.")]
        public new bool Truncate
        {
            set => throw new InvalidOperationException($"Use {nameof(TruncatingSpriteText)} instead.");
        }

        public OsuSpriteText()
        {
            Shadow = true;
            Font = OsuFont.Default;
        }

        /// <summary>
        /// type!beat: draws an emoji (or any character outside the BMP) as one placeholder bullet
        /// rather than a '?' per surrogate half; see <see cref="SurrogatePlaceholderGlyphStore"/>.
        /// </summary>
        protected override TextBuilder CreateTextBuilder(ITexturedGlyphLookupStore store) =>
            base.CreateTextBuilder(new SurrogatePlaceholderGlyphStore(store));
    }
}
