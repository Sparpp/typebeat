// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;

namespace typebeat.Game.Configuration.Presets
{
    public enum ShareCodeError
    {
        None,

        /// <summary>Nothing was pasted.</summary>
        Empty,

        /// <summary>Something other than letters and digits.</summary>
        InvalidCharacters,

        /// <summary>The right alphabet, but not a code: mistyped, cut short, or not a share code at all.</summary>
        Invalid,

        /// <summary>A well-formed code whose schema version this build does not know yet.</summary>
        NewerVersion,
    }

    /// <summary>
    /// Gameplay preset share codes (backlog 484), in the spirit of Valorant's crosshair codes: one short,
    /// pasteable string that IS the configuration, not a reference to one stored somewhere.
    ///
    /// <para>It is not a hash. Every covered setting is a digit in its own radix (its
    /// <see cref="PresetDomain"/>), and the digits make ONE integer by mixed-radix encoding, the first
    /// schema entry least significant: <c>N = d0 + r0 * (d1 + r1 * (d2 + ...))</c>. That is a bijection
    /// between configurations and the integers below <see cref="PresetSchema.Capacity"/>, the tightest
    /// packing there is (no bit is spent on a value no setting can hold, unlike bit packing, which rounds
    /// every radix up to a power of two), so two configurations can never share a code and every code
    /// decodes back to exactly the configuration it came from.</para>
    ///
    /// <para>The layout, all in Base62 (<see cref="ALPHABET"/>, case-sensitive, nothing else, so a code
    /// is one "word" a double-click selects whole in any chat):</para>
    /// <list type="number">
    /// <item>One VERSION character: the <see cref="PresetSchema.Version"/> the digits are laid out by, 1 to
    /// 61. '0' is reserved for a longer version field a later build may need; this one reads it as newer.</item>
    /// <item>The PAYLOAD: N in exactly <see cref="PresetSchema.PayloadLength"/> characters, most significant
    /// first, zero-padded, so a version's codes all have one length and a truncated paste is caught.</item>
    /// <item>One CHECK character over the two above (Luhn mod 62). It costs one character per code, and
    /// buys rejecting every single mistyped or misread character and almost every swap of two
    /// neighbours, which would otherwise mostly decode as some OTHER valid configuration and be applied
    /// silently. A random string passes it one time in 62. Its algorithm is part of the format and must
    /// never change, since it is what lets an old build tell a newer code from garbage.</item>
    /// </list>
    ///
    /// <para>Schema version 1's codes are 33 characters: 1 + 31 + 1. Its 44 settings hold about 180 bits,
    /// 48 of them the two timing tint colours, and 31 Base62 characters are the fewest that hold them.</para>
    /// </summary>
    public static class GameplayShareCode
    {
        public const string ALPHABET = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

        private const int base_62 = 62;

        /// <summary>The highest version one version character can carry.</summary>
        public const int MAX_VERSION = base_62 - 1;

        /// <summary>How many Base62 characters it takes to write <paramref name="value"/> (at least one).</summary>
        public static int DigitsFor(BigInteger value)
        {
            int digits = 1;

            for (var limit = new BigInteger(base_62); limit <= value; limit *= base_62)
                digits++;

            return digits;
        }

        /// <summary>The code for <paramref name="digits"/>, one per entry of <paramref name="schema"/> in its order.</summary>
        public static string Encode(PresetSchema schema, IReadOnlyList<int> digits)
        {
            if (schema.Version > MAX_VERSION)
                throw new InvalidOperationException($"Schema version {schema.Version} does not fit one version character; extend the version field first (see {nameof(GameplayShareCode)}).");

            if (digits.Count != schema.Entries.Count)
                throw new ArgumentException($"Schema version {schema.Version} has {schema.Entries.Count} settings, but {digits.Count} digits were given.", nameof(digits));

            BigInteger number = BigInteger.Zero;

            for (int i = digits.Count - 1; i >= 0; i--)
            {
                int radix = schema.Entries[i].Domain.Radix;

                if (digits[i] < 0 || digits[i] >= radix)
                    throw new ArgumentOutOfRangeException(nameof(digits), digits[i], $"{schema.Entries[i].Key} has {radix} values.");

                number = number * radix + digits[i];
            }

            var body = new StringBuilder(schema.PayloadLength + 2);
            body.Append(ALPHABET[schema.Version]);
            body.Append(toBase62(number, schema.PayloadLength));
            body.Append(CheckCharacter(body.ToString()));
            return body.ToString();
        }

        /// <summary>
        /// Reads a pasted code back into its schema (from <paramref name="schemas"/>, by the code's own
        /// version) and that schema's digits. Whitespace anywhere is ignored, and so are quotes or
        /// backticks around the code, since chat clients add both.
        /// </summary>
        public static ShareCodeError TryDecode(string? text, IReadOnlyList<PresetSchema> schemas, out PresetSchema? schema, out int[] digits)
        {
            schema = null;
            digits = Array.Empty<int>();

            string code = Normalise(text);

            if (code.Length == 0)
                return ShareCodeError.Empty;

            if (code.Any(c => ALPHABET.IndexOf(c) < 0))
                return ShareCodeError.InvalidCharacters;

            if (code.Length < 3 || CheckCharacter(code[..^1]) != code[^1])
                return ShareCodeError.Invalid;

            int version = ALPHABET.IndexOf(code[0]);
            var found = schemas.FirstOrDefault(s => s.Version == version);

            if (found == null)
                return version == 0 || version > schemas.Select(s => s.Version).DefaultIfEmpty(0).Max() ? ShareCodeError.NewerVersion : ShareCodeError.Invalid;

            string payload = code[1..^1];

            if (payload.Length != found.PayloadLength)
                return ShareCodeError.Invalid;

            BigInteger number = BigInteger.Zero;

            foreach (char c in payload)
                number = number * base_62 + ALPHABET.IndexOf(c);

            if (number >= found.Capacity)
                return ShareCodeError.Invalid;

            digits = new int[found.Entries.Count];

            for (int i = 0; i < digits.Length; i++)
            {
                int radix = found.Entries[i].Domain.Radix;
                digits[i] = (int)(number % radix);
                number /= radix;
            }

            schema = found;
            return ShareCodeError.None;
        }

        /// <summary>What <see cref="TryDecode"/> reads of <paramref name="text"/>: no whitespace, no wrapping quotes or backticks.</summary>
        public static string Normalise(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            string compact = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
            return compact.Trim('"', '\'', '`');
        }

        private static string toBase62(BigInteger number, int length)
        {
            char[] chars = new char[length];

            for (int i = length - 1; i >= 0; i--)
            {
                chars[i] = ALPHABET[(int)(number % base_62)];
                number /= base_62;
            }

            if (number != 0)
                throw new InvalidOperationException("The payload does not fit its schema's length.");

            return new string(chars);
        }

        // Luhn mod N for N = 62: every second character from the right is doubled, and the doubled value's
        // two base-62 digits are summed. Doubling then summing is a permutation of 0..61, which is what
        // makes every single-character change move the sum.
        internal static char CheckCharacter(string body)
        {
            int factor = 2;
            int sum = 0;

            for (int i = body.Length - 1; i >= 0; i--)
            {
                int addend = factor * ALPHABET.IndexOf(body[i]);
                factor = factor == 2 ? 1 : 2;
                sum += addend / base_62 + addend % base_62;
            }

            return ALPHABET[(base_62 - sum % base_62) % base_62];
        }
    }
}
