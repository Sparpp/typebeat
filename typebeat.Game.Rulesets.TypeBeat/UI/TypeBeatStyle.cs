// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Ported from type!beat TypeBeat.Game/TypeBeatStyle.cs. Colour fields became get-only
// properties (fork naming rules make public static readonly fields ALL_UPPER); the
// font factory now builds on type!beat's default font with a fixed per-glyph advance.

using osu.Framework.Graphics.Sprites;
using typebeat.Game.Graphics;
using osuTK;
using osuTK.Graphics;

namespace typebeat.Game.Rulesets.TypeBeat.UI
{
    /// <summary>
    /// Single source of visual truth for type!beat: the monkeytype "serika-dark"
    /// palette, a fixed-width font factory, and the shared animation constants.
    /// </summary>
    public static class TypeBeatStyle
    {
        // Exact monkeytype serika-dark hexes. Constructed from RGB bytes (cast to
        // disambiguate the byte ctor from the float ctor).
        public static Color4 Background { get; } = new Color4((byte)50, (byte)52, (byte)55, (byte)255);      // #323437
        public static Color4 UntypedChar { get; } = new Color4((byte)100, (byte)102, (byte)105, (byte)255); // #646669
        public static Color4 TypedChar { get; } = new Color4((byte)209, (byte)208, (byte)197, (byte)255);   // #d1d0c5
        public static Color4 ErrorChar { get; } = new Color4((byte)202, (byte)71, (byte)84, (byte)255);     // #ca4754
        public static Color4 Caret { get; } = new Color4((byte)226, (byte)183, (byte)20, (byte)255);        // #e2b714
        public static Color4 SungAccent { get; } = new Color4((byte)126, (byte)200, (byte)227, (byte)255);  // #7ec8e3
        public static Color4 PanelBackground { get; } = new Color4((byte)44, (byte)46, (byte)49, (byte)255); // #2c2e31

        /// <summary>
        /// FREESTYLE characters (the mapper's '&amp;' slots, where any key but space is accepted): a bright
        /// violet that reads clearly on the dark playfield and is unmistakable against every other
        /// character state, untyped grey #646669, typed off-white #d1d0c5, error red #ca4754, the
        /// yellow caret and the blue sung accent. Worn both while the glyph shimmers and after the
        /// player has filled it in, so a finished line still shows which chars were free.
        /// </summary>
        public static Color4 FreestyleChar { get; } = new Color4((byte)199, (byte)146, (byte)234, (byte)255); // #c792ea

        /// <summary>Near-opaque black drop shadow applied to gameplay text so glyphs stay legible
        /// over a beatmap background image or video (not just the flat serika-dark panel).</summary>
        public static Color4 TextShadow { get; } = new Color4((byte)0, (byte)0, (byte)0, (byte)200);

        /// <summary>Shadow offset (font-relative) paired with <see cref="TextShadow"/>.</summary>
        public static readonly Vector2 TEXT_SHADOW_OFFSET = new Vector2(0f, 0.08f);

        public const double CARET_DAMP_HALF_TIME = 35;   // ms, player caret
        public const double SUNG_DAMP_HALF_TIME = 45;    // ms, sung caret
        public const double CARET_BLINK_PERIOD = 530;    // ms
        public const double LINE_SCROLL_DURATION = 220;  // ms, Easing.OutQuint
        public const double SCREEN_FADE_DURATION = 300;  // ms
        public const float LYRIC_FONT_SIZE = 42;

        /// <summary>
        /// type!beat's default font with a fixed per-glyph advance, used for readouts (HUD
        /// numbers, editor panels) where digits must not jitter as values change.
        /// </summary>
        public static FontUsage Mono(float size) => OsuFont.Default.With(size: size, fixedWidth: true);

        /// <summary>
        /// Lyric text: the default font at its natural (proportional) advances. The line layout
        /// measures every glyph individually, so caret/sweep math does not assume a constant
        /// advance (see <see cref="LyricLineDisplay"/>).
        /// </summary>
        public static FontUsage Lyric(float size) => OsuFont.Default.With(size: size);

        /// <summary>
        /// Lyric text in a specific font <paramref name="family"/> (an accessibility pick such as
        /// OpenDyslexic or a system font registered via <c>LyricFontManager</c>). A null/empty family
        /// keeps the built-in lyric font. The family is used with no weight suffix, matching how the
        /// runtime glyph store registers itself.
        /// </summary>
        public static FontUsage Lyric(float size, string? family)
            => string.IsNullOrEmpty(family) ? Lyric(size) : new FontUsage(family, size);
    }
}
