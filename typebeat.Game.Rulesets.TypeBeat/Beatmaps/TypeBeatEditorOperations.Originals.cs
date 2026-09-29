// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// THE WORD EDITOR for the original text (backlog 330): the two-row, per-word surface where a
    /// word's ORIGINAL (above) and its ROMANISATION (below) are edited, the unromanised words
    /// included. The line box (<see cref="SetLineText"/>) proposes a romanisation for a whole line;
    /// these overwrite it word by word, and are the only way to give an unromanised word its text.
    /// </summary>
    public static partial class TypeBeatEditorOperations
    {
        /// <summary>
        /// One WORD of a line as the word editor lists it: a unit (<see cref="UnitIndex"/>) or an
        /// unromanised word (<see cref="PendingIndex"/>), in sung order, with its typed text (empty
        /// for an unromanised word) and its original.
        /// </summary>
        public readonly record struct WordSlot(int? UnitIndex, int? PendingIndex, string Text, string? Original, double StartTime, double EndTime)
        {
            public bool Unromanised => PendingIndex != null;
        }

        /// <summary>
        /// The line's words in order, each unromanised word before the unit at its position (several
        /// at one position in the order the line lists them).
        /// </summary>
        public static IReadOnlyList<WordSlot> WordSlots(LyricLine line)
        {
            var slots = new List<WordSlot>(line.Units.Count + line.UnromanisedWords.Count);

            for (int u = 0; u <= line.Units.Count; u++)
            {
                for (int p = 0; p < line.UnromanisedWords.Count; p++)
                {
                    var pending = line.UnromanisedWords[p];

                    if (System.Math.Min(pending.Position, line.Units.Count) == u)
                        slots.Add(new WordSlot(null, p, string.Empty, pending.Original, pending.StartTime, pending.EndTime));
                }

                if (u < line.Units.Count)
                {
                    var unit = line.Units[u];
                    slots.Add(new WordSlot(u, null, unit.Text, unit.Original, unit.StartTime, unit.EndTime));
                }
            }

            return slots;
        }

        /// <summary>
        /// The caption the editor shows over a line's romanised text: its original, or when it has
        /// none of its own but its words do, the words' originals joined. Null for a line without any.
        /// </summary>
        public static string? OriginalCaption(LyricLine line)
        {
            if (line.Original != null)
                return line.Original;

            if (line.UnromanisedWords.Count == 0 && line.Units.All(u => u.Original == null))
                return null;

            return JoinedOriginal(line.Units, line.UnromanisedWords);
        }

        /// <summary>
        /// Sets the ROMANISED text of word <paramref name="slot"/> (the word editor's lower row). The
        /// text is normalized like any lyric (freestyle markers kept) and must be ONE word: spaces are
        /// taken out, because the slot is one sung word. For a unit, the word keeps its span, and its
        /// subdivision and rests as long as they still fit the new spelling; emptying a unit that has
        /// an original turns it back into an unromanised word. For an unromanised word, a text makes
        /// it an ordinary word at its place, hand timed over its own span. Returns false when nothing
        /// changed. Single undo step.
        /// </summary>
        public static bool SetWordText(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int slot, string text)
        {
            var line = hitObject.Line;
            var slots = WordSlots(line);

            if (slot < 0 || slot >= slots.Count)
                return false;

            string normalized = string.Concat(Typeability.Normalize(text, keepFreestyleMarkers: true).Split(' '));
            bool empty = Typeability.ToDefaultStream(normalized).Length == 0;
            var target = slots[slot];
            var entries = entriesOf(line);

            if (target.UnitIndex is int unitIndex)
            {
                var unit = line.Units[unitIndex];

                if (empty)
                {
                    if (unit.Original == null)
                        return false;

                    entries[slot] = (null, new UnromanisedWord(0, unit.Original, unit.StartTime, unit.EndTime));
                }
                else
                {
                    if (normalized == unit.Text)
                        return false;

                    entries[slot] = (respelled(unit, normalized), null);
                }
            }
            else
            {
                if (empty)
                    return false;

                var pending = line.UnromanisedWords[target.PendingIndex!.Value];
                var placed = placedWord(normalized, pending.StartTime, System.Math.Max(pending.EndTime, pending.StartTime));
                entries[slot] = (LrcParser.WithOriginal(placed, pending.Original), null);
            }

            commitEntries(editorBeatmap, hitObject, entries, line.Original);
            return true;
        }

        /// <summary>
        /// Sets the ORIGINAL of word <paramref name="slot"/> (the word editor's upper row). With
        /// <paramref name="proposeRomanisation"/>, an original the romaniser can spell also PROPOSES
        /// the word's typed text, which the lower row then shows for the mapper to keep or overwrite;
        /// an original it cannot spell leaves the typed text alone. Emptying the original of an
        /// unromanised word removes that word altogether, since nothing of it would be left. Returns
        /// false when nothing changed. Single undo step.
        /// </summary>
        public static bool SetWordOriginal(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int slot, string original, bool proposeRomanisation = true)
        {
            var line = hitObject.Line;
            var slots = WordSlots(line);

            if (slot < 0 || slot >= slots.Count)
                return false;

            string collapsed = LyricOriginals.CollapseWhitespace(original);
            var target = slots[slot];
            var entries = entriesOf(line);

            LyricOriginals.RomanisedWord? proposal = null;

            if (proposeRomanisation && collapsed.Length > 0)
            {
                string? language = LyricOriginals.RomanisationLanguage(editorBeatmap.BeatmapInfo.Metadata.Language, new[] { collapsed });
                proposal = LyricOriginals.RomaniseWord(string.Concat(collapsed.Split(' ')), language);

                if (proposal.Flagged || proposal.Text.Length == 0 || Typeability.ToDefaultStream(proposal.Text).Length == 0)
                    proposal = null;
            }

            if (target.UnitIndex is int unitIndex)
            {
                var unit = line.Units[unitIndex];
                string? newOriginal = collapsed.Length == 0 || collapsed == (proposal?.Text ?? unit.Text) ? null : collapsed;
                var changed = proposal != null && proposal.Text != unit.Text ? respelled(unit, proposal.Text) : unit;

                if (newOriginal == unit.Original && ReferenceEquals(changed, unit))
                    return false;

                entries[slot] = (LrcParser.WithOriginal(changed, newOriginal), null);
            }
            else
            {
                var pending = line.UnromanisedWords[target.PendingIndex!.Value];

                if (collapsed.Length == 0)
                {
                    entries.RemoveAt(slot);
                }
                else if (proposal != null)
                {
                    var placed = placedWord(proposal.Text, pending.StartTime, System.Math.Max(pending.EndTime, pending.StartTime));
                    entries[slot] = (LrcParser.WithOriginal(placed, collapsed == proposal.Text ? null : collapsed), null);
                }
                else
                {
                    if (collapsed == pending.Original)
                        return false;

                    entries[slot] = (null, pending with { Original = collapsed });
                }
            }

            commitEntries(editorBeatmap, hitObject, entries, line.Original);
            return true;
        }

        /// <summary>
        /// Sets the line's own ORIGINAL (the caption over it), or clears it with an empty string, in
        /// which case the caption falls back to the words' originals. Returns false when nothing
        /// changed. Single undo step.
        /// </summary>
        public static bool SetLineOriginal(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, string original)
        {
            var line = hitObject.Line;
            string collapsed = LyricOriginals.CollapseWhitespace(original);
            string? value = collapsed.Length == 0 || collapsed == line.RawText ? null : collapsed;

            if (value == line.Original)
                return false;

            editorBeatmap.BeginChange();
            hitObject.Line = rebuild(line, originals: (value, line.UnromanisedWords));
            editorBeatmap.Update(hitObject);
            editorBeatmap.EndChange();
            return true;
        }

        /// <summary>A unit re-spelled: same span, and its subdivision and rests kept only while they still fit the new text.</summary>
        private static TimedUnit respelled(TimedUnit unit, string text) => new TimedUnit
        {
            Text = text,
            StartTime = unit.StartTime,
            EndTime = unit.EndTime,
            Source = TimingSource.Explicit,
            Confidence = 1,
            SyllableBoundaries = unit.SyllableBoundaries,
            SyllableSplits = Gameplay.SyllableSegments.IsAuthoredValid(text, unit.SyllableBoundaries.Count + 1, unit.SyllableSplits)
                ? unit.SyllableSplits
                : System.Array.Empty<int>(),
            Pauses = carryPausesAcrossText(unit, text),
            Original = unit.Original,
        };

        /// <summary>The line's words in slot order, each either a unit or an unromanised word.</summary>
        private static List<(TimedUnit? Unit, UnromanisedWord? Pending)> entriesOf(LyricLine line)
            => WordSlots(line).Select(s => s.UnitIndex is int u
                ? ((TimedUnit?)line.Units[u], (UnromanisedWord?)null)
                : ((TimedUnit?)null, (UnromanisedWord?)line.UnromanisedWords[s.PendingIndex!.Value])).ToList();

        /// <summary>
        /// Writes a word list back as the line: the units in order (the text is theirs, joined), each
        /// unromanised word at the place the units before it give it. One undo step, and the map's
        /// granularity follows (an unromanised word, like a hand-timed one, needs words[]).
        /// </summary>
        private static void commitEntries(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject,
                                          List<(TimedUnit? Unit, UnromanisedWord? Pending)> entries, string? lineOriginal)
        {
            var line = hitObject.Line;
            var units = new List<TimedUnit>();
            var pending = new List<UnromanisedWord>();

            foreach (var (unit, word) in entries)
            {
                if (unit != null)
                    units.Add(unit);
                else if (word is UnromanisedWord w)
                    pending.Add(w with { Position = units.Count });
            }

            string rawText = string.Join(' ', units.Select(u => u.Text));

            editorBeatmap.BeginChange();
            hitObject.Line = rebuild(line, rawText: rawText, units: units, originals: (lineOriginal, pending));
            editorBeatmap.Update(hitObject);
            syncGranularity(editorBeatmap, keepAuthoredWords: true);

            // A LINE-granularity map writes no words[], so its words' originals persist only through
            // the line's own original, which the decode splits back over the words. Rebuilt from the
            // words here, or a word's new original would not survive the save.
            if (hitObject.Granularity == TimingGranularity.Line && hitObject.Line.Units.Any(u => u.Original != null))
            {
                hitObject.Line = rebuild(hitObject.Line, originals: (JoinedOriginal(hitObject.Line.Units, hitObject.Line.UnromanisedWords), hitObject.Line.UnromanisedWords));
                editorBeatmap.Update(hitObject);
            }

            syncSingEndToLastUnit(editorBeatmap, hitObject, lastUnitEnd(line));
            editorBeatmap.EndChange();
        }
    }
}
