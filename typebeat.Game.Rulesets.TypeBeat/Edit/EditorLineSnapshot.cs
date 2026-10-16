// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using typebeat.Game.Rulesets.Objects;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;

namespace typebeat.Game.Rulesets.TypeBeat.Edit
{
    /// <summary>
    /// Shares the committed line order across editor display updates. Scrubbing changes the clock,
    /// not the map, so it should not sort and allocate the same song in every panel each frame.
    /// Mutation operations must continue using <see cref="TypeBeatEditorOperations.OrderedLines"/>
    /// for a fresh view of any in-progress transaction.
    ///
    /// <para>The order is sorted again only after the beatmap reports a change. A line is immutable,
    /// so replacing it is the only way an edit moves the sort key, and every editor edit does that
    /// through <see cref="TypeBeatEditorOperations"/>, which queues the hit object's update. The
    /// beatmap raises the update when the edit commits, or on its next frame inside a longer
    /// transaction such as a drag, and the beatmap updates before the editor's surfaces do.</para>
    /// </summary>
    public sealed class EditorLineSnapshot
    {
        private static readonly ConditionalWeakTable<EditorBeatmap, EditorLineSnapshot> snapshots = new ConditionalWeakTable<EditorBeatmap, EditorLineSnapshot>();

        public static EditorLineSnapshot For(EditorBeatmap beatmap) => snapshots.GetValue(beatmap, map => new EditorLineSnapshot(map));

        private readonly EditorBeatmap beatmap;
        private IReadOnlyList<TypeBeatHitObject>? lines;

        /// <summary>Changes whenever the map is edited, including undo and reprocessing.</summary>
        public int Revision { get; private set; }

        /// <summary>The lines in typing order. Do not mutate the result.</summary>
        public IReadOnlyList<TypeBeatHitObject> Lines => lines ??= TypeBeatEditorOperations.OrderedLines(beatmap);

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
