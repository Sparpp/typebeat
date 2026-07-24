// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using typebeat.Game.Graphics.Cursor;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Screens.Edit;
using osuTK;

namespace typebeat.Game.Rulesets.TypeBeat.Edit
{
    /// <summary>
    /// The fine-timing surface as a continuous timeline: every line's word blocks laid out along
    /// song time, hosted as a full-width strip directly beneath the waveform timeline so the two
    /// read as one surface. The strip owns its view window (initial zoom snapshotted from the
    /// waveform timeline): the wheel zooms it anchored at the time under the cursor, dragging
    /// empty space pans it, and it re-centres on the playhead whenever playback starts.
    /// Adjacent lines share ONE boundary: the handle at a line's start
    /// is also the previous line's end (<see cref="TypeBeatEditorOperations.SetLineStart"/>
    /// moves both sides together).
    ///
    /// Word edges resize window-style (horizontal-resize cursor over the grab zone); the block
    /// body moves the word; per-line sung-end flags and alternating line bands complete the
    /// picture. Poll-synced: children are rebuilt only when the line set / text layout changes
    /// and are repositioned in place otherwise, so a block survives its own drag while the
    /// model updates per frame beneath it.
    /// </summary>
    public partial class LyricTimeline : CompositeDrawable, IProvideCursor
    {
        [Resolved]
        private EditorBeatmap editorBeatmap { get; set; } = null!;

        [Resolved]
        private LyricEditState state { get; set; } = null!;

        [Resolved]
        private EditorClock editorClock { get; set; } = null!;

        [Resolved]
        private EditorScreenWithTimeline screen { get; set; } = null!;

        private readonly Container bandLayer;
        private readonly Container blockLayer;
        private readonly Container handleLayer;
        private readonly Box playhead;
        private readonly ResizeCursorContainer resizeCursor;

        private double windowStart, windowLength = 1;

        // Video-editor semantics: this strip owns its horizontal view offset. Panning moves the
        // view WITHOUT seeking, and the playhead is a moving marker. `following` re-centres the
        // view on the playhead; armed at load and re-armed whenever playback starts (so pressing
        // play snaps the view back and then tracks it); any manual pan or seek disengages it.
        private double viewStart;
        private bool following = true;
        private bool wasRunning;
        private bool zoomInitialised;

        private const double zoom_step = 1.2;        // window scale per wheel notch
        private const double min_window_ms = 400;    // deepest zoom-in
        private const double max_window_ms = 120000; // furthest zoom-out

        // Rebuild signature: line identities + text + unit counts (positions are re-polled).
        private readonly List<(TypeBeatHitObject hitObject, string rawText, int unitCount, int syllableCount)> displayed = new List<(TypeBeatHitObject, string, int, int)>();

        private bool edgeHovered;

        public LyricTimeline()
        {
            RelativeSizeAxes = Axes.Both;

            InternalChildren = new Drawable[]
            {
                new Container
                {
                    // Masked so blocks/handles outside the visible window are clipped.
                    RelativeSizeAxes = Axes.Both,
                    Masking = true,
                    Children = new Drawable[]
                    {
                        new Box
                        {
                            RelativeSizeAxes = Axes.Both,
                            Colour = TypeBeatStyle.Background,
                            Alpha = 0.6f,
                        },
                        bandLayer = new Container { RelativeSizeAxes = Axes.Both },
                        blockLayer = new Container { RelativeSizeAxes = Axes.Both },
                        handleLayer = new Container { RelativeSizeAxes = Axes.Both },
                        playhead = new Box
                        {
                            RelativeSizeAxes = Axes.Y,
                            Width = 2,
                            Colour = TypeBeatStyle.TypedChar,
                            Alpha = 0,
                        },
                    },
                },
                // Unmasked so the resize cursor is never clipped at the strip edges.
                resizeCursor = new ResizeCursorContainer { State = { Value = Visibility.Hidden } },
            };
        }

        // --- IProvideCursor: swap in a horizontal-resize cursor while hovering a word edge. ---
        CursorContainer IProvideCursor.Cursor => resizeCursor;
        public bool ProvidingUserCursor => edgeHovered;

        /// <summary>Reported by word blocks: whether the mouse is currently over a resize edge.</summary>
        public void SetEdgeHovered(bool value) => edgeHovered = value;

        protected override void Update()
        {
            base.Update();

            var timeline = screen.TimelineArea?.Timeline;

            if (timeline == null || !timeline.IsLoaded)
                return;

            // The view is owned locally; the strip no longer drives (or reads, beyond the initial
            // zoom snapshot) the shared waveform timeline, so neither panning nor zooming seeks the
            // clock. A rising edge of playback re-arms follow so the view snaps back to the playhead
            // and tracks it each frame; a manual pan/seek has cleared `following`.
            if (!zoomInitialised)
            {
                windowLength = Math.Clamp(timeline.VisibleRange, min_window_ms, max_window_ms);
                zoomInitialised = true;
            }

            if (editorClock.IsRunning && !wasRunning)
                following = true;
            wasRunning = editorClock.IsRunning;

            if (following)
                viewStart = editorClock.CurrentTime - windowLength / 2;

            windowStart = viewStart;

            var ordered = TypeBeatEditorOperations.OrderedLines(editorBeatmap);

            if (signatureChanged(ordered))
                rebuild(ordered);

            foreach (var band in bandLayer.OfType<LineBand>())
                band.UpdateLayout(this);

            foreach (var block in blockLayer.OfType<WordBlock>())
                block.UpdateLayout(this);

            foreach (var handle in handleLayer.OfType<BoundaryHandle>())
                handle.UpdateLayout(this);

            foreach (var syllable in handleLayer.OfType<SyllableHandle>())
                syllable.UpdateLayout(this);

            foreach (var flag in handleLayer.OfType<SingEndFlag>())
                flag.UpdateLayout(this);

            double now = editorClock.CurrentTime;
            bool playheadVisible = now >= windowStart && now <= windowStart + windowLength;
            playhead.Alpha = playheadVisible ? 0.7f : 0;
            if (playheadVisible)
                playhead.X = PositionOf(now);
        }

        private bool signatureChanged(IReadOnlyList<TypeBeatHitObject> ordered)
        {
            if (ordered.Count != displayed.Count)
                return true;

            for (int i = 0; i < ordered.Count; i++)
            {
                var (hitObject, rawText, unitCount, syllableCount) = displayed[i];

                if (ordered[i] != hitObject || ordered[i].Line.RawText != rawText || ordered[i].Line.Units.Count != unitCount
                    || totalSyllableBoundaries(ordered[i].Line) != syllableCount)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Total subdivision boundaries across a line's words: a rebuild trigger (add/remove of a dotted line).</summary>
        private static int totalSyllableBoundaries(LyricLine line)
        {
            int count = 0;

            foreach (var unit in line.Units)
                count += unit.SyllableBoundaries.Count;

            return count;
        }

        private void rebuild(IReadOnlyList<TypeBeatHitObject> ordered)
        {
            displayed.Clear();
            bandLayer.Clear();
            blockLayer.Clear();
            handleLayer.Clear();

            for (int i = 0; i < ordered.Count; i++)
            {
                var hitObject = ordered[i];
                displayed.Add((hitObject, hitObject.Line.RawText, hitObject.Line.Units.Count, totalSyllableBoundaries(hitObject.Line)));

                bandLayer.Add(new LineBand(this, hitObject, i));

                for (int j = 0; j < hitObject.Line.Units.Count; j++)
                {
                    blockLayer.Add(new WordBlock(this, hitObject, j));

                    // One draggable dotted line per syllable subdivision inside the word; sits above
                    // the word block so it takes the drag before the block's move/resize.
                    for (int k = 0; k < hitObject.Line.Units[j].SyllableBoundaries.Count; k++)
                        handleLayer.Add(new SyllableHandle(this, hitObject, j, k));
                }

                // ONE boundary per line start: dragging it moves this line's start and the
                // previous line's end together (SetLineStart maintains both sides).
                handleLayer.Add(new BoundaryHandle(this, hitObject));
                handleLayer.Add(new SingEndFlag(this, hitObject));
            }
        }

        /// <summary>Window-relative time → local X pixels.</summary>
        public float PositionOf(double time) => (float)((time - windowStart) / windowLength * DrawWidth);

        /// <summary>Local X pixels → time.</summary>
        public double TimeAt(float x) => windowStart + x / DrawWidth * windowLength;

        protected override bool OnScroll(ScrollEvent e)
        {
            if (DrawWidth <= 0)
                return false;

            // Zoom the strip's OWN window around the cursor. This never touches the shared waveform
            // timeline or the clock, so zooming does NOT move the playhead. Wheel up = zoom in.
            float cursorX = ToLocalSpace(e.ScreenSpaceMousePosition).X;
            double cursorTime = TimeAt(cursorX); // uses the pre-zoom window

            windowLength = Math.Clamp(windowLength * Math.Pow(zoom_step, -e.ScrollDelta.Y), min_window_ms, max_window_ms);

            // Keep the time under the cursor fixed. While following, Update re-centres on the
            // playhead each frame instead (zoom pivots on the playhead during playback).
            if (!following)
                viewStart = cursorTime - cursorX / DrawWidth * windowLength;

            return true;
        }

        private double dragStartViewStart;

        protected override bool OnDragStart(DragStartEvent e)
        {
            // Grab-and-pan the VIEW only: no seek, no clock stop. The playhead keeps its time and
            // simply slides within the view. Word/line blocks and handles consume their own drags
            // before this fires, so this is only a drag over empty strip space.
            dragStartViewStart = viewStart;
            following = false;
            return true;
        }

        protected override void OnDrag(DragEvent e)
        {
            if (DrawWidth <= 0)
                return;

            float deltaX = ToLocalSpace(e.ScreenSpaceMousePosition).X - ToLocalSpace(e.ScreenSpaceMouseDownPosition).X;
            viewStart = dragStartViewStart - deltaX / DrawWidth * windowLength;
        }

        /// <summary>Move the playhead to a screen-space X on the strip (video-editor seek), leaving
        /// the view put. Shared by empty-space clicks (root) and line-band grey-area clicks.</summary>
        internal void SeekToScreenSpace(Vector2 screenSpacePosition)
        {
            following = false;
            editorClock.SeekSmoothlyTo(TimeAt(ToLocalSpace(screenSpacePosition).X));
        }

        protected override bool OnClick(ClickEvent e)
        {
            // A plain click that reaches the root landed on empty strip space (word blocks/handles
            // consume their own clicks; a click-drag fires OnDrag, never OnClick).
            SeekToScreenSpace(e.ScreenSpaceMousePosition);
            return true;
        }

        protected override bool OnDoubleClick(DoubleClickEvent e)
        {
            // Double click on empty space (outside every line band, before the first line or
            // after the last) authors a new line there; bands/blocks consume their own clicks.
            double time = TimeAt(ToLocalSpace(e.ScreenSpaceMousePosition).X);
            var added = TypeBeatEditorOperations.AddLine(editorBeatmap, time);

            if (added != null)
            {
                state.SelectedLine.Value = added;
                editorClock.SeekSmoothlyTo(added.Line.StartTime);
            }

            return true;
        }

        /// <summary>A CursorContainer whose cursor is a horizontal-resize arrow (window-edge feel).</summary>
        public partial class ResizeCursorContainer : CursorContainer
        {
            protected override Drawable CreateCursor() => new SpriteIcon
            {
                Icon = FontAwesome.Solid.ArrowsAltH,
                Size = new Vector2(18),
                Origin = Anchor.Centre,
                Colour = TypeBeatStyle.TypedChar,
            };
        }

        /// <summary>
        /// The background band spanning one line's window: shows line extents (alternating
        /// tint), highlights the active line. Its grey area is treated as empty space: clicking it
        /// seeks the playhead there (and selects the line); word blocks sit above and take priority.
        /// </summary>
        private partial class LineBand : CompositeDrawable
        {
            private readonly LyricTimeline strip;
            private readonly TypeBeatHitObject hitObject;
            private readonly int lineIndex;
            private readonly Box body;

            [Resolved]
            private LyricEditState state { get; set; } = null!;

            public LineBand(LyricTimeline strip, TypeBeatHitObject hitObject, int lineIndex)
            {
                this.strip = strip;
                this.hitObject = hitObject;
                this.lineIndex = lineIndex;

                RelativeSizeAxes = Axes.Y;
                InternalChild = body = new Box { RelativeSizeAxes = Axes.Both };
            }

            public void UpdateLayout(LyricTimeline parent)
            {
                X = parent.PositionOf(hitObject.Line.StartTime);
                Width = Math.Max(0, parent.PositionOf(hitObject.Line.EndTime) - X);

                bool active = state.ActiveLine.Value == hitObject;

                body.Colour = TypeBeatStyle.PanelBackground.Lighten(active ? 0.6f : lineIndex % 2 == 0 ? 0.15f : 0f);
                body.Alpha = active ? 0.9f : 0.7f;
            }

            protected override bool OnClick(ClickEvent e)
            {
                // Grey band area = empty space: bring the playhead here (word blocks above consume
                // their own clicks), and select the line so the detail panel edits it.
                strip.SeekToScreenSpace(e.ScreenSpaceMousePosition);
                state.SelectedLine.Value = hitObject;
                return true;
            }
        }

        private partial class WordBlock : CompositeDrawable
        {
            // Edge grab zone: fixed pixels, but never more than 40% of a thin block (so a narrow
            // word still has a central move region). Window-style: near an edge = resize.
            private const float edge_px = 7;

            private enum Grab { Move, ResizeStart, ResizeEnd }

            private readonly LyricTimeline strip;
            private readonly TypeBeatHitObject hitObject;
            private readonly int index;

            private readonly Box body;
            private readonly Box progress;
            private readonly OsuSpriteText label;

            [Resolved]
            private EditorBeatmap editorBeatmap { get; set; } = null!;

            [Resolved]
            private LyricEditState state { get; set; } = null!;

            [Resolved]
            private EditorClock editorClock { get; set; } = null!;

            private Grab grab;
            private double grabStart, grabEnd, grabTime;

            // Multi-select drag: captured at drag start so a uniform delta applies to the whole group.
            private bool groupDrag;
            private int[] groupIndices = Array.Empty<int>();
            private double[] groupOrigStart = Array.Empty<double>();
            private double[] groupOrigEnd = Array.Empty<double>();

            public WordBlock(LyricTimeline strip, TypeBeatHitObject hitObject, int index)
            {
                this.strip = strip;
                this.hitObject = hitObject;
                this.index = index;

                Anchor = Anchor.CentreLeft;
                Origin = Anchor.CentreLeft;
                RelativeSizeAxes = Axes.Y;
                Height = 0.55f;
                Masking = true;
                CornerRadius = 4;

                InternalChildren = new Drawable[]
                {
                    body = new Box { RelativeSizeAxes = Axes.Both, Colour = TypeBeatStyle.UntypedChar },
                    progress = new Box { RelativeSizeAxes = Axes.Both, Width = 0, Colour = TypeBeatStyle.SungAccent, Alpha = 0.45f },
                    label = new TruncatingSpriteText
                    {
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        Font = TypeBeatStyle.Mono(16),
                        Colour = TypeBeatStyle.TypedChar,
                    },
                };
            }

            private TimedUnit unit => hitObject.Line.Units[index];

            public void UpdateLayout(LyricTimeline parent)
            {
                if (index >= hitObject.Line.Units.Count)
                    return;

                X = parent.PositionOf(unit.StartTime);
                Width = Math.Max(4, parent.PositionOf(unit.EndTime) - parent.PositionOf(unit.StartTime));
                // Freestyle slots shimmer in the word strip too (fixed-width label font, so the
                // substitution cannot change the word's measured width). The block label is a
                // single sprite, so unlike the line preview it cannot colour the slot separately.
                label.Text = FreestyleGlyphs.Substitute(unit.Text, FreestyleGlyphs.FIXED_WIDTH_POOL, FreestyleGlyphs.TickFor(Time.Current));
                label.MaxWidth = Math.Max(1, Width - 6);
                label.Alpha = Width < 16 ? 0 : 1;

                bool activeLine = state.ActiveLine.Value == hitObject;
                bool selected = activeLine && state.SelectedUnitIndices.Contains(index);
                bool explicitTiming = unit.Source == TimingSource.Explicit;

                body.Colour = selected
                    ? TypeBeatStyle.Caret
                    : explicitTiming ? TypeBeatStyle.SungAccent.Darken(0.4f) : TypeBeatStyle.UntypedChar.Darken(0.2f);

                // Other lines' blocks stay visible but recede so the active line reads at a glance.
                Alpha = activeLine ? 1f : 0.55f;

                // Gameplay-style sweep: fill mirrors the sung position.
                double now = editorClock.CurrentTime;
                float fill = (float)Math.Clamp((now - unit.StartTime) / Math.Max(1, unit.EndTime - unit.StartTime), 0, 1);
                progress.Width = fill;
            }

            /// <summary>Which part of the block a local X hits: window-style edge zones.</summary>
            private Grab grabAt(float localX)
            {
                float zone = Math.Min(edge_px, DrawWidth * 0.4f);

                if (localX <= zone)
                    return Grab.ResizeStart;
                if (localX >= DrawWidth - zone)
                    return Grab.ResizeEnd;

                return Grab.Move;
            }

            // Report edge-hover to the strip so it can show the horizontal-resize cursor.
            protected override bool OnHover(HoverEvent e) => false;

            protected override bool OnMouseMove(MouseMoveEvent e)
            {
                strip.SetEdgeHovered(grabAt(ToLocalSpace(e.ScreenSpaceMousePosition).X) != Grab.Move);
                return false;
            }

            protected override void OnHoverLost(HoverLostEvent e) => strip.SetEdgeHovered(false);

            protected override bool OnClick(ClickEvent e)
            {
                // A block on another line first pulls selection to that line (unit selection is
                // scoped to the active line and is cleared by the line change).
                if (state.ActiveLine.Value != hitObject)
                {
                    state.SelectedLine.Value = hitObject;
                    return true;
                }

                // Ctrl+click toggles a block in/out; Shift+click selects the run from the anchor;
                // a plain click selects just this block. (Shift needs no Ctrl, so multi-select still
                // works even if Ctrl is bound to something else.)
                if (e.ControlPressed)
                    state.ToggleUnit(index);
                else if (e.ShiftPressed && state.SelectedUnitIndex.Value >= 0)
                    state.SelectUnitRange(state.SelectedUnitIndex.Value, index);
                else
                    state.SelectUnit(index);

                return true;
            }

            protected override bool OnDoubleClick(DoubleClickEvent e)
            {
                if (index >= hitObject.Line.Units.Count)
                    return false;

                // Word replay: hear exactly this word.
                editorClock.Seek(Math.Max(0, unit.StartTime - 300));
                state.ReplayStopTime = unit.EndTime + 200;
                editorClock.Start();
                return true;
            }

            protected override bool OnMouseDown(MouseDownEvent e)
            {
                grab = grabAt(ToLocalSpace(e.ScreenSpaceMousePosition).X);
                return true;
            }

            protected override bool OnDragStart(DragStartEvent e)
            {
                if (index >= hitObject.Line.Units.Count)
                    return false;

                grabStart = unit.StartTime;
                grabEnd = unit.EndTime;
                grabTime = strip.TimeAt(strip.ToLocalSpace(e.ScreenSpaceMouseDownPosition).X);

                // Dragging a block on another line pulls the active line over first, so the edit
                // lands with the same state a click would have produced.
                if (state.ActiveLine.Value != hitObject)
                    state.SelectedLine.Value = hitObject;

                var sel = state.SelectedUnitIndices;

                // Dragging a block that is part of a multi-selection drags the whole group; grabbing
                // any other block collapses the selection to just it (standard editor feel).
                groupDrag = state.ActiveLine.Value == hitObject && sel.Count > 1 && sel.Contains(index);

                if (groupDrag)
                {
                    groupIndices = sel.Where(i => i >= 0 && i < hitObject.Line.Units.Count).OrderBy(i => i).ToArray();
                    groupOrigStart = groupIndices.Select(i => hitObject.Line.Units[i].StartTime).ToArray();
                    groupOrigEnd = groupIndices.Select(i => hitObject.Line.Units[i].EndTime).ToArray();
                }
                else
                {
                    state.SelectUnit(index);
                }

                state.BeginInteraction();
                editorBeatmap.BeginChange();
                return true;
            }

            protected override void OnDrag(DragEvent e)
            {
                double cursorTime = strip.TimeAt(strip.ToLocalSpace(e.ScreenSpaceMousePosition).X);
                double delta = cursorTime - grabTime;

                if (groupDrag)
                {
                    // Every selected block moves/stretches by the SAME delta (the mouse distance),
                    // never clipped individually to the cursor.
                    var mode = grab switch
                    {
                        Grab.ResizeStart => TypeBeatEditorOperations.UnitGroupEdit.ResizeStart,
                        Grab.ResizeEnd => TypeBeatEditorOperations.UnitGroupEdit.ResizeEnd,
                        _ => TypeBeatEditorOperations.UnitGroupEdit.Move,
                    };

                    TypeBeatEditorOperations.EditUnitGroup(editorBeatmap, hitObject, groupIndices, groupOrigStart, groupOrigEnd, delta, mode);
                    return;
                }

                switch (grab)
                {
                    case Grab.ResizeStart:
                        // The dragged edge follows the cursor directly (SetUnitTiming clamps it).
                        TypeBeatEditorOperations.SetUnitTiming(editorBeatmap, hitObject, index, cursorTime, grabEnd);
                        break;

                    case Grab.ResizeEnd:
                        TypeBeatEditorOperations.SetUnitTiming(editorBeatmap, hitObject, index, grabStart, cursorTime);
                        break;

                    default:
                        // Rigid move: keeps the word's width and just stops at a neighbour.
                        TypeBeatEditorOperations.MoveUnit(editorBeatmap, hitObject, index, grabStart + delta);
                        break;
                }
            }

            protected override void OnDragEnd(DragEndEvent e)
            {
                groupDrag = false;
                editorBeatmap.EndChange();
                state.EndInteraction();
            }
        }

        /// <summary>
        /// The SHARED boundary at a line's start: dragging it moves this line's start AND the
        /// previous line's end together (one boundary between adjacent lines).
        /// </summary>
        private partial class BoundaryHandle : CompositeDrawable
        {
            private readonly LyricTimeline strip;
            private readonly TypeBeatHitObject hitObject;
            private readonly Box line;

            [Resolved]
            private EditorBeatmap editorBeatmap { get; set; } = null!;

            [Resolved]
            private LyricEditState state { get; set; } = null!;

            public BoundaryHandle(LyricTimeline strip, TypeBeatHitObject hitObject)
            {
                this.strip = strip;
                this.hitObject = hitObject;

                Anchor = Anchor.CentreLeft;
                Origin = Anchor.Centre;
                RelativeSizeAxes = Axes.Y;
                // Wide hit box (grabbable), thin visual line; the boundary is only 3px on screen
                // but the click target is 22px so it is easy to hit.
                Width = 22;

                InternalChild = line = new Box
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    RelativeSizeAxes = Axes.Y,
                    Width = 3,
                    Colour = TypeBeatStyle.Caret,
                };
            }

            public void UpdateLayout(LyricTimeline parent) => X = parent.PositionOf(hitObject.Line.StartTime);

            public override bool HandlePositionalInput => true;

            protected override bool OnHover(HoverEvent e)
            {
                line.Width = 5;
                return false;
            }

            protected override void OnHoverLost(HoverLostEvent e) => line.Width = 3;

            protected override bool OnMouseDown(MouseDownEvent e) => true;

            protected override bool OnDragStart(DragStartEvent e)
            {
                state.BeginInteraction();
                editorBeatmap.BeginChange();
                return true;
            }

            protected override void OnDrag(DragEvent e)
            {
                TypeBeatEditorOperations.SetLineStart(editorBeatmap, hitObject, strip.TimeAt(strip.ToLocalSpace(e.ScreenSpaceMousePosition).X));
            }

            protected override void OnDragEnd(DragEndEvent e)
            {
                editorBeatmap.EndChange();
                state.EndInteraction();
            }
        }

        /// <summary>
        /// A draggable DOTTED line inside a word block marking one syllable subdivision. Dragging it
        /// re-times that boundary (clamped inside the word); double-clicking removes it. Added by the
        /// "subdivide word" action, one per boundary. Sits in the handle layer above the word blocks.
        /// </summary>
        private partial class SyllableHandle : CompositeDrawable
        {
            private readonly LyricTimeline strip;
            private readonly TypeBeatHitObject hitObject;
            private readonly int unitIndex;
            private readonly int boundaryIndex;
            private readonly Container visual;

            [Resolved]
            private EditorBeatmap editorBeatmap { get; set; } = null!;

            [Resolved]
            private LyricEditState state { get; set; } = null!;

            public SyllableHandle(LyricTimeline strip, TypeBeatHitObject hitObject, int unitIndex, int boundaryIndex)
            {
                this.strip = strip;
                this.hitObject = hitObject;
                this.unitIndex = unitIndex;
                this.boundaryIndex = boundaryIndex;

                Anchor = Anchor.CentreLeft;
                Origin = Anchor.Centre;
                RelativeSizeAxes = Axes.Y;
                // Match the word block's height so the dotted line reads as splitting the block, with
                // a wide invisible grab zone (like BoundaryHandle) around a thin dotted visual.
                Height = 0.55f;
                Width = 16;

                // Dotted line: a column of short dashes clipped to the block height by the masking
                // container (there is no dashed-line drawable, so it is tiled from boxes). Enough
                // dashes to overflow any row height; masking trims the rest.
                var dashes = new FillFlowContainer
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical,
                    Spacing = new Vector2(0, 3),
                };

                for (int i = 0; i < 40; i++)
                {
                    dashes.Add(new Box
                    {
                        RelativeSizeAxes = Axes.X,
                        Height = 4,
                        Colour = TypeBeatStyle.TypedChar,
                    });
                }

                InternalChild = visual = new Container
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    RelativeSizeAxes = Axes.Y,
                    Width = 2,
                    Masking = true,
                    Alpha = 0.85f,
                    Child = dashes,
                };
            }

            public override bool HandlePositionalInput => true;

            /// <summary>Current boundary time, or null when this handle's word/boundary no longer exists.</summary>
            private double? boundaryTime()
            {
                var units = hitObject.Line.Units;

                if (unitIndex < 0 || unitIndex >= units.Count)
                    return null;

                var boundaries = units[unitIndex].SyllableBoundaries;

                if (boundaryIndex < 0 || boundaryIndex >= boundaries.Count)
                    return null;

                return boundaries[boundaryIndex];
            }

            public void UpdateLayout(LyricTimeline parent)
            {
                double? time = boundaryTime();

                // Stale handle (an undo/edit dropped this boundary before the next rebuild); hide it.
                Alpha = time.HasValue ? 1 : 0;

                if (time.HasValue)
                    X = parent.PositionOf(time.Value);
            }

            protected override bool OnHover(HoverEvent e)
            {
                visual.Width = 4;
                visual.Alpha = 1;
                return false;
            }

            protected override void OnHoverLost(HoverLostEvent e)
            {
                visual.Width = 2;
                visual.Alpha = 0.85f;
            }

            protected override bool OnMouseDown(MouseDownEvent e) => true;

            protected override bool OnClick(ClickEvent e)
            {
                // Pull selection to this word so the detail panel / subdivide button target it.
                if (state.ActiveLine.Value != hitObject)
                    state.SelectedLine.Value = hitObject;
                else
                    state.SelectUnit(unitIndex);

                return true;
            }

            protected override bool OnDoubleClick(DoubleClickEvent e)
            {
                TypeBeatEditorOperations.RemoveSyllableBoundary(editorBeatmap, hitObject, unitIndex, boundaryIndex);
                return true;
            }

            protected override bool OnDragStart(DragStartEvent e)
            {
                state.BeginInteraction();
                editorBeatmap.BeginChange();
                return true;
            }

            protected override void OnDrag(DragEvent e)
            {
                TypeBeatEditorOperations.SetSyllableBoundary(editorBeatmap, hitObject, unitIndex, boundaryIndex,
                    strip.TimeAt(strip.ToLocalSpace(e.ScreenSpaceMousePosition).X));
            }

            protected override void OnDragEnd(DragEndEvent e)
            {
                editorBeatmap.EndChange();
                state.EndInteraction();
            }
        }

        /// <summary>The sung-end flag (persisted end_ms): where a line's vocal stops.</summary>
        private partial class SingEndFlag : CompositeDrawable
        {
            private readonly LyricTimeline strip;
            private readonly TypeBeatHitObject hitObject;

            [Resolved]
            private EditorBeatmap editorBeatmap { get; set; } = null!;

            [Resolved]
            private LyricEditState state { get; set; } = null!;

            public SingEndFlag(LyricTimeline strip, TypeBeatHitObject hitObject)
            {
                this.strip = strip;
                this.hitObject = hitObject;

                Anchor = Anchor.TopLeft;
                Origin = Anchor.TopCentre;
                RelativeSizeAxes = Axes.Y;
                // Wide hit box, thin visual (a 2px stem with a small flag), same as BoundaryHandle.
                Width = 20;

                InternalChildren = new Drawable[]
                {
                    new Box
                    {
                        Anchor = Anchor.TopCentre,
                        Origin = Anchor.TopCentre,
                        RelativeSizeAxes = Axes.Y,
                        Width = 2,
                        Colour = TypeBeatStyle.SungAccent,
                    },
                    new Box
                    {
                        Anchor = Anchor.TopCentre,
                        Origin = Anchor.TopLeft,
                        Size = new Vector2(8, 6),
                        Colour = TypeBeatStyle.SungAccent,
                    },
                };
            }

            public void UpdateLayout(LyricTimeline parent) => X = parent.PositionOf(hitObject.Line.SingEndTime);

            public override bool HandlePositionalInput => true;

            protected override bool OnMouseDown(MouseDownEvent e) => true;

            protected override bool OnDragStart(DragStartEvent e)
            {
                state.BeginInteraction();
                editorBeatmap.BeginChange();
                return true;
            }

            protected override void OnDrag(DragEvent e)
            {
                TypeBeatEditorOperations.SetSingEnd(editorBeatmap, hitObject, strip.TimeAt(strip.ToLocalSpace(e.ScreenSpaceMousePosition).X));
            }

            protected override void OnDragEnd(DragEndEvent e)
            {
                editorBeatmap.EndChange();
                state.EndInteraction();
            }
        }
    }
}
