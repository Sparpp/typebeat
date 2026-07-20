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
    /// song time, with the visible window mirrored from the waveform timeline (scroll and zoom
    /// stay in sync — one window, two viewports). The mouse wheel over this strip zooms that
    /// shared window, anchored at the time under the cursor; over the waveform timeline the
    /// wheel scrolls as usual. Adjacent lines share ONE boundary — the handle at a line's start
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

        // Rebuild signature: line identities + text + unit counts (positions are re-polled).
        private readonly List<(TypeBeatHitObject hitObject, string rawText, int unitCount)> displayed = new List<(TypeBeatHitObject, string, int)>();

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

            // Mirror the waveform timeline's visible window. Its content carries a half-viewport
            // margin on each side (the playhead is pinned to the timeline's CENTRE), so Current
            // maps to the centre time of the view, not its left edge. Centring here is also what
            // keeps the strip snapped to the playhead during playback — the timeline scrolls to
            // track time every frame while the clock runs, and we follow it.
            double windowCentre = timeline.TimeAtPosition(timeline.Current);
            windowLength = Math.Max(1, timeline.VisibleRange);
            windowStart = windowCentre - windowLength / 2;

            var ordered = TypeBeatEditorOperations.OrderedLines(editorBeatmap);

            if (signatureChanged(ordered))
                rebuild(ordered);

            foreach (var band in bandLayer.OfType<LineBand>())
                band.UpdateLayout(this);

            foreach (var block in blockLayer.OfType<WordBlock>())
                block.UpdateLayout(this);

            foreach (var handle in handleLayer.OfType<BoundaryHandle>())
                handle.UpdateLayout(this);

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
                var (hitObject, rawText, unitCount) = displayed[i];

                if (ordered[i] != hitObject || ordered[i].Line.RawText != rawText || ordered[i].Line.Units.Count != unitCount)
                    return true;
            }

            return false;
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
                displayed.Add((hitObject, hitObject.Line.RawText, hitObject.Line.Units.Count));

                bandLayer.Add(new LineBand(this, hitObject, i));

                for (int j = 0; j < hitObject.Line.Units.Count; j++)
                    blockLayer.Add(new WordBlock(this, hitObject, j));

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
            var timeline = screen.TimelineArea?.Timeline;

            if (timeline == null || !timeline.IsLoaded)
                return false;

            // Wheel over the strip zooms the SHARED window, anchored at the time under the
            // cursor — the waveform timeline pans/zooms with it, since it owns the window.
            // (Raw ScrollDelta matches AdjustZoomRelatively's alt+wheel sensitivity.)
            double cursorTime = TimeAt(ToLocalSpace(e.ScreenSpaceMousePosition).X);
            timeline.AdjustZoomRelatively(e.ScrollDelta.Y, timeline.PositionAtTime(cursorTime));
            return true;
        }

        private double dragGrabCentreTime;
        private bool dragWasPlaying;

        protected override bool OnDragStart(DragStartEvent e)
        {
            var timeline = screen.TimelineArea?.Timeline;

            if (timeline == null || !timeline.IsLoaded)
                return false;

            // Grab-and-pan the SHARED window: the strip drives the waveform timeline's scroll,
            // which seeks the clock — the same contract as dragging the waveform itself, so
            // playback pauses for the drag and resumes on release. Blocks/handles consume their
            // own drags before this fires.
            dragGrabCentreTime = windowStart + windowLength / 2;
            dragWasPlaying = editorClock.IsRunning;

            if (dragWasPlaying)
                editorClock.Stop();

            return true;
        }

        protected override void OnDrag(DragEvent e)
        {
            var timeline = screen.TimelineArea?.Timeline;

            if (timeline == null || !timeline.IsLoaded || DrawWidth <= 0)
                return;

            float deltaX = ToLocalSpace(e.ScreenSpaceMousePosition).X - ToLocalSpace(e.ScreenSpaceMouseDownPosition).X;
            double targetCentre = dragGrabCentreTime - deltaX / DrawWidth * windowLength;

            // The timeline's Current maps to the CENTRE time of the view (half-viewport content
            // margins), so scrolling to the target centre's position pans both views together.
            timeline.ScrollTo(timeline.PositionAtTime(targetCentre), false);
        }

        protected override void OnDragEnd(DragEndEvent e)
        {
            if (dragWasPlaying)
                editorClock.Start();
        }

        protected override bool OnDoubleClick(DoubleClickEvent e)
        {
            // Double click on empty space (outside every line band — before the first line or
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
        /// The background band spanning one line's window — shows line extents (alternating
        /// tint), highlights the active line, and clicking it selects the line.
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
                label.Text = unit.Text;
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

            /// <summary>Which part of the block a local X hits — window-style edge zones.</summary>
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
                        // Rigid move — keeps the word's width and just stops at a neighbour.
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
                // Wide hit box (grabbable), thin visual line — the boundary is only 3px on screen
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
