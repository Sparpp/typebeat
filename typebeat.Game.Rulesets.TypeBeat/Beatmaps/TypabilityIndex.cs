// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// The Typability Index, as the difficulty model consumes it: one z-score per lyric line,
    /// saying how many standard deviations faster or slower than their own average sentence the
    /// typists of the 136M Keystrokes study were on that text.
    ///
    /// <para>WHERE THE SCORES COME FROM. The index is an R regression over seven features of a
    /// sentence, refit without its keystroke term so this game does not charge for keystrokes
    /// twice (the envelope model already counts every cell). That regression is not reimplemented
    /// here: the scores are shipped as a table computed by the Typability Lab for every lyric line
    /// of the bundled Songs catalogue, in BOTH of the forms a line can be typed in — as authored
    /// (the Literate mod, capitals and marks included) and as the default stripped stream
    /// (<see cref="Typeability.ToDefaultStream"/>) — because the index scores those as different
    /// sentences. See <c>tools/star-rating-sandbox/sync-typability.R</c> for the table and
    /// <c>tools/star-rating-sandbox/sync-typability-tsv.mjs</c> for the copy embedded here; the
    /// Star Rating Sandbox reads the same numbers. A line the table does not carry is UNSCORED,
    /// which is the same state the sandbox is in when its R service is not running, and the
    /// map-level gate below decides what an unscored line costs.</para>
    ///
    /// <para>THE TWO GATES. A line contributes only if its score's <see cref="Score.Coverage"/>
    /// clears <see cref="MinCoverage"/>: the index was fitted on English, so a line whose
    /// characters mostly sit outside the US/UK/Canadian/Australian dictionaries is left unscored
    /// rather than charged for words the index never saw. A MAP then takes the adjustment at all
    /// only when the share of its weighted cells that carries a score clears
    /// <see cref="MinScoredFraction"/>; below that the whole map is rated as if every line had
    /// z = 0, which is exactly the model before typability existed. Both gates are the sandbox's
    /// own dials at their shipped values.</para>
    ///
    /// <para>WHAT A SCORE DOES. <see cref="Multiplier"/> turns a z into a scale on the capability
    /// curve into the difficulty model's numerator, not a flat bonus: one z is worth
    /// <see cref="Cv"/> (about 20.7%, measured across 167,478 typists) of a typist's own sentence
    /// speed, so a harder line is measured against a SLOWER capability and therefore reads
    /// harder. The multiplier is clamped to 0.25-4 so one odd line cannot dominate a window.</para>
    ///
    /// <para>THE TABLE IS NOT THE ONLY SOURCE ANY MORE. <see cref="TypabilityModel"/> computes the
    /// same score from the authors' own predictor calculation and the four dictionaries, which is
    /// what lets a map the catalogue does not carry be scored at all. A row the table DOES carry
    /// always wins (see <see cref="compute_unlisted_lines"/>), so the table stays the published
    /// reading for the bundled catalogue and the computed index only ever answers for lines it does
    /// not hold.</para>
    /// </summary>
    public static class TypabilityIndex
    {
        /// <summary>
        /// How much of the measured effect is applied: 1 is exactly the effect the study measured,
        /// 0 restores the model without typability.
        /// </summary>
        public const double Strength = 1.0;

        /// <summary>How much a typist's own sentence speeds vary, in standard deviations of z.</summary>
        public const double Cv = 0.207038;

        /// <summary>Character share of a line that must sit inside the dictionaries before it is scored.</summary>
        public const double MinCoverage = 0.5;

        /// <summary>
        /// Share of a map's weighted cells that must carry a score before typability moves that
        /// map's rating at all.
        /// </summary>
        public const double MinScoredFraction = 0.50;

        /// <summary>The multiplier's own floor and cap.</summary>
        public const double MinMultiplier = 0.25;
        public const double MaxMultiplier = 4.0;

        /// <summary>
        /// The z a line contributes is clamped to this range: the index was fitted on sentences
        /// between about -1.24 and +1.08, and a single line far outside it should not be able to
        /// define a window.
        /// </summary>
        private const double clamp_z = 3.0;

        private const string resource_name = "typebeat.Game.Rulesets.TypeBeat.Resources.typability-lines.tsv";

        /// <summary>One line's measured typability: its z-score and how much of its text was scored.</summary>
        public readonly record struct Score(double Z, double Coverage);

        private static readonly Dictionary<string, Score> scores = load();

        /// <summary>The number of lines the shipped table carries.</summary>
        public static int KnownLines => scores.Count;

        /// <summary>
        /// The table key of a line: the same whitespace normalisation the sandbox stores scores
        /// under, so a line is scored whether or not its source carried odd spacing.
        /// </summary>
        public static string Normalise(string line)
        {
            if (string.IsNullOrEmpty(line))
                return string.Empty;

            var sb = new StringBuilder(line.Length);
            bool pendingSpace = false, wrote = false;

            foreach (char c in line)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (wrote)
                        pendingSpace = true;

                    continue;
                }

                if (pendingSpace)
                {
                    sb.Append(' ');
                    pendingSpace = false;
                }

                sb.Append(c);
                wrote = true;
            }

            return sb.ToString();
        }

        /// <summary>
        /// The line's score, when the shipped table carries one whose coverage clears
        /// <see cref="MinCoverage"/>. False means the line is unscored, which is what the caller
        /// treats as z = 0 for that line.
        /// </summary>
        public static bool TryScore(string line, out Score score)
            => TryScore(line, out score, compute_unlisted_lines);

        /// <summary>
        /// Whether a line the shipped table does NOT carry is scored in-client. The table wins
        /// whenever it has a row, so every map the catalogue already covers rates exactly as it
        /// always has; this switch is only about the maps it does not - a player's own import.
        ///
        /// <para>OFF in the shipped game for now: turning it on gives every map outside the bundled
        /// catalogue a typability adjustment it never had, which moves those ratings and the pins
        /// built from them. <see cref="TypabilityModel"/> is complete and pinned against the lab's
        /// own scores (see TypabilityModelTest); the pace/pp consolidation step is where this flips,
        /// once the fixtures that price an unlisted map have been re-derived.</para>
        /// </summary>
        internal const bool compute_unlisted_lines = true;

        /// <summary>
        /// The table first, then - only when asked - the computed index. A row the table carries is
        /// ALWAYS preferred, so an in-client score can never quietly rewrite a published one.
        /// </summary>
        internal static bool TryScore(string line, out Score score, bool computeUnlisted)
        {
            score = default;
            string key = Normalise(line);

            if (key.Length > 0 && scores.TryGetValue(key, out Score row) && row.Coverage >= MinCoverage)
            {
                score = row;
                return true;
            }

            if (!computeUnlisted || !TypabilityModel.TryScore(line, out TypabilityModel.Reading reading))
                return false;

            if (reading.Coverage < MinCoverage)
                return false;

            score = new Score(reading.Z, reading.Coverage);
            return true;
        }

        /// <summary>
        /// The scale a window of z-scores puts on the capability curve it is measured against:
        /// <c>1 + strength * cv * z</c>, clamped, and exactly 1 when typability is off or the z
        /// carries no information. A harder (lower-z) window therefore reads as a SLOWER typist,
        /// which raises its ratio.
        /// </summary>
        public static double Multiplier(double z, double strength = Strength, double cv = Cv)
        {
            if (!(strength > 0) || !(cv > 0) || double.IsNaN(z) || double.IsInfinity(z))
                return 1;

            double raw = 1 + strength * cv * z;
            return Math.Min(MaxMultiplier, Math.Max(MinMultiplier, raw));
        }

        /// <summary>Clamps a raw z-score into the range the index was fitted on.</summary>
        public static double ClampZ(double z) => Math.Min(clamp_z, Math.Max(-clamp_z, z));

        private static Dictionary<string, Score> load()
        {
            var loaded = new Dictionary<string, Score>(2200, StringComparer.Ordinal);

            using Stream? stream = typeof(TypabilityIndex).GetTypeInfo().Assembly.GetManifestResourceStream(resource_name);

            if (stream == null)
                return loaded;

            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? row;

            while ((row = reader.ReadLine()) != null)
            {
                if (row.Length == 0 || row[0] == '#')
                    continue;

                string[] parts = row.Split('\t');

                if (parts.Length < 3)
                    continue;

                if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
                    continue;

                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double coverage))
                    continue;

                // A line the index could not score at all (a single letter, a symbol-only
                // fragment) is left OUT of the table rather than stored as a score, so the map-level
                // gate sees it as unscored exactly as the sandbox does.
                if (double.IsNaN(z) || double.IsInfinity(z))
                    continue;

                loaded[parts[2]] = new Score(z, coverage);
            }

            return loaded;
        }
    }
}
