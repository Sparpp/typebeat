// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Text;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// The '|' authoring mark (<see cref="Typeability.SPLIT_MARKER"/>) as it flows through LYRIC
    /// TEXT, in the one place every reader shares. Three surfaces author with it and must read it
    /// identically or the same typed line would subdivide differently depending on where it came
    /// from: the editor's line box, LRC import, and the aligner's display text.
    ///
    /// <para>A pipe is never lyric. It is read for its POSITION and then stripped, so no stored
    /// <see cref="LyricLine.RawText"/>, no cell and no aligner input ever carries one; what it
    /// leaves behind is a <see cref="TimedUnit.SyllableBoundaries"/> / <see cref="TimedUnit.SyllableSplits"/>
    /// pair on its word.</para>
    /// </summary>
    public static class SplitMarkers
    {
        /// <summary>Whether <paramref name="text"/> carries a pipe at all, the gate every caller
        /// gets its "nothing changes for text without one" guarantee from.</summary>
        public static bool Carries(string? text) => !string.IsNullOrEmpty(text) && text.IndexOf(Typeability.SPLIT_MARKER) >= 0;

        /// <summary>
        /// Splits pipe-bearing text into the text that gets STORED and, per surviving token, the
        /// character positions the pipes sat at (measured in the stripped token, so a pipe before
        /// the first char is 0 and one after the last is the token's length: both are rejected
        /// downstream as empty segments). A token that was nothing but pipes disappears entirely
        /// rather than becoming an empty word.
        /// </summary>
        public static (string Text, IReadOnlyList<IReadOnlyList<int>> Pipes) Strip(string withMarkers)
        {
            var texts = new List<string>();
            var pipes = new List<IReadOnlyList<int>>();

            foreach (string token in (withMarkers ?? string.Empty).Split(' '))
            {
                if (token.IndexOf(Typeability.SPLIT_MARKER) < 0)
                {
                    if (token.Length == 0)
                        continue;

                    texts.Add(token);
                    pipes.Add(Array.Empty<int>());
                    continue;
                }

                var sb = new StringBuilder(token.Length);
                var positions = new List<int>();

                foreach (char c in token)
                {
                    if (c == Typeability.SPLIT_MARKER)
                        positions.Add(sb.Length);
                    else
                        sb.Append(c);
                }

                if (sb.Length == 0)
                    continue;

                texts.Add(sb.ToString());
                pipes.Add(positions);
            }

            return (string.Join(' ', texts), pipes);
        }

        /// <summary>
        /// The subdivision TIMES a pipe-marked word gets when nothing else says where its syllables
        /// sit: <paramref name="segments"/> equal slices of [<paramref name="start"/>,
        /// <paramref name="end"/>], returned as the <c>segments - 1</c> internal boundaries. Empty
        /// (author nothing) for a degenerate word with no span at all, since a boundary must land
        /// STRICTLY inside its word.
        /// </summary>
        public static double[] EvenBoundaries(double start, double end, int segments)
        {
            if (segments < 2 || end <= start)
                return Array.Empty<double>();

            double[] boundaries = new double[segments - 1];

            for (int i = 0; i < boundaries.Length; i++)
                boundaries[i] = start + (end - start) * (i + 1) / segments;

            return boundaries;
        }

        /// <summary>
        /// The boundary/split pair one word's pipes author, or null when they author nothing: the
        /// pattern is not a legal cut of <paramref name="token"/> (a pipe that would empty a
        /// segment, or one past its end) or the word has no span to divide. Callers keep whatever
        /// the word already had in that case, which is the same forgiveness the editor has always
        /// shown a mistyped pipe.
        /// </summary>
        public static (double[] Boundaries, int[] Splits)? Authored(string token, double start, double end, IReadOnlyList<int> pipes)
        {
            int segments = pipes.Count + 1;

            if (pipes.Count == 0 || !Gameplay.SyllableSegments.IsAuthoredValid(token, segments, pipes))
                return null;

            double[] boundaries = EvenBoundaries(start, end, segments);

            if (boundaries.Length != pipes.Count)
                return null;

            int[] splits = new int[pipes.Count];

            for (int i = 0; i < splits.Length; i++)
                splits[i] = pipes[i];

            return (boundaries, splits);
        }
    }
}
