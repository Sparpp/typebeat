// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

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
    /// actions on the bottom (subdivide) right beside the word blocks they act on.
    /// </summary>
    public partial class ActiveLineDetailPanel : CompositeDrawable
    {
        [Resolved]
        private EditorBeatmap editorBeatmap { get; set; } = null!;

        [Resolved]
        private LyricEditState state { get; set; } = null!;

        [Resolved]
        private EditorClock editorClock { get; set; } = null!;

        private OsuSpriteText header = null!;
        private OsuSpriteText timing = null!;

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
                                    header = new OsuSpriteText
                                    {
                                        Font = TypeBeatStyle.Mono(18),
                                        Colour = TypeBeatStyle.TypedChar,
                                    },
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
                            actionRow("word", new[]
                            {
                                actionButton("subdivide (D)", subdivideSelectedWords),
                            }),
                        },
                    },
                },
            };
        }

        private static Drawable actionButton(string text, System.Action action) => new RoundedButton
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

            if (line == null || !editorBeatmap.HitObjects.Contains(line))
            {
                header.Text = "no line, click one, or double-click a gap in the timeline to add";
                timing.Text = string.Empty;
                return;
            }

            var l = line.Line;
            header.Text = $"line {line.LineIndex + 1}: {l.RawText}";
            timing.Text = $"start {l.StartTime:0}ms   sung end {l.SingEndTime:0}ms   window end {l.EndTime:0}ms   "
                          + $"{line.Granularity} granularity{(l.Estimated ? "   [estimated]" : string.Empty)}{(l.SealGraceMs > 0 ? $"   grace {l.SealGraceMs:0}ms" : string.Empty)}";
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

        private void subdivideSelectedWords()
        {
            if (state.ActiveLine.Value is not TypeBeatHitObject line)
                return;

            // Every selected word gets a subdivision (the primary alone when nothing is multi-selected),
            // as one undo. Each press bisects the widest remaining segment, so pressing again keeps
            // splitting. The draggable dotted lines appear in the timeline.
            int[] targets = state.SelectedUnitIndices.Count > 0
                ? state.SelectedUnitIndices.OrderBy(i => i).ToArray()
                : state.SelectedUnitIndex.Value >= 0
                    ? new[] { state.SelectedUnitIndex.Value }
                    : System.Array.Empty<int>();

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
