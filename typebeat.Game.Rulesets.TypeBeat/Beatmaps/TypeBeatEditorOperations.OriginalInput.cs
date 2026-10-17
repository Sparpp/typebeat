// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using typebeat.Game.Beatmaps;
using typebeat.Game.Screens.Edit;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    public static partial class TypeBeatEditorOperations
    {
        public static string JapaneseOriginalPunctuation(string text)
            => text.Replace('[', '「').Replace(']', '」').Replace('.', '。').Replace(',', '、');

        public static string NormalizeOriginalInput(EditorBeatmap beatmap, LyricLine line, string text)
            => JapaneseReading.IsJapanese(OriginalSubdivisionLanguage(beatmap, line)) ? JapaneseOriginalPunctuation(text) : text;

        /// <summary>Create editable, initially empty original fields for every word in one undo step.</summary>
        public static bool AddOriginalScript(EditorBeatmap beatmap)
        {
            if (beatmap.Metadata.Language is BeatmapLanguage.English or BeatmapLanguage.Instrumental)
                return false;
            var changes = OrderedLines(beatmap).Where(h => h.Line.Units.Any(u => u.Original == null)).ToArray();
            if (changes.Length == 0)
                return false;
            beatmap.BeginChange();
            foreach (var hit in changes)
            {
                var linked = normalizeOriginalSubdivisions(beatmap, hit.Line);
                hit.Line = rebuild(linked, units: linked.Units.Select(u => u.Original == null ? LrcParser.WithOriginal(u, string.Empty) : u).ToArray());
                beatmap.Update(hit);
            }
            beatmap.EndChange();
            return true;
        }
    }
}
