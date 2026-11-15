// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace typebeat.Game.Configuration.Presets
{
    public enum PresetDomainKind
    {
        Bool,
        Enum,
        Range,
        Colour,
    }

    /// <summary>
    /// The finite value space one setting covered by Gameplay presets is encoded in: every value the
    /// setting can hold is one DIGIT, an integer from 0 to <see cref="Radix"/> - 1, and a share code is
    /// the mixed-radix number those digits make (see <see cref="GameplayShareCode"/>).
    ///
    /// <list type="bullet">
    /// <item><see cref="PresetDomainKind.Bool"/>: 2 digits, false then true.</item>
    /// <item><see cref="PresetDomainKind.Enum"/>: one digit per member NAME, in the order the schema lists
    /// them. Names rather than ordinals, so an enum reordered or renumbered later still decodes old codes
    /// right, and a member removed later decodes as the setting's default.</item>
    /// <item><see cref="PresetDomainKind.Range"/>: a slider, <c>(max - min) / step + 1</c> digits. A value
    /// between two steps SNAPS to the nearest step and one outside the range clamps to it (a slider's
    /// bindable already holds every value on a step, so live values never move).</item>
    /// <item><see cref="PresetDomainKind.Colour"/>: an opaque <c>#rrggbb</c> colour, 2^24 digits (alpha is
    /// not part of it: the colour settings presets cover store none).</item>
    /// </list>
    ///
    /// <para>A digit's VALUE, as the rest of the preset code passes it around, is a plain object of one
    /// shape per kind: <see cref="bool"/>, the member name as a <see cref="string"/>, a
    /// <see cref="decimal"/> (decimal so steps like 0.01 add up exactly, and a stored 0.4 reads back as
    /// the 0.4 a bindable's default holds), or the colour as an <see cref="int"/> <c>0xRRGGBB</c>.</para>
    /// </summary>
    public sealed class PresetDomain : IEquatable<PresetDomain>
    {
        public const int COLOUR_RADIX = 1 << 24;

        public PresetDomainKind Kind { get; }

        /// <summary>The member names of an <see cref="PresetDomainKind.Enum"/> domain, in digit order. Empty otherwise.</summary>
        public IReadOnlyList<string> Names { get; }

        public decimal Min { get; }

        public decimal Max { get; }

        public decimal Step { get; }

        /// <summary>How many digits this domain has.</summary>
        public int Radix { get; }

        /// <summary>
        /// The domain as one line of a frozen schema (after the setting's key): <c>bool</c>,
        /// <c>enum A B C</c>, <c>range 0 1 0.01</c> or <c>colour</c>. Two domains are equal exactly when
        /// their specs are.
        /// </summary>
        public string Spec { get; }

        private PresetDomain(PresetDomainKind kind, IReadOnlyList<string> names, decimal min, decimal max, decimal step, int radix, string spec)
        {
            Kind = kind;
            Names = names;
            Min = min;
            Max = max;
            Step = step;
            Radix = radix;
            Spec = spec;
        }

        public static PresetDomain Bool() => new PresetDomain(PresetDomainKind.Bool, Array.Empty<string>(), 0, 0, 0, 2, "bool");

        public static PresetDomain Colour() => new PresetDomain(PresetDomainKind.Colour, Array.Empty<string>(), 0, 0, 0, COLOUR_RADIX, "colour");

        public static PresetDomain Enum(IEnumerable<string> names)
        {
            string[] list = names.ToArray();

            if (list.Length == 0)
                throw new ArgumentException("An enum domain needs at least one member.", nameof(names));

            if (list.Distinct(StringComparer.Ordinal).Count() != list.Length)
                throw new ArgumentException($"An enum domain lists a member twice: {string.Join(' ', list)}.", nameof(names));

            if (list.Any(n => n.Length == 0 || n.Any(char.IsWhiteSpace)))
                throw new ArgumentException($"Enum domain member names cannot be empty or contain spaces: {string.Join(' ', list)}.", nameof(names));

            return new PresetDomain(PresetDomainKind.Enum, list, 0, 0, 0, list.Length, "enum " + string.Join(' ', list));
        }

        public static PresetDomain Range(decimal min, decimal max, decimal step)
        {
            min = normalise(min);
            max = normalise(max);
            step = normalise(step);

            if (step <= 0)
                throw new ArgumentException($"A range domain needs a positive step, not {format(step)}. A slider without a precision has no finite domain: give its bindable one.", nameof(step));

            if (max < min)
                throw new ArgumentException($"A range domain's maximum {format(max)} is below its minimum {format(min)}.", nameof(max));

            decimal steps = decimal.Floor((max - min) / step) + 1;

            if (steps > int.MaxValue)
                throw new ArgumentException($"The range {format(min)} to {format(max)} in steps of {format(step)} has {steps} values, too many to encode. Use a coarser precision.");

            return new PresetDomain(PresetDomainKind.Range, Array.Empty<string>(), min, max, step, (int)steps, $"range {format(min)} {format(max)} {format(step)}");
        }

        /// <summary>Reads a domain back from its <see cref="Spec"/>.</summary>
        public static PresetDomain Parse(string spec)
        {
            string[] tokens = spec.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (tokens.Length == 0)
                throw new FormatException("An empty preset domain.");

            switch (tokens[0])
            {
                case "bool" when tokens.Length == 1:
                    return Bool();

                case "colour" when tokens.Length == 1:
                    return Colour();

                case "enum" when tokens.Length > 1:
                    return Enum(tokens.Skip(1));

                case "range" when tokens.Length == 4:
                    return Range(parseNumber(tokens[1]), parseNumber(tokens[2]), parseNumber(tokens[3]));

                default:
                    throw new FormatException($"Not a preset domain: \"{spec}\".");
            }
        }

        /// <summary>The value <paramref name="digit"/> stands for.</summary>
        public object ValueOf(int digit)
        {
            if (digit < 0 || digit >= Radix)
                throw new ArgumentOutOfRangeException(nameof(digit), digit, $"Outside the domain \"{Spec}\".");

            switch (Kind)
            {
                case PresetDomainKind.Bool:
                    return digit == 1;

                case PresetDomainKind.Enum:
                    return Names[digit];

                case PresetDomainKind.Range:
                    return normalise(Min + digit * Step);

                case PresetDomainKind.Colour:
                    return digit;

                default:
                    throw new InvalidOperationException();
            }
        }

        /// <summary>
        /// The digit <paramref name="value"/> encodes as, or null when it is not a value of this kind at
        /// all (another kind's value, or an enum name this domain does not list), in which case the
        /// caller falls back to the setting's default. A range value snaps to the nearest step and
        /// clamps to the range; a colour drops anything above 24 bits.
        /// </summary>
        public int? DigitOf(object? value)
        {
            switch (Kind)
            {
                case PresetDomainKind.Bool:
                    return value is bool b ? b ? 1 : 0 : null;

                case PresetDomainKind.Enum:
                    if (value is not string name)
                        return null;

                    for (int i = 0; i < Names.Count; i++)
                    {
                        if (Names[i] == name)
                            return i;
                    }

                    return null;

                case PresetDomainKind.Range:
                    if (value is not decimal number)
                        return null;

                    decimal digit = decimal.Round((number - Min) / Step, MidpointRounding.AwayFromZero);
                    return (int)Math.Clamp(digit, 0, Radix - 1);

                case PresetDomainKind.Colour:
                    return value is int rgb ? rgb & (COLOUR_RADIX - 1) : null;

                default:
                    return null;
            }
        }

        public bool Equals(PresetDomain? other) => other != null && other.Spec == Spec;

        public override bool Equals(object? obj) => obj is PresetDomain other && Equals(other);

        public override int GetHashCode() => Spec.GetHashCode(StringComparison.Ordinal);

        public override string ToString() => Spec;

        private static decimal parseNumber(string text) => decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture);

        // Drops trailing zeros (0.40 -> 0.4), so a spec spells each number one way only.
        private static decimal normalise(decimal value) => value / 1.0000000000000000000000000000m;

        private static string format(decimal value) => normalise(value).ToString(CultureInfo.InvariantCulture);
    }
}
