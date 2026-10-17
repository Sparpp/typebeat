// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using typebeat.Game.Rulesets.TypeBeat.Import;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    public static partial class TypeBeatEditorOperations
    {
        /// <summary>
        /// Adds generated originals in one undo step. A lookup made against older map content must
        /// never overwrite edits made while it was running. Romanized text and all timings survive.
        /// </summary>
        public static int AddGeneratedOriginals(EditorBeatmap beatmap, IReadOnlyList<TypeBeatHitObject> targets,
                                               IReadOnlyList<LyricLine> snapshot, IReadOnlyList<string?> originals, bool replaceExisting = false)
        {
            if (targets.Count != snapshot.Count || targets.Count != originals.Count)
                throw new ArgumentException("Generated lyrics must correspond to the requested lines.");

            var current = OrderedLines(beatmap);

            if (current.Count != targets.Count || current.Where((h, i) => !ReferenceEquals(h, targets[i]) || !ReferenceEquals(h.Line, snapshot[i])).Any())
                return 0;

            int changed = 0;
            beatmap.BeginChange();

            try
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    var line = snapshot[i];
                    string? original = LyricOriginals.OriginalFor(originals[i], line.RawText);

                    if (original == null || (!replaceExisting && LyricOriginals.HasOriginals(new[] { line })))
                        continue;

                    string?[] words = OriginalLyricGenerator.MatchWords(line, original, beatmap.Metadata.Language);
                    var units = line.Units.Select((u, w) => words[w] == null && !replaceExisting ? u : LrcParser.WithOriginal(u, words[w])).ToArray();
                    targets[i].Line = normalizeOriginalSubdivisions(beatmap, rebuild(line, units: units, originals: (original, replaceExisting ? Array.Empty<UnromanisedWord>() : line.UnromanisedWords)));
                    beatmap.Update(targets[i]);
                    changed++;
                }
            }
            finally
            {
                beatmap.EndChange();
            }

            return changed;
        }
    }
}
