// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Input.Events;
using typebeat.Game.Graphics.Containers;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Screens.Edit;
using osuTK;

namespace typebeat.Game.Rulesets.TypeBeat.Edit
{
    /// <summary>
    /// The scrollable list of all lyric lines: index, time, and an editable text box per line,
    /// the fastest surface for sweeping text edits ("yeah" → "yeaaaaaaaah") across a whole song.
    /// Clicking a row selects the line and seeks to it. Poll-synced: rows rebuild only when the
    /// line set changes identity; labels refresh in place; a focused text box is never stomped.
    /// </summary>
    public partial class LineListPanel : CompositeDrawable
    {
        [Resolved]
        private EditorBeatmap editorBeatmap { get; set; } = null!;

        [Resolved]
        private LyricEditState state { get; set; } = null!;

        private readonly FillFlowContainer<LineRow> rows;
        private readonly OsuScrollContainer scroll;
        private readonly List<TypeBeatHitObject> displayed = new List<TypeBeatHitObject>();

        public LineListPanel()
        {
            RelativeSizeAxes = Axes.Both;

            InternalChildren = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = TypeBeatStyle.PanelBackground,
                    Alpha = 0.6f,
                },
                scroll = new OsuScrollContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    ScrollbarOverlapsContent = false,
                    Child = rows = new FillFlowContainer<LineRow>
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Direction = FillDirection.Vertical,
                        Spacing = new Vector2(0, 2),
                        Padding = new MarginPadding(4),
                    },
                },
            };
        }

        protected override void Update()
        {
            base.Update();

            var current = TypeBeatEditorOperations.OrderedLines(editorBeatmap);

            if (!current.SequenceEqual(displayed))
            {
                displayed.Clear();
                displayed.AddRange(current);

                rows.Clear();

                foreach (var hitObject in current)
                    rows.Add(new LineRow(hitObject));
            }
        }

        /// <summary>Brings the active line's row into view (called by the screen on line change).</summary>
        public void ScrollToActive()
        {
            var row = rows.FirstOrDefault(r => r.HitObject == state.ActiveLine.Value);

            if (row != null)
                scroll.ScrollIntoView(row);
        }

        private partial class LineRow : CompositeDrawable
        {
            public readonly TypeBeatHitObject HitObject;

            [Resolved]
            private EditorBeatmap editorBeatmap { get; set; } = null!;

            [Resolved]
            private LyricEditState state { get; set; } = null!;

            [Resolved]
            private EditorClock editorClock { get; set; } = null!;

            private readonly Box background;
            private OsuSpriteText indexText = null!;
            private OsuSpriteText timeText = null!;
            private OsuTextBox textBox = null!;

            public LineRow(TypeBeatHitObject hitObject)
            {
                HitObject = hitObject;

                RelativeSizeAxes = Axes.X;
                Height = 34;
                Masking = true;
                CornerRadius = 4;

                InternalChild = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Children = new Drawable[]
                    {
                        background = new Box
                        {
                            RelativeSizeAxes = Axes.Both,
                            Colour = TypeBeatStyle.Background,
                            Alpha = 0.9f,
                        },
                    },
                };
            }

            [BackgroundDependencyLoader]
            private void load()
            {
                ((Container)InternalChild).Add(new GridContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    ColumnDimensions = new[]
                    {
                        new Dimension(GridSizeMode.Absolute, 34),
                        new Dimension(GridSizeMode.Absolute, 76),
                        new Dimension(),
                    },
                    Content = new[]
                    {
                        new Drawable[]
                        {
                            indexText = new OsuSpriteText
                            {
                                Anchor = Anchor.Centre,
                                Origin = Anchor.Centre,
                                Font = TypeBeatStyle.Mono(13),
                                Colour = TypeBeatStyle.UntypedChar,
                            },
                            timeText = new OsuSpriteText
                            {
                                Anchor = Anchor.CentreLeft,
                                Origin = Anchor.CentreLeft,
                                Font = TypeBeatStyle.Mono(13),
                                Colour = TypeBeatStyle.UntypedChar,
                            },
                            textBox = new OsuTextBox
                            {
                                Anchor = Anchor.CentreLeft,
                                Origin = Anchor.CentreLeft,
                                RelativeSizeAxes = Axes.X,
                                Height = 28,
                                FontSize = 15,
                                CommitOnFocusLost = true,
                            },
                        },
                    },
                });

                textBox.OnCommit += (_, _) => commitText();
            }

            private void commitText()
            {
                if (!editorBeatmap.HitObjects.Contains(HitObject))
                    return;

                if (!TypeBeatEditorOperations.SetLineText(editorBeatmap, HitObject, textBox.Text))
                {
                    // Normalized to empty; refuse and flash (delete the line instead).
                    textBox.Text = HitObject.Line.RawText;
                    background.FlashColour(TypeBeatStyle.ErrorChar, 400, Easing.OutQuint);
                }
            }

            protected override void Update()
            {
                base.Update();

                indexText.Text = (HitObject.LineIndex + 1).ToString();
                timeText.Text = formatTime(HitObject.Line.StartTime);

                if (!textBox.HasFocus && textBox.Text != HitObject.Line.RawText)
                    textBox.Text = HitObject.Line.RawText;

                bool active = state.ActiveLine.Value == HitObject;
                bool multiSelected = state.MultiSelectedLines.Contains(HitObject);
                background.Colour = active
                    ? TypeBeatStyle.PanelBackground.Lighten(0.5f)
                    : multiSelected
                        ? TypeBeatStyle.PanelBackground.Lighten(0.25f)
                        : TypeBeatStyle.Background;
            }

            private static string formatTime(double ms)
            {
                int total = (int)(ms / 1000);
                return $"{total / 60}:{total % 60:00}.{(int)(ms % 1000):000}";
            }

            protected override bool OnClick(ClickEvent e)
            {
                // Ctrl/Shift build a multi-selection (for timing copy/paste) without seeking;
                // yanking the playhead mid-selection would fight the user.
                if (e.ControlPressed)
                {
                    state.ToggleLine(HitObject);
                    return true;
                }

                if (e.ShiftPressed)
                {
                    state.SelectLineRange(TypeBeatEditorOperations.OrderedLines(editorBeatmap), HitObject);
                    return true;
                }

                state.SelectLine(HitObject);
                editorClock.SeekSmoothlyTo(HitObject.Line.StartTime);
                return true;
            }
        }
    }
}
