// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.UI
{
    /// <summary>
    /// The shimmer ("obfuscated text") a FREESTYLE character wears until the player fills it in.
    ///
    /// <para>The idea is ported from Minecraft's obfuscated-text style: rather than animating a
    /// glyph, the renderer substitutes a DIFFERENT random glyph every tick, chosen from the
    /// candidates that share the original's advance width. Grouping by width is the whole trick,
    /// it is what stops the surrounding text from jittering horizontally as the glyph changes.
    /// Only the idea is ported; none of Minecraft's code or data is used, and the candidate pool
    /// here is a modest A-Z / a-z / 0-9 set measured in whatever font the caller is rendering
    /// with.</para>
    ///
    /// <para>Pure and deterministic: the "random" glyph is a hash of (tick, position), so a frozen
    /// clock shows a frozen glyph, two displays never have to share a random source, and the whole
    /// thing is unit-testable. Nothing here touches gameplay; the engine neither knows nor cares
    /// which glyph is on screen.</para>
    /// </summary>
    public static class FreestyleGlyphs
    {
        /// <summary>The candidate glyphs a shimmering cell may show, before width grouping.</summary>
        public const string CANDIDATES = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

        /// <summary>All candidates as a ready-made pool, for callers rendering in a FIXED-WIDTH font
        /// (every glyph already shares an advance there, so there is nothing to group).</summary>
        public static readonly char[] FIXED_WIDTH_POOL = CANDIDATES.ToCharArray();

        /// <summary>How long a single substituted glyph stays on screen (ms).</summary>
        public const double SHIMMER_INTERVAL_MS = 60;

        /// <summary>Width bucketing tolerance, as a fraction of the widest candidate.</summary>
        private const float width_tolerance = 0.02f;

        /// <summary>
        /// Groups <see cref="CANDIDATES"/> by measured advance width and returns the LARGEST group:
        /// the biggest set of glyphs that can stand in for one another without changing the line's
        /// width. <paramref name="advance"/> returns a glyph's advance in any consistent unit, or
        /// null when the font cannot measure it. If nothing measures (no font store available), the
        /// full candidate list is returned so the effect still runs; cells are laid out once at load
        /// and never re-measured, so an odd advance can only overhang slightly, never reflow a line.
        /// </summary>
        public static char[] BuildPool(Func<char, float?> advance)
        {
            ArgumentNullException.ThrowIfNull(advance);

            var measured = new List<(char Glyph, float Width)>(CANDIDATES.Length);
            float widest = 0;

            foreach (char c in CANDIDATES)
            {
                if (advance(c) is float w && w > 0)
                {
                    measured.Add((c, w));
                    widest = Math.Max(widest, w);
                }
            }

            if (measured.Count == 0 || widest <= 0)
                return CANDIDATES.ToCharArray();

            float tolerance = widest * width_tolerance;

            // Greedy grouping over the measured widths: every candidate seeds a group of the
            // glyphs within tolerance of it, and the fullest group wins (ties keep the first,
            // which makes the result independent of dictionary ordering).
            char[] best = Array.Empty<char>();

            foreach ((char _, float seed) in measured)
            {
                var group = new List<char>(measured.Count);

                foreach ((char glyph, float width) in measured)
                {
                    if (Math.Abs(width - seed) <= tolerance)
                        group.Add(glyph);
                }

                if (group.Count > best.Length)
                    best = group.ToArray();
            }

            return best.Length > 0 ? best : CANDIDATES.ToCharArray();
        }

        /// <summary>
        /// The glyph a shimmering slot shows on tick <paramref name="tick"/>. <paramref name="position"/>
        /// (a cell/char index) decorrelates neighbouring slots so two markers side by side do not
        /// shimmer in lockstep. Falls back to the authoring marker for an empty pool.
        /// </summary>
        public static char Glyph(IReadOnlyList<char> pool, int tick, int position)
        {
            if (pool == null || pool.Count == 0)
                return Typeability.FREESTYLE_MARKER;

            // Cheap deterministic mix (xorshift-flavoured, the same shape as a 32-bit finaliser).
            unchecked
            {
                uint h = (uint)tick * 2654435761u ^ ((uint)position + 1u) * 2246822519u;
                h ^= h >> 15;
                h *= 2654435761u;
                h ^= h >> 13;

                return pool[(int)(h % (uint)pool.Count)];
            }
        }

        /// <summary>Tick index for a clock time; the shimmer advances with the gameplay/editor clock
        /// rather than wall time, so a paused or scrubbed clock holds a stable glyph.</summary>
        public static int TickFor(double timeMs) => (int)Math.Floor(timeMs / SHIMMER_INTERVAL_MS);

        /// <summary>
        /// Substitutes every <see cref="Typeability.FREESTYLE_MARKER"/> in <paramref name="text"/>
        /// with its current shimmer glyph, for single-sprite labels (editor readouts) that cannot
        /// colour characters individually. Returns the input unchanged when it holds no marker, so
        /// the common case allocates nothing.
        /// </summary>
        public static string Substitute(string text, IReadOnlyList<char> pool, int tick)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf(Typeability.FREESTYLE_MARKER) < 0)
                return text;

            char[] chars = text.ToCharArray();

            for (int i = 0; i < chars.Length; i++)
            {
                if (Typeability.IsFreestyle(chars[i]))
                    chars[i] = Glyph(pool, tick, i);
            }

            return new string(chars);
        }
    }
}
