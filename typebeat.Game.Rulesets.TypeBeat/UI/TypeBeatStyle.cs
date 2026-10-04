// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Ported from type!beat TypeBeat.Game/TypeBeatStyle.cs. Colour fields became get-only
// properties (fork naming rules make public static readonly fields ALL_UPPER); the
// font factory now builds on type!beat's default font with a fixed per-glyph advance.

using System;
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

        /// <summary>Default sung-syllable highlight, 50% of the way from untyped grey to typed white.</summary>
        public static Color4 SungChar { get; } = SungCharForBrightness(50f);

        /// <summary>Blend untyped grey towards typed white by a player-selected percentage.</summary>
        public static Color4 SungCharForBrightness(float percent)
        {
            float amount = Math.Clamp(percent, 0f, 100f) / 100f;
            return new Color4(
                (byte)Math.Round(100 + (209 - 100) * amount),
                (byte)Math.Round(102 + (208 - 102) * amount),
                (byte)Math.Round(105 + (197 - 105) * amount),
                (byte)255);
        }

        /// <summary>
        /// FREESTYLE characters (the mapper's '&amp;' slots, where any key but space is accepted): a bright
        /// violet that reads clearly on the dark playfield and is unmistakable against every other
        /// character state, untyped grey #646669, the sung highlight grey #7e8083, typed off-white
        /// #d1d0c5, error red #ca4754, the yellow caret and the blue sung accent. Worn both while the glyph shimmers and after the
        /// player has filled it in, so a finished line still shows which chars were free.
        /// </summary>
        public static Color4 FreestyleChar { get; } = new Color4((byte)199, (byte)146, (byte)234, (byte)255); // #c792ea

        /// <summary>
        /// The SLOW end of the underline's PACE HUE (backlog 228, see
        /// <see cref="UnderlinePace"/>): the colour a word segment slower than its predecessor
        /// shades towards. The fast end deliberately reuses <see cref="ErrorChar"/> rather
        /// than adding a second red, so the palette gains exactly one colour for this feature.
        ///
        /// <para>#6ed26e is a clean medium green picked on ONE constraint above all others: it must
        /// weigh the same as <see cref="SungAccent"/> (#7ec8e3), the rail's own colour. The hex was
        /// chosen on contrast, not by eye (WCAG relative luminance, sRGB):</para>
        /// <list type="bullet">
        /// <item>1.01:1 against <see cref="SungAccent"/>, which is as close to no luminance step as
        /// two different hues get. That is the requirement and not a compromise: the rail must not
        /// change WEIGHT as it hues, or a slow passage would read as a brighter underline rather
        /// than a greener one, and the whole point of the feature is that grey stays the default.
        /// The reading is carried by hue instead, and by a large step of it: blue drops 227 to 110
        /// (a 52% cut) while green rises 200 to 210, which turns the rail's cyan into an unambiguous
        /// green. The gentle alpha lift the ramp applies towards either end (see
        /// <see cref="UnderlinePace.NEUTRAL_ALPHA"/> and <see cref="UnderlinePace.HUED_ALPHA"/>) is
        /// what makes that hue legible at a fifth alpha without making it loud.</item>
        /// <item>2.45:1 against <see cref="ErrorChar"/>, the fast end, so the two ends of the ramp
        /// can never be confused with each other. They are opposite in hue as well as separated in
        /// luminance, which is the reading the feature is entirely about.</item>
        /// <item>Well clear of <see cref="Caret"/>'s yellow and <see cref="FreestyleChar"/>'s violet
        /// in hue, and it is drawn on the rail UNDER the glyphs at a fifth to a third alpha, so it
        /// competes with no character state: nothing on the character row is ever this colour.</item>
        /// </list>
        /// </summary>
        public static Color4 PaceSlowAccent { get; } = new Color4((byte)110, (byte)210, (byte)110, (byte)255); // #6ed26e

        /// <summary>
        /// The DEFAULT colour of the EARLY end of the timing tint (see
        /// <see cref="LyricLineDisplay.TimingTintColour"/>; players can pick their own through
        /// <see cref="Configuration.TypeBeatRulesetSetting.TimingTintEarlyColour"/>). Warm for early:
        /// rushing reads as burning, and it matches the pace underline, whose FAST end is the warm one
        /// (<see cref="ErrorChar"/>) and whose slow end is cool (<see cref="PaceSlowAccent"/>). An
        /// orange well clear of <see cref="Caret"/>'s yellow and of <see cref="ErrorChar"/>'s red, so an
        /// early char never reads as the caret or as a typo. Orange/blue rather than red/green so the
        /// pair survives the common red-green colour blindnesses.
        /// </summary>
        public static Color4 EarlyTint { get; } = new Color4((byte)255, (byte)138, (byte)61, (byte)255); // #ff8a3d

        /// <summary>
        /// The DEFAULT colour of the LATE end of the timing tint, the counterpart of
        /// <see cref="EarlyTint"/> (player setting:
        /// <see cref="Configuration.TypeBeatRulesetSetting.TimingTintLateColour"/>). Cool for late:
        /// dragging reads as frozen. A purer, deeper blue than <see cref="SungAccent"/>'s cyan, so a
        /// tinted trail does not read as the sung highlight.
        /// </summary>
        public static Color4 LateTint { get; } = new Color4((byte)77, (byte)139, (byte)255, (byte)255); // #4d8bff

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
