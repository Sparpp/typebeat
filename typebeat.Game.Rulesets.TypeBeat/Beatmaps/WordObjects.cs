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
    public sealed record WordObjects
    {
        public static WordObjects None { get; } = new WordObjects();

        /// <summary>The word's holds, in character order.</summary>
        public IReadOnlyList<WordHold> Holds { get; init; } = Array.Empty<WordHold>();

        public bool IsEmpty => Holds.Count == 0;

        /// <summary>These objects with each hold mapped linearly from the old word span to the new one.</summary>
        public WordObjects Retimed(double oldStart, double oldEnd, double newStart, double newEnd)
        {
            if (Holds.Count == 0 || (oldStart == newStart && oldEnd == newEnd))
                return this;

            double scale = oldEnd > oldStart ? (newEnd - newStart) / (oldEnd - oldStart) : 1;

            double map(double t) => newStart + (t - oldStart) * scale;

            return this with { Holds = Holds.Select(h => HoldTicks.Stretched(h, map(h.StartTime), map(h.EndTime))).ToArray() };
        }

        /// <summary>These objects for a retyped word: holds index characters, so a new spelling drops them.</summary>
        public WordObjects ForText(string oldText, string newText)
            => Holds.Count == 0 || string.Equals(oldText, newText, StringComparison.Ordinal)
                ? this
                : this with { Holds = Array.Empty<WordHold>() };

        public bool Equals(WordObjects? other)
            => other is not null && (ReferenceEquals(this, other) || Holds.SequenceEqual(other.Holds));

        public override int GetHashCode() => Holds.Count;
    }

    /// <summary>
    /// Submission of maps with word objects. The server rates holds as stored and cannot run
    /// <see cref="HeldWord.UsableHolds"/>, so a map is submitted only when every hold is one a play
    /// honours; otherwise the rating and the play would disagree.
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
            var units = beatmap.HitObjects.OfType<TypeBeatHitObject>().SelectMany(h => h.Line.Units).Where(u => !u.Objects.IsEmpty).ToList();
            int holds = units.Sum(u => u.Objects.Holds.Count - HeldWord.UsableHolds(u).Count);

            if (holds == 0)
                return null;

            return $"This map has {holds} hold{(holds == 1 ? string.Empty : "s")} that can't be played where {(holds == 1 ? "it sits" : "they sit")}. "
                   + "Fix or remove them in the lyric editor to submit it.";
        }
    }
}
