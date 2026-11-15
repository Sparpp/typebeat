// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace typebeat.Game.Configuration.Presets
{
    /// <summary>One setting a schema covers: its stable key and its value space.</summary>
    public sealed class PresetSchemaEntry
    {
        /// <summary>
        /// Where the setting lives and what it is called there: <c>game:&lt;OsuSetting member&gt;</c> for a
        /// game setting, <c>&lt;ruleset short name&gt;:&lt;lookup member&gt;</c> for a ruleset's. Both configs
        /// persist settings by member name, so this is exactly as stable as the stored setting itself.
        /// </summary>
        public string Key { get; }

        public PresetDomain Domain { get; }

        public PresetSchemaEntry(string key, PresetDomain domain)
        {
            if (string.IsNullOrEmpty(key) || key.Any(char.IsWhiteSpace) || key.IndexOf(':') <= 0)
                throw new ArgumentException($"\"{key}\" is not a preset key: expected <source>:<setting name>.", nameof(key));

            Key = key;
            Domain = domain;
        }

        /// <summary>The entry as one line of a frozen schema: the key, a space, then the domain's spec.</summary>
        public string Line => $"{Key} {Domain.Spec}";

        public static PresetSchemaEntry Parse(string line)
        {
            int space = line.IndexOf(' ');

            if (space <= 0)
                throw new FormatException($"Not a preset schema line: \"{line}\".");

            return new PresetSchemaEntry(line[..space], PresetDomain.Parse(line[(space + 1)..]));
        }

        public override string ToString() => Line;
    }

    /// <summary>
    /// A FROZEN description of what Settings &gt; Gameplay held when a share code version was cut: the
    /// covered settings in digit order (first entry least significant) and each one's domain. A code
    /// carries its schema's <see cref="Version"/>, and is always decoded against that schema, however
    /// the live settings have changed since (see <see cref="GameplayPresetSchemas"/>).
    /// </summary>
    public sealed class PresetSchema
    {
        public int Version { get; }

        public IReadOnlyList<PresetSchemaEntry> Entries { get; }

        /// <summary>How many distinct configurations the schema can describe: the product of every radix.</summary>
        public BigInteger Capacity { get; }

        /// <summary>
        /// How many Base62 characters every payload of this version takes: just enough for
        /// <see cref="Capacity"/> - 1. Fixed, so a code's length is part of what is checked.
        /// </summary>
        public int PayloadLength { get; }

        public PresetSchema(int version, IEnumerable<PresetSchemaEntry> entries)
        {
            if (version < 1)
                throw new ArgumentOutOfRangeException(nameof(version), version, "Schema versions start at 1.");

            Version = version;
            Entries = entries.ToArray();

            string? duplicate = Entries.GroupBy(e => e.Key, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1)?.Key;

            if (duplicate != null)
                throw new ArgumentException($"Schema version {version} lists {duplicate} twice.", nameof(entries));

            Capacity = Entries.Aggregate(BigInteger.One, (product, e) => product * e.Domain.Radix);
            PayloadLength = GameplayShareCode.DigitsFor(Capacity - 1);
        }

        /// <summary>Reads a schema from its frozen lines (<see cref="PresetSchemaEntry.Line"/>).</summary>
        public PresetSchema(int version, params string[] lines)
            : this(version, lines.Select(PresetSchemaEntry.Parse))
        {
        }

        public PresetSchemaEntry? Find(string key) => Entries.FirstOrDefault(e => e.Key == key);

        /// <summary>The schema as the C# to paste into <see cref="GameplayPresetSchemas"/>.</summary>
        public string ToSource()
        {
            var lines = new List<string> { $"new PresetSchema({Version}," };

            for (int i = 0; i < Entries.Count; i++)
                lines.Add($"    \"{Entries[i].Line}\"{(i < Entries.Count - 1 ? "," : "),")}");

            return string.Join(Environment.NewLine, lines);
        }
    }
}
