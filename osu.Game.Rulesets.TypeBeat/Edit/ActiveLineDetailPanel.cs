// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Rulesets.TypeBeat.Beatmaps;
using osu.Game.Rulesets.TypeBeat.Objects;
using osu.Game.Rulesets.TypeBeat.UI;
using osu.Game.Screens.Edit;
using osuTK;

namespace osu.Game.Rulesets.TypeBeat.Edit
{
    /// <summary>
    /// Everything about the ACTIVE line: header readouts (index, start / sung end / window end,
    /// granularity, estimated badge), the <see cref="WordStrip"/> fine-timing surface, and the
    /// structural action bar (replay, add at playhead, split before word, merge, delete).
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
                        new Dimension(GridSizeMode.Absolute, 26),
                        new Dimension(GridSizeMode.Absolute, 22),
                        new Dimension(),
                        new Dimension(GridSizeMode.Absolute, 44),
                    },
                    Content = new[]
                    {
                        new Drawable[]
                        {
                            header = new OsuSpriteText
                            {
                                Font = TypeBeatStyle.Mono(18),
                                Colour = TypeBeatStyle.TypedChar,
                            },
                        },
                        new Drawable[]
                        {
                            timing = new OsuSpriteText
                            {
                                Font = TypeBeatStyle.Mono(13),
                                Colour = TypeBeatStyle.UntypedChar,
                            },
                        },
                        new Drawable[]
                        {
                            new Container
                            {
                                RelativeSizeAxes = Axes.Both,
                                Padding = new MarginPadding { Vertical = 6 },
                                Child = new WordStrip(),
                            },
                        },
                        new Drawable[]
                        {
                            new FillFlowContainer
                            {
                                RelativeSizeAxes = Axes.Both,
                                Direction = FillDirection.Horizontal,
                                Spacing = new Vector2(6, 0),
                                Padding = new MarginPadding { Top = 8 },
                                Children = new Drawable[]
                                {
                                    actionButton("replay (R)", replayActiveLine),
                                    actionButton("add @ playhead", addAtPlayhead),
                                    actionButton("split @ word (S)", splitAtSelectedWord),
                                    actionButton("merge next (M)", mergeNext),
                                    actionButton("delete line", deleteLine),
                                },
                            },
                        },
                    },
                },
            };
        }

        private static Drawable actionButton(string text, System.Action action) => new RoundedButton
        {
            Text = text,
            Action = action,
            Width = 130,
            Height = 30,
        };

        protected override void Update()
        {
            base.Update();

            var line = state.ActiveLine.Value;

            if (line == null || !editorBeatmap.HitObjects.Contains(line))
            {
                header.Text = "no line — click one, or double-click a gap in the timeline to add";
                timing.Text = string.Empty;
                return;
            }

            var l = line.Line;
            header.Text = $"line {line.LineIndex + 1}: {l.RawText}";
            timing.Text = $"start {l.StartTime:0}ms   sung end {l.SingEndTime:0}ms   window end {l.EndTime:0}ms   "
                          + $"{line.Granularity} granularity{(l.Estimated ? "   [estimated]" : string.Empty)}{(l.SealGraceMs > 0 ? $"   grace {l.SealGraceMs:0}ms" : string.Empty)}";
        }

        private void replayActiveLine()
        {
            if (state.ActiveLine.Value is not TypeBeatHitObject line)
                return;

            editorClock.Seek(System.Math.Max(0, line.Line.StartTime - 600));
            state.ReplayStopTime = line.Line.EndTime + 200;
            editorClock.Start();
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
