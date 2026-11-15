// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;

namespace typebeat.Game.Rulesets.TypeBeat.Edit
{
    /// <summary>
    /// The map as it stood when a timeline drag began (backlog 466). A drag restores it before every
    /// step and then applies the edit for where the cursor is NOW, so only the release point decides
    /// the result: a word the cursor squished, or a hold tick a shortened hold dropped, on the way out
    /// comes back whole when the cursor returns. Without it each step edited the previous step's
    /// result, and every clamp along the path stuck.
    ///
    /// <para>Cheap: a line is immutable, so the capture is one reference and two numbers per line.
    /// Restoring only touches lines a step actually replaced, and runs inside the drag's open
    /// transaction, so the whole gesture is still one undo step.</para>
    /// </summary>
    public sealed class DragBaseline
    {
        private readonly EditorBeatmap editorBeatmap;
        private readonly (TypeBeatHitObject HitObject, LyricLine Line, double StartTime, int LineIndex)[] lines;

        private DragBaseline(EditorBeatmap editorBeatmap)
        {
            this.editorBeatmap = editorBeatmap;
            lines = editorBeatmap.HitObjects.OfType<TypeBeatHitObject>().Select(h => (h, h.Line, h.StartTime, h.LineIndex)).ToArray();
        }

        public static DragBaseline Capture(EditorBeatmap editorBeatmap) => new DragBaseline(editorBeatmap);

        /// <summary>Puts every captured line back as it was at the drag's start.</summary>
        public void Restore()
        {
            foreach (var (hitObject, line, startTime, lineIndex) in lines)
            {
                if (ReferenceEquals(hitObject.Line, line) && hitObject.StartTime == startTime && hitObject.LineIndex == lineIndex)
                    continue;

                TypeBeatEditorOperations.RestoreLine(editorBeatmap, hitObject, line, startTime, lineIndex);
            }
        }
    }
}
