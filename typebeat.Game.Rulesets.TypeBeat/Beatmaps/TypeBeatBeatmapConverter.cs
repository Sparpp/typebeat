// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Objects;
using typebeat.Game.Rulesets.TypeBeat.Objects;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// type!beat maps are never converted FROM other rulesets: lyric data cannot be synthesized
    /// from circles. Native <see cref="TypeBeatHitObject"/>s pass through via the base
    /// converter's is-T fast path; anything else yields nothing.
    /// </summary>
    public class TypeBeatBeatmapConverter : BeatmapConverter<TypeBeatHitObject>
    {
        public TypeBeatBeatmapConverter(IBeatmap beatmap, Ruleset ruleset)
            : base(beatmap, ruleset)
        {
        }

        /// <summary>
        /// Only sources that already carry typebeat objects (the M6 decoder path), or entirely
        /// empty beatmaps, are convertible. Legacy maps are not: Player refuses empty playable
        /// beatmaps ("Beatmap contains no hit objects!") and unconvertible maps simply are not
        /// playable in this ruleset.
        /// </summary>
        public override bool CanConvert() => Beatmap.HitObjects.Count == 0 || Beatmap.HitObjects.Any(h => h is TypeBeatHitObject);

        /// <summary>Playable beatmaps are <see cref="TypeBeatBeatmap"/>s so song select gets live pace statistics.</summary>
        protected override Beatmap<TypeBeatHitObject> CreateBeatmap() => new TypeBeatBeatmap();

        /// <summary>
        /// The base converter passes native <see cref="TypeBeatHitObject"/>s through BY REFERENCE, so
        /// without this every play would share the WorkingBeatmap's cached instances. Each play's
        /// <c>GetPlayableBeatmap</c> then calls <c>ApplyDefaults</c> on those shared instances and
        /// fires <c>HitObject.DefaultsApplied</c>, which, on a fast quick-restart, is still subscribed
        /// by the OUTGOING player's drawables (not yet disposed) and mutates them off-thread, throwing
        /// and surfacing as "Could not load beatmap successfully!". Cloning gives every play its own
        /// hit-object instances so a new load can never touch objects a live player still holds.
        /// </summary>
        protected override Beatmap<TypeBeatHitObject> ConvertBeatmap(IBeatmap original, CancellationToken cancellationToken)
        {
            var converted = base.ConvertBeatmap(original, cancellationToken);
            converted.HitObjects = converted.HitObjects.Select(cloneForPlay).ToList();
            return converted;
        }

        /// <summary>A fresh object carrying the same lyric payload (the shared <see cref="LyricLine"/>
        /// is read-only source data; only the per-play object identity + its DefaultsApplied event
        /// must be independent).</summary>
        private static TypeBeatHitObject cloneForPlay(TypeBeatHitObject source) => new TypeBeatHitObject
        {
            StartTime = source.StartTime,
            LineIndex = source.LineIndex,
            Line = source.Line,
            Granularity = source.Granularity,
            Literate = source.Literate,
        };

        protected override IEnumerable<TypeBeatHitObject> ConvertHitObject(HitObject original, IBeatmap beatmap, CancellationToken cancellationToken)
            => Enumerable.Empty<TypeBeatHitObject>();
    }
}
