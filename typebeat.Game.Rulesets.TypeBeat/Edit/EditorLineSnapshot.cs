// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using typebeat.Game.Rulesets.Objects;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;

namespace typebeat.Game.Rulesets.TypeBeat.Edit
{
    /// <summary>
    /// Shares the committed line order across editor display updates. Scrubbing changes the clock,
    /// not the map, so it should not sort and allocate the same song in every panel each frame.
    /// Mutation operations must continue using <see cref="TypeBeatEditorOperations.OrderedLines"/>
    /// for a fresh view of any in-progress transaction.
    /// </summary>
    public sealed class EditorLineSnapshot
    {
        private static readonly ConditionalWeakTable<EditorBeatmap, EditorLineSnapshot> snapshots = new ConditionalWeakTable<EditorBeatmap, EditorLineSnapshot>();

        public static EditorLineSnapshot For(EditorBeatmap beatmap) => snapshots.GetValue(beatmap, map => new EditorLineSnapshot(map));

        private readonly EditorBeatmap beatmap;
        private readonly OrderedLinesCache orderedLines = new OrderedLinesCache();
        private IReadOnlyList<TypeBeatHitObject>? lines;

        /// <summary>Changes whenever the map is edited, including undo and reprocessing.</summary>
        public int Revision { get; private set; }

        public IReadOnlyList<TypeBeatHitObject> Lines
        {
            get
            {
                var current = orderedLines.Get(beatmap);
                // Also catch direct immutable-line replacements that did not raise an editor event.
                if (lines != null && !ReferenceEquals(lines, current))
                    Revision++;
                return lines = current;
            }
        }

        private EditorLineSnapshot(EditorBeatmap beatmap)
        {
            this.beatmap = beatmap;
            beatmap.HitObjectAdded += invalidate;
            beatmap.HitObjectRemoved += invalidate;
            beatmap.HitObjectUpdated += invalidate;
            beatmap.BeatmapReprocessed += invalidate;
        }

        private void invalidate(HitObject _) => invalidate();

        private void invalidate()
        {
            lines = null;
            Revision++;
        }
    }
}
