// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.IO;
using System.Linq;
using System.Text;
using typebeat.Game.Beatmaps;
using typebeat.Game.IO;
using typebeat.Game.Rulesets;
using Decoder = typebeat.Game.Beatmaps.Formats.Decoder;

namespace typebeat.Game.Screens.Edit
{
    /// <summary>
    /// Undo/redo handler for rulesets that persist beatmaps in a native on-disk text format
    /// (<see cref="Ruleset.CanEncodeToNativeFormat"/>) rather than the legacy <c>.osu</c> format.
    /// Each state is a full serialisation of the beatmap; restoring decodes that text and replaces
    /// the editor's hit objects wholesale. This avoids the legacy line-diff patcher, which only
    /// understands the <c>[HitObjects]</c> section and would silently ignore custom sections.
    /// Appropriate because such maps are small (tens of objects), so a full re-decode is cheap.
    /// </summary>
    public partial class RulesetBeatmapChangeHandler : EditorChangeHandler
    {
        private readonly EditorBeatmap editorBeatmap;
        private readonly Ruleset ruleset;

        public RulesetBeatmapChangeHandler(EditorBeatmap editorBeatmap, Ruleset ruleset)
        {
            this.editorBeatmap = editorBeatmap;
            this.ruleset = ruleset;

            editorBeatmap.TransactionBegan += BeginChange;
            editorBeatmap.TransactionEnded += EndChange;
            editorBeatmap.SaveStateTriggered += SaveState;
        }

        protected override void WriteCurrentStateToStream(MemoryStream stream)
        {
            using (var sw = new StreamWriter(stream, Encoding.UTF8, 1024, true))
                ruleset.EncodeToNativeFormat(editorBeatmap, editorBeatmap.Storyboard, sw);
        }

        protected override void ApplyStateChange(byte[] previousState, byte[] newState)
        {
            var decoded = readBeatmap(newState);

            editorBeatmap.BeginChange();

            editorBeatmap.Clear();
            editorBeatmap.AddRange(decoded.HitObjects.ToArray());

            // The native encoder captures beatmap-level fields (metadata, preview time, audio
            // lead-in) into every state, so undo/redo must restore them too, otherwise a metadata
            // or preview-time edit is silently unreverted (and HasUnsavedChanges goes stale).
            //
            // The two RESOURCE filenames (audio, background) are deliberately excluded. They are not
            // editable text: the only thing that writes them is the setup screen's resource flow,
            // which copies the new file into the beatmap set, DELETES the file the old name pointed
            // at, and saves immediately. Restoring an older state's filename would therefore point
            // the map at a file that no longer exists on disk, silently breaking its audio (or
            // background) as collateral damage of undoing some unrelated lyric edit. There is
            // nothing to undo back to, so the current filenames are left alone.
            var target = decoded.BeatmapInfo.Metadata;
            var current = editorBeatmap.Metadata;

            current.Artist = target.Artist;
            current.ArtistUnicode = target.ArtistUnicode;
            current.Title = target.Title;
            current.TitleUnicode = target.TitleUnicode;
            current.Author.Username = target.Author.Username;

            editorBeatmap.PreviewTime.Value = target.PreviewTime;
            editorBeatmap.AudioLeadIn = decoded.AudioLeadIn;
            editorBeatmap.IntroBeatdrop.Value = decoded.IntroBeatdropTime;

            editorBeatmap.EndChange();
        }

        private IBeatmap readBeatmap(byte[] state)
        {
            using (var stream = new MemoryStream(state))
            using (var reader = new LineBufferedReader(stream, true))
            {
                // The native decoder produces the ruleset's own hit objects directly (no conversion
                // needed); EditorBeatmap.AddRange re-applies defaults, rebuilding nested objects.
                var decoded = Decoder.GetDecoder<Beatmap>(reader).Decode(reader);
                decoded.BeatmapInfo.Ruleset = editorBeatmap.BeatmapInfo.Ruleset;
                return decoded;
            }
        }
    }
}
