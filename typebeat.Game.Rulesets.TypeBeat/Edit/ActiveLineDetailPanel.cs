// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Screens.Edit;
using osuTK;

namespace typebeat.Game.Rulesets.TypeBeat.Edit
{
    /// <summary>
    /// Everything about the ACTIVE line, sandwiched between two categorised action rows:
    /// LINE-level actions on top (add at playhead, split before word, merge, delete), then the
    /// line view (index, text, start / sung end / window end, granularity, estimated badge),
    /// then the interactive fine-timing surface (<see cref="LyricTimeline"/>), and WORD-level
    /// actions on the bottom (add word, remove word, subdivide) right beside the word blocks they
    /// act on.
    /// </summary>
    public partial class ActiveLineDetailPanel : CompositeDrawable
    {
        [Resolved]
        private EditorBeatmap editorBeatmap { get; set; } = null!;

        [Resolved]
        private LyricEditState state { get; set; } = null!;

        [Resolved]
        private EditorClock editorClock { get; set; } = null!;

        private FreestyleTextFlow header = null!;
        private OsuSpriteText timing = null!;
        private RoundedButton addWordButton = null!;
        private RoundedButton removeWordButton = null!;

        public ActiveLineDetailPanel()
        {
            RelativeSizeAxes = Axes.Both;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            InternalChildren = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = TypeBeatStyle.Background,
                    Alpha = 0.4f,
                },
                new GridContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    Padding = new MarginPadding(8),
                    RowDimensions = new[]
                    {
                        // Sandwich: LINE-level actions on top, then the readouts, then the
                        // fine-timing word strip, and WORD-level actions on the bottom (so the
                        // word buttons sit right under the word blocks they act on).
                        new Dimension(GridSizeMode.Absolute, 30),
                        new Dimension(GridSizeMode.Absolute, 52),
                        new Dimension(),
                        new Dimension(GridSizeMode.Absolute, 30),
                    },
                    Content = new[]
                    {
                        new Drawable[]
                        {
                            // The R hotkey still replays the active line (see LyricComposeScreen);
                            // the button was dropped to declutter the row.
                            // Copy/paste timing stays on the standard ^C/^V hotkeys
                            // (LyricComposeScreen.Copy/Paste); the buttons were dropped.
                            actionRow("line", new[]
                            {
                                actionButton("add @ playhead", addAtPlayhead),
                                actionButton("split @ word (S)", splitAtSelectedWord),
                                actionButton("merge next (M)", mergeNext),
                                actionButton("delete line", deleteLine),
                            }),
                        },
                        new Drawable[]
                        {
                            new FillFlowContainer
                            {
                                RelativeSizeAxes = Axes.Both,
                                Direction = FillDirection.Vertical,
                                Spacing = new Vector2(0, 4),
                                Padding = new MarginPadding { Vertical = 8 },
                                Children = new Drawable[]
                                {
                                    // Per-character flow rather than a plain label: the line preview
                                    // is where a mapper checks their '&' authoring, so freestyle
                                    // slots shimmer here in the freestyle colour exactly as they
                                    // will in gameplay.
                                    header = new FreestyleTextFlow(18, TypeBeatStyle.TypedChar),
                                    timing = new OsuSpriteText
                                    {
                                        Font = TypeBeatStyle.Mono(13),
                                        Colour = TypeBeatStyle.UntypedChar,
                                    },
                                },
                            },
                        },
                        new Drawable[]
                        {
                            new Container
                            {
                                RelativeSizeAxes = Axes.Both,
                                Padding = new MarginPadding { Vertical = 6 },
                                Child = new LyricTimeline(),
                            },
                        },
                        new Drawable[]
                        {
                            actionRow("word", new Drawable[]
                            {
                                addWordButton = actionButton("add word", addWord),
                                removeWordButton = actionButton("remove word", removeWord),
                                actionButton("subdivide (D)", subdivideSelectedWords),
                            }),
                        },
                    },
                },
            };
        }

        private static RoundedButton actionButton(string text, System.Action action) => new RoundedButton
        {
            Text = text,
            Action = action,
            Width = 108,
            Height = 30,
        };

        /// <summary>One categorised action row: a small caption ("line" / "word") then its buttons.</summary>
        private static Drawable actionRow(string category, Drawable[] buttons)
        {
            var row = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.Both,
                Direction = FillDirection.Horizontal,
                Spacing = new Vector2(6, 0),
            };

            row.Add(new Container
            {
                Width = 36,
                RelativeSizeAxes = Axes.Y,
                Child = new OsuSpriteText
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Text = category,
                    Font = TypeBeatStyle.Mono(11),
                    Colour = TypeBeatStyle.UntypedChar,
                },
            });

            row.AddRange(buttons);
            return row;
        }

        protected override void Update()
        {
            base.Update();

            var line = state.ActiveLine.Value;
            bool live = line != null && editorBeatmap.HitObjects.Contains(line);

            // Word mutation needs a live line, and never runs mid tap-pass: a pass is
            // record-then-commit, so the sheet it is timing must not change under it.
            bool editable = live && state.TapSession == null;

            addWordButton.Enabled.Value = editable;
            removeWordButton.Enabled.Value = editable && line != null && removalTargets(line).Length > 0;

            if (line == null || !live)
            {
                // A BLANK map (an audio-only import) has no lines at all, so "click one" would be
                // advice about something that does not exist: point at the two ways to author the
                // very first line instead.
                header.Text = editorBeatmap.HitObjects.Count == 0
                    ? "no lyrics yet, press \"add @ playhead\" above (or double-click the timeline) to write the first line"
                    : "no line, click one, or double-click a gap in the timeline to add";
                timing.Text = string.Empty;
                return;
            }

            // A tap-timing pass shows only the section it is recording, and the preview is the one
            // place a whole line's text is spelled out. If the pinned active line happens to sit
            // outside the pass, its lyric goes away with the rest until the pass ends.
            if (state.HiddenByTapScope(line))
            {
                header.Text = "tap timing: timing another section";
                timing.Text = string.Empty;
                return;
            }

            var l = line.Line;
            header.Text = $"line {line.LineIndex + 1}: {l.RawText}";

            // Freestyle authoring feedback: the preview above shimmers the slots, this states the
            // count outright so a stray '&' is impossible to miss.
            int freestyle = l.RawText.Count(Typeability.IsFreestyle);

            timing.Text = $"start {l.StartTime:0}ms   sung end {l.SingEndTime:0}ms   window end {l.EndTime:0}ms   "
                          + $"{line.Granularity} granularity{(l.Estimated ? "   [estimated]" : string.Empty)}{(l.SealGraceMs > 0 ? $"   grace {l.SealGraceMs:0}ms" : string.Empty)}"
                          + (freestyle > 0 ? $"   {freestyle} freestyle ('&' = any key)" : string.Empty);
        }

        private void addAtPlayhead()
        {
            var added = TypeBeatEditorOperations.AddLine(editorBeatmap, editorClock.CurrentTime);

            if (added != null)
                state.SelectedLine.Value = added;
        }

        private void splitAtSelectedWord()
        {
            if (state.ActiveLine.Value is TypeBeatHitObject line && state.SelectedUnitIndex.Value > 0)
                TypeBeatEditorOperations.SplitLine(editorBeatmap, line, state.SelectedUnitIndex.Value);
        }

        /// <summary>
        /// The words a word-level action addresses: the multi-selection when there is one, else the
        /// primary focused word, else none (each action decides what "nothing selected" means for
        /// it). Ascending, and never past the end of the line's word list.
        /// </summary>
        private int[] selectedWords(TypeBeatHitObject line)
        {
            int words = wordCount(line);

            IEnumerable<int> selected = state.SelectedUnitIndices.Count > 0
                ? state.SelectedUnitIndices
                : state.SelectedUnitIndex.Value >= 0
                    ? new[] { state.SelectedUnitIndex.Value }
                    : System.Array.Empty<int>();

            return selected.Where(i => i >= 0 && i < words).OrderBy(i => i).ToArray();
        }

        /// <summary>
        /// The words "remove word" would delete: the selection, or the LAST word when nothing is
        /// selected. EMPTY when the deletion would leave the line wordless (the format has no such
        /// line), which is exactly what greys the button out.
        /// </summary>
        private int[] removalTargets(TypeBeatHitObject line)
        {
            int words = wordCount(line);
            int[] targets = selectedWords(line);

            if (targets.Length == 0 && words > 0)
                targets = new[] { words - 1 };

            return targets.Length < words ? targets : System.Array.Empty<int>();
        }

        /// <summary>Words in a line: one per whitespace token (the unit list mirrors them).</summary>
        private static int wordCount(TypeBeatHitObject line) => line.Line.RawText.Split(' ').Length;

        private void addWord()
        {
            if (state.ActiveLine.Value is not TypeBeatHitObject line)
                return;

            // Inserted after the PRIMARY selected word (the anchor of any multi-selection);
            // nothing selected appends at the line's end.
            int after = state.SelectedUnitIndex.Value;
            int inserted = after >= 0 ? after + 1 : line.Line.Units.Count;

            if (TypeBeatEditorOperations.AddWord(editorBeatmap, line, after))
                state.SelectUnit(System.Math.Min(inserted, line.Line.Units.Count - 1));
        }

        private void removeWord()
        {
            if (state.ActiveLine.Value is not TypeBeatHitObject line)
                return;

            int[] targets = removalTargets(line);

            if (targets.Length == 0)
                return;

            // Every selected word goes as ONE undo (same idiom as subdivide), highest index first
            // so the pending ones stay addressable as the list shrinks.
            editorBeatmap.BeginChange();

            foreach (int i in targets.OrderByDescending(i => i))
                TypeBeatEditorOperations.RemoveWord(editorBeatmap, line, i);

            editorBeatmap.EndChange();

            // Keep the focus where the deletion happened: whatever shifted into the lowest removed
            // slot, clamped to the line's new end.
            state.SelectUnit(System.Math.Min(targets[0], line.Line.Units.Count - 1));
        }

        private void subdivideSelectedWords()
        {
            if (state.ActiveLine.Value is not TypeBeatHitObject line)
                return;

            // Every selected word gets a subdivision (the primary alone when nothing is multi-selected),
            // as one undo. Each press bisects the widest remaining segment, so pressing again keeps
            // splitting. The draggable dotted lines appear in the timeline.
            int[] targets = selectedWords(line);

            if (targets.Length == 0)
                return;

            editorBeatmap.BeginChange();

            foreach (int i in targets)
                TypeBeatEditorOperations.AddSyllableBoundary(editorBeatmap, line, i);

            editorBeatmap.EndChange();
        }

        private void mergeNext()
        {
            if (state.ActiveLine.Value is TypeBeatHitObject line)
                TypeBeatEditorOperations.MergeWithNext(editorBeatmap, line);
        }

        private void deleteLine()
        {
            if (state.ActiveLine.Value is not TypeBeatHitObject line)
                return;

            TypeBeatEditorOperations.DeleteLine(editorBeatmap, line);
            state.SelectedLine.Value = null;
        }
    }
}
