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
        /// An UNTYPED character of the syllable the vocals are on right now (the sung-group
        /// highlight, backlog 174 stage 3). Not a monkeytype palette colour: the palette has one
        /// untyped grey and one typed off-white, and this state is a third thing, "not typed yet,
        /// but sing it NOW", so it takes a grey of its own BETWEEN the two.
        ///
        /// <para>#7e8083 is the untyped grey #646669 lifted 26 points on every channel, so it stays
        /// the same faintly cool grey rather than drifting warm the way a step along the sync ramp
        /// would. Warmth is exactly what this colour must not borrow: the character has not been
        /// typed, and the palette spends its warmth on characters that have.</para>
        ///
        /// <para>The hex was chosen on contrast, not by eye (WCAG relative luminance, sRGB):</para>
        /// <list type="bullet">
        /// <item>1.45:1 against <see cref="UntypedChar"/>, so "the song is here" reads at a glance.
        /// The yardstick is the untyped-versus-Missed step the game already ships and asks players
        /// to read, 1.47:1, which this effectively matches.</item>
        /// <item>2.55:1 against <see cref="TypedChar"/>, so a sung-but-untyped character can never
        /// be mistaken for one the player has already typed. That is the demotion backlog 178 asked
        /// for: the highlight used to BE <see cref="TypedChar"/>.</item>
        /// <item>1.33:1 below the sync ramp's floor, roughly #969692 (see
        /// <c>LyricLineDisplay.SYNC_TINT_FLOOR</c>), and darker on every channel, so the worst
        /// correct character stays brighter than the highlight rather than colliding with it. That
        /// step is the tightest of the three because the band between the untyped grey and the ramp
        /// floor is only 1.94:1 wide in total, so no colour in it can clear both ends by more than
        /// about 1.39:1; the split is deliberately weighted towards the untyped end, which is the
        /// read the feature exists for. What is left over is reinforced by hue (this grey is cool,
        /// the ramp floor warm) and by position: everything behind the caret is typed and everything
        /// ahead of it is not, so a ramp-floor character and a highlighted one are not neighbours.</item>
        /// </list>
        /// </summary>
        public static Color4 SungChar { get; } = new Color4((byte)126, (byte)128, (byte)131, (byte)255); // #7e8083

        /// <summary>
        /// FREESTYLE characters (the mapper's '&amp;' slots, where any key but space is accepted): a bright
        /// violet that reads clearly on the dark playfield and is unmistakable against every other
        /// character state, untyped grey #646669, the sung highlight grey #7e8083, typed off-white
        /// #d1d0c5, error red #ca4754, the yellow caret and the blue sung accent. Worn both while the glyph shimmers and after the
        /// player has filled it in, so a finished line still shows which chars were free.
        /// </summary>
        public static Color4 FreestyleChar { get; } = new Color4((byte)199, (byte)146, (byte)234, (byte)255); // #c792ea

        /// <summary>
        /// The RETYPE SELECTION wash (backlog 182): the block a Ctrl+A paints behind the characters
        /// it has offered to erase and retype. The caret's own yellow at 22% alpha, so the highlight
        /// reads as "the caret is holding this run" rather than as a sixth character state, and so it
        /// cannot be confused with the error red (which says a cell IS wrong) or with the blue sung
        /// accent (which says where the song is). Drawn BEHIND the glyphs at a low enough alpha that
        /// every character state above it, the untyped grey included, keeps its own contrast against
        /// the serika-dark panel.
        /// </summary>
        public static Color4 Selection { get; } = new Color4((byte)226, (byte)183, (byte)20, (byte)56); // #e2b714 at 22%

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
