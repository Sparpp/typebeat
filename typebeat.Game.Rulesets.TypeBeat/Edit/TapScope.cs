// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using typebeat.Game.Rulesets.TypeBeat.Objects;

namespace typebeat.Game.Rulesets.TypeBeat.Edit
{
    /// <summary>
    /// The lyric section a live tap-timing pass is recording over, in HIT OBJECT terms rather than
    /// the snapshot indices <see cref="Beatmaps.TapTarget"/> uses, so the editor's display surfaces
    /// can ask "is this line mine?" without knowing anything about the queue.
    ///
    /// <para>WHY: during a pass the mapper is timing one section by ear, and every other line on
    /// screen is noise they have to read past to find their place. So for the duration of the pass
    /// the surfaces hide everything outside this scope and restore it when the pass ends, whichever
    /// way it ends (Finish, Escape, or any other exit through the overlay's teardown).</para>
    ///
    /// <para>The whole-sheet default is the fresh-paste case: nothing selected means the pass covers
    /// everything, which is exactly when hiding would be wrong. <see cref="CoversEverything"/>
    /// detects that (however it arose, including a mapper who selected every line by hand) and every
    /// query short-circuits to true, so nothing hides.</para>
    /// </summary>
    public sealed class TapScope
    {
        private readonly HashSet<TypeBeatHitObject> lines = new HashSet<TypeBeatHitObject>();
        private readonly TypeBeatHitObject? firstLine;
        private readonly TypeBeatHitObject? lastLine;
        private readonly int firstUnit;
        private readonly int lastUnit;

        /// <summary>The pass covers the whole sheet, so nothing is out of scope and nothing hides.</summary>
        public bool CoversEverything { get; }

        /// <summary>
        /// The contiguous run from (<paramref name="firstLine"/>, <paramref name="firstUnit"/>) to
        /// (<paramref name="lastLine"/>, <paramref name="lastUnit"/>) of <paramref name="ordered"/>,
        /// which is the same span <see cref="Beatmaps.TapTimingBuilder.BuildQueue"/> was handed.
        /// </summary>
        public TapScope(IReadOnlyList<TypeBeatHitObject> ordered, int firstLine, int firstUnit, int lastLine, int lastUnit)
        {
            if (ordered.Count == 0)
            {
                CoversEverything = true;
                return;
            }

            CoversEverything = firstLine <= 0
                               && firstUnit <= 0
                               && lastLine >= ordered.Count - 1
                               && lastUnit >= ordered[^1].Line.Units.Count - 1;

            if (CoversEverything)
                return;

            for (int l = System.Math.Max(0, firstLine); l <= System.Math.Min(lastLine, ordered.Count - 1); l++)
                lines.Add(ordered[l]);

            this.firstLine = firstLine >= 0 && firstLine < ordered.Count ? ordered[firstLine] : null;
            this.lastLine = lastLine >= 0 && lastLine < ordered.Count ? ordered[lastLine] : null;
            this.firstUnit = firstUnit;
            this.lastUnit = lastUnit;
        }

        /// <summary>Whether the pass touches <paramref name="line"/> at all.</summary>
        public bool Covers(TypeBeatHitObject line) => CoversEverything || lines.Contains(line);

        /// <summary>
        /// Whether the pass touches word <paramref name="unitIndex"/> of <paramref name="line"/>.
        /// Only the first and last lines of the scope can be partial, and only when the mapper
        /// narrowed the pass to a word-block run inside a single line.
        /// </summary>
        public bool Covers(TypeBeatHitObject line, int unitIndex)
        {
            if (CoversEverything)
                return true;

            if (!lines.Contains(line))
                return false;

            if (line == firstLine && unitIndex < firstUnit)
                return false;

            if (line == lastLine && unitIndex > lastUnit)
                return false;

            return true;
        }
    }
}
