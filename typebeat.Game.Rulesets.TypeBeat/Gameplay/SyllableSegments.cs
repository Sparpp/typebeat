// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    /// <summary>
    /// WHERE a subdivided word's characters are cut into syllable segments: the ONE derivation
    /// gameplay (<see cref="TypingLine"/>) and the editor both read, so the split a mapper sees on
    /// the timeline strip ("ap|ple") is exactly the split the judgement groups and the per-char
    /// targets are built from.
    ///
    /// <para>A word carrying N <see cref="TimedUnit.SyllableBoundaries"/> has N + 1 segments and
    /// therefore N character splits. They come from one of two places: the mapper AUTHORED them
    /// (<see cref="TimedUnit.SyllableSplits"/>) or they are DERIVED by the <see cref="Syllabifier"/>
    /// forced to the boundary count. Authored wins, but only while it is still VALID for the word it
    /// is attached to (<see cref="IsAuthoredValid"/>); anything else silently falls back to derived
    /// rather than throwing, because a split is a CHAR INDEX and every edit that retypes a word or
    /// changes its boundary count can invalidate one.</para>
    ///
    /// <para>Splits index the TOKEN string (punctuation included, exactly like
    /// <see cref="Syllabifier.SplitPoints"/>), never the typed cell stream. The conversion to cell
    /// space, which is what the target spread needs, is <see cref="CellCuts"/>.</para>
    /// </summary>
    public static class SyllableSegments
    {
        /// <summary>
        /// Whether <paramref name="authored"/> is a usable char split of <paramref name="token"/>
        /// into <paramref name="segments"/> segments: exactly <c>segments - 1</c> indices, strictly
        /// ascending, every one strictly inside <c>(0, token.Length)</c> so no segment is empty.
        /// An empty list is "derived", not "valid", and returns false (the caller falls back).
        /// </summary>
        public static bool IsAuthoredValid(string token, int segments, IReadOnlyList<int>? authored)
        {
            if (authored == null || authored.Count == 0 || string.IsNullOrEmpty(token))
                return false;

            if (segments < 2 || authored.Count != segments - 1)
                return false;

            int previous = 0;

            foreach (int split in authored)
            {
                if (split <= previous || split >= token.Length)
                    return false;

                previous = split;
            }

            return true;
        }

        /// <summary>
        /// The split the <see cref="Syllabifier"/> picks for a word forced to
        /// <paramref name="segments"/> segments. Can return FEWER than <c>segments - 1</c> indices
        /// on an over-forced short word (the syllabifier degrades rather than inventing splits);
        /// <see cref="TypingLine"/> has always tolerated that, so callers must too.
        /// </summary>
        public static IReadOnlyList<int> Derived(string token, int segments)
            => segments < 2 || string.IsNullOrEmpty(token) ? Array.Empty<int>() : Syllabifier.SplitPoints(token, segments);

        /// <summary>
        /// The EFFECTIVE split: <paramref name="authored"/> when it is valid, the derived split
        /// otherwise. This is the function every reader goes through.
        /// </summary>
        public static IReadOnlyList<int> SplitsFor(string token, int segments, IReadOnlyList<int>? authored)
            => IsAuthoredValid(token, segments, authored) ? authored! : Derived(token, segments);

        /// <summary>The effective split of a word unit (segment count read off its boundaries).</summary>
        public static IReadOnlyList<int> SplitsFor(TimedUnit unit)
            => SplitsFor(unit.Text, unit.SyllableBoundaries.Count + 1, unit.SyllableSplits);

        /// <summary>
        /// The word's characters cut at <paramref name="splits"/>: one string per segment, in order,
        /// concatenating back to <paramref name="token"/>. Used by the timeline strip to print "ap"
        /// left of a dotted line and "ple" right of it, and by the encoder for the cosmetic
        /// per-syllable text.
        /// </summary>
        public static IReadOnlyList<string> SegmentTexts(string token, IReadOnlyList<int> splits)
        {
            token ??= string.Empty;
            var result = new List<string>(splits.Count + 1);
            int from = 0;

            for (int i = 0; i <= splits.Count; i++)
            {
                int to = i < splits.Count ? Math.Clamp(splits[i], from, token.Length) : token.Length;
                result.Add(token.Substring(from, to - from));
                from = to;
            }

            return result;
        }

        /// <summary>
        /// The split expressed in CELL space: <c>cuts[s]</c> is the number of
        /// <see cref="Typeability.IsCell"/> characters of <paramref name="token"/> before segment
        /// <c>s</c> starts, so <c>cuts[0] == 0</c> and <c>cuts[^1] == k</c> (the word's typeable
        /// char count). Length is <c>splits.Count + 2</c>.
        ///
        /// <para>This is the bridge the target spread needs: it counts what
        /// <see cref="TypingLine.FromLyricLine"/> counts, so punctuation (which is timed by
        /// interpolation, never by the per-word spread) rides inside whichever segment surrounds
        /// it without spending a slot.</para>
        /// </summary>
        public static int[] CellCuts(string token, IReadOnlyList<int> splits)
        {
            token ??= string.Empty;
            int[] cuts = new int[splits.Count + 2];
            int cells = 0;
            int next = 0;

            for (int i = 0; i < token.Length; i++)
            {
                while (next < splits.Count && splits[next] == i)
                    cuts[++next] = cells;

                if (Typeability.IsCell(token[i]))
                    cells++;
            }

            while (next < splits.Count)
                cuts[++next] = cells;

            cuts[^1] = cells;
            return cuts;
        }

        /// <summary>
        /// The segment holding typeable-char index <paramref name="j"/>: the LAST segment whose cut
        /// is at or before it. Taking the last (rather than the first) is what makes a segment with
        /// no typeable char of its own, a lone hyphen between two letters, transparent in exactly
        /// the way <see cref="TypingLine"/>'s group assignment already makes it.
        /// </summary>
        public static int SegmentOf(IReadOnlyList<int> cuts, int j)
        {
            int segments = cuts.Count - 1;
            int s = 0;

            for (int i = 1; i < segments; i++)
            {
                if (cuts[i] <= j)
                    s = i;
                else
                    break;
            }

            return s;
        }
    }
}
