// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// A HOLD: the character at <paramref name="Char"/> (index into <see cref="TimedUnit.Text"/>) is held
    /// from <paramref name="StartTime"/> to <paramref name="EndTime"/>. <c>HeldWord.UsableHolds</c> decides
    /// which holds a play honours.
    /// </summary>
    public readonly record struct WordHold(int Char, double StartTime, double EndTime)
    {
        private readonly IReadOnlyList<double>? ticks;

        /// <summary>The hold's authored ticks, absolute ms, ascending. Never null.</summary>
        public IReadOnlyList<double> Ticks
        {
            get => ticks ?? Array.Empty<double>();
            init => ticks = value;
        }

        // Ticks compare by content, so a decoded hold equals the one that was saved.
        public bool Equals(WordHold other)
            => Char == other.Char && StartTime.Equals(other.StartTime) && EndTime.Equals(other.EndTime) && Ticks.SequenceEqual(other.Ticks);

        public override int GetHashCode() => HashCode.Combine(Char, StartTime, EndTime, Ticks.Count);
    }

    /// <summary>
    /// The mapper-authored objects a word carries beyond its timing. One record, so every unit rebuild
    /// carries them all with one assignment.
    /// </summary>
    /// <param name="AutoFill">Only the word's onsets are typed; the rest completes itself.</param>
    public sealed record WordObjects(bool AutoFill)
    {
        public static WordObjects None { get; } = new WordObjects(AutoFill: false);

        /// <summary>The word's holds, in character order.</summary>
        public IReadOnlyList<WordHold> Holds { get; init; } = Array.Empty<WordHold>();

        /// <summary>The letters of an auto-fill word the player types, as indices into the text.</summary>
        public IReadOnlyList<int> TypedLetters { get; init; } = Array.Empty<int>();

        public bool IsEmpty => !AutoFill && Holds.Count == 0 && TypedLetters.Count == 0;

        /// <summary>These objects with each hold mapped linearly from the old word span to the new one.</summary>
        public WordObjects Retimed(double oldStart, double oldEnd, double newStart, double newEnd)
        {
            if (Holds.Count == 0 || (oldStart == newStart && oldEnd == newEnd))
                return this;

            double scale = oldEnd > oldStart ? (newEnd - newStart) / (oldEnd - oldStart) : 1;

            double map(double t) => newStart + (t - oldStart) * scale;

            return this with { Holds = Holds.Select(h => HoldTicks.Stretched(h, map(h.StartTime), map(h.EndTime))).ToArray() };
        }

        /// <summary>These objects for a retyped word: holds and picks index characters, so a new spelling drops them.</summary>
        public WordObjects ForText(string oldText, string newText)
            => (Holds.Count == 0 && TypedLetters.Count == 0) || string.Equals(oldText, newText, StringComparison.Ordinal)
                ? this
                : this with { Holds = Array.Empty<WordHold>(), TypedLetters = Array.Empty<int>() };

        public bool Equals(WordObjects? other)
            => other is not null
               && (ReferenceEquals(this, other)
                   || (AutoFill == other.AutoFill && Holds.SequenceEqual(other.Holds) && TypedLetters.SequenceEqual(other.TypedLetters)));

        public override int GetHashCode() => HashCode.Combine(AutoFill, Holds.Count, TypedLetters.Count);
    }

    /// <summary>
    /// The server cannot parse, rate or replay word objects yet, so a map that uses any is played
    /// locally but not submitted.
    /// </summary>
    public static class WordObjectsSubmission
    {
        private static readonly ConditionalWeakTable<IBeatmap, object> uses_word_objects = new ConditionalWeakTable<IBeatmap, object>();

        /// <summary>Whether any word of <paramref name="beatmap"/> carries objects, cached per beatmap instance.</summary>
        public static bool UsesWordObjects(IBeatmap? beatmap)
        {
            if (beatmap == null)
                return false;

            return (bool)uses_word_objects.GetValue(beatmap,
                b => b.HitObjects.OfType<TypeBeatHitObject>().Any(h => h.Line.Units.Any(u => !u.Objects.IsEmpty)));
        }

        public static string? Refusal(IBeatmap beatmap)
        {
            var units = beatmap.HitObjects.OfType<TypeBeatHitObject>().SelectMany(h => h.Line.Units).ToList();
            int autoFill = units.Count(u => u.Objects.AutoFill);
            int holds = units.Sum(u => u.Objects.Holds.Count);

            if (autoFill == 0 && holds == 0)
                return null;

            var parts = new List<string>();

            if (autoFill > 0)
                parts.Add($"{autoFill} auto-fill word{(autoFill == 1 ? string.Empty : "s")}");

            if (holds > 0)
                parts.Add($"{holds} hold{(holds == 1 ? string.Empty : "s")}");

            return $"This map has {string.Join(" and ", parts)}, which the server can't take yet. "
                   + "It plays locally; remove them in the lyric editor to submit it.";
        }
    }
}
