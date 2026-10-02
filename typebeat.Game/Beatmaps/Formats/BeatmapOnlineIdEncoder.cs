// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace typebeat.Game.Beatmaps.Formats
{
    /// <summary>
    /// Synchronises online IDs without decoding or rewriting the beatmap's gameplay data.
    /// </summary>
    public static class BeatmapOnlineIdEncoder
    {
        private static readonly Regex metadata_section = new Regex(@"(^\[Metadata\][ \t]*\r?\n)(.*?)(?=^\[|\z)",
            RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled);

        public static string Encode(string contents, int beatmapId, int beatmapSetId)
        {
            var metadata = metadata_section.Match(contents);

            if (!metadata.Success)
                throw new InvalidDataException("The beatmap has no [Metadata] section to write its online IDs into.");

            string fields = metadata.Groups[2].Value;
            string newline = metadata.Groups[1].Value.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            fields = writeId(fields, "BeatmapID", beatmapId > 0 ? beatmapId : 0, newline);
            fields = writeId(fields, "BeatmapSetID", beatmapSetId > 0 ? beatmapSetId : -1, newline);

            return contents.Remove(metadata.Index, metadata.Length).Insert(metadata.Index, metadata.Groups[1].Value + fields);
        }

        private static string writeId(string fields, string key, int id, string newline)
        {
            var pattern = new Regex(@"^[ \t]*" + key + @"[ \t]*:[^\r\n]*", RegexOptions.Multiline);
            string value = id.ToString(CultureInfo.InvariantCulture);

            if (!pattern.IsMatch(fields))
                return key + ":" + value + newline + fields;

            return pattern.Replace(fields, match =>
            {
                // Keep already-correct fields byte-identical, including their whitespace.
                string storedValue = match.Value.Substring(match.Value.IndexOf(':') + 1).Trim();
                return storedValue == value ? match.Value : key + ":" + value;
            });
        }
    }
}
