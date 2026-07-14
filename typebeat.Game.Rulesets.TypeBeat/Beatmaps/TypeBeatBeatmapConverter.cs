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
        /// Only sources that already carry typebeat objects (the M6 decoder path) — or entirely
        /// empty beatmaps — are convertible. Legacy maps are not: Player refuses empty playable
        /// beatmaps ("Beatmap contains no hit objects!") and unconvertible maps simply are not
        /// playable in this ruleset.
        /// </summary>
        public override bool CanConvert() => Beatmap.HitObjects.Count == 0 || Beatmap.HitObjects.Any(h => h is TypeBeatHitObject);

        /// <summary>Playable beatmaps are <see cref="TypeBeatBeatmap"/>s so song select gets live pace statistics.</summary>
        protected override Beatmap<TypeBeatHitObject> CreateBeatmap() => new TypeBeatBeatmap();

        protected override IEnumerable<TypeBeatHitObject> ConvertHitObject(HitObject original, IBeatmap beatmap, CancellationToken cancellationToken)
            => Enumerable.Empty<TypeBeatHitObject>();
    }
}
