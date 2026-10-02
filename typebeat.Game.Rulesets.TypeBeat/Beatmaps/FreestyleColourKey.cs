// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Globalization;
using osu.Framework.Graphics;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.UI;
using osuTK.Graphics;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// The mapper-chosen FREESTYLE character colour (backlog 384): one optional <c>[General]</c> key,
    /// <c>FreestyleColour: #rrggbb</c>, carried on <see cref="IBeatmap.FreestyleColour"/>.
    ///
    /// <para>ABSENCE is the default (<see cref="TypeBeatStyle.FreestyleChar"/>), so every map written
    /// before the key existed decodes, plays and re-encodes exactly as it did, and an older client
    /// skips the unknown key (the inherited <c>[General]</c> parse ignores keys it does not know).
    /// The writer therefore emits the key only for a colour that differs from the default.</para>
    ///
    /// <para>The value is strict: a <c>#</c> and exactly six hex digits, either case, opaque.
    /// Anything else reads as absent rather than failing the load, the same reading the browser
    /// player's decoder takes, so the two clients cannot disagree about a malformed value.</para>
    /// </summary>
    public static class FreestyleColourKey
    {
        /// <summary>The <c>[General]</c> key name, spelled identically by every reader and writer.</summary>
        public const string KEY = "FreestyleColour";

        /// <summary>The colour an absent key resolves to.</summary>
        public static Color4 Default => TypeBeatStyle.FreestyleChar;

        /// <summary>
        /// The stored value, or null when <paramref name="value"/> is not exactly <c>#</c> plus six
        /// hex digits (surrounding whitespace aside).
        /// </summary>
        public static Colour4? Parse(string? value)
        {
            string text = value?.Trim() ?? string.Empty;

            if (text.Length != 7 || text[0] != '#')
                return null;

            for (int i = 1; i < 7; i++)
            {
                if (!Uri.IsHexDigit(text[i]))
                    return null;
            }

            int rgb = int.Parse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return new Colour4((byte)(rgb >> 16), (byte)((rgb >> 8) & 0xff), (byte)(rgb & 0xff), (byte)255);
        }

        /// <summary>The stored spelling of <paramref name="colour"/>: <c>#rrggbb</c>, lowercase, alpha dropped.</summary>
        public static string Format(Colour4 colour)
            => FormattableString.Invariant($"#{toByte(colour.R):x2}{toByte(colour.G):x2}{toByte(colour.B):x2}");

        /// <summary>
        /// What a map should CARRY for a picked colour: null (no key) for no colour or for one that
        /// spells the same as the default, otherwise the colour made opaque and snapped to what the
        /// stored spelling can hold, so the value in memory is the value a reload produces.
        /// </summary>
        public static Colour4? Normalise(Colour4? colour)
        {
            if (colour is not Colour4 c)
                return null;

            string spelled = Format(c);
            return spelled == Format(Default) ? null : Parse(spelled);
        }

        /// <summary>The line the writer emits for <paramref name="colour"/>, or null when it writes none.</summary>
        public static string? LineFor(Colour4? colour)
            => Normalise(colour) is Colour4 c ? $"{KEY}: {Format(c)}" : null;

        /// <summary>The colour freestyle characters render in for a map carrying <paramref name="colour"/>.</summary>
        public static Color4 Resolve(Colour4? colour) => colour ?? (Colour4)Default;

        private static int toByte(float channel) => (int)Math.Round(Math.Clamp(channel, 0, 1) * 255);
    }
}
