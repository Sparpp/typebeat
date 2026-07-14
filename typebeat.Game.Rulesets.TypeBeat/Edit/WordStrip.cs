// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using typebeat.Game.Graphics;
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
    /// The fine-timing surface for the ACTIVE line: word blocks over a window spanning the line
    /// (±15% context), which the mouse wheel zooms and a background drag pans. Word edges resize
    /// like a window's edges (cursor turns into a horizontal-resize arrow over the grab zone);
    /// the block body moves the word. A line-start boundary handle, a sung-end flag, and a
    /// gameplay-style playhead sweep complete the picture.
    ///
    /// Poll-synced: blocks are rebuilt only when the active line's identity or token layout
    /// changes and are repositioned in place otherwise, so a block survives its own drag while
    /// the model updates per frame beneath it.
    /// </summary>
    public partial class WordStrip : CompositeDrawable, IProvideCursor
    {
        private const double max_zoom = 12;

        [Resolved]
        private EditorBeatmap editorBeatmap { get; set; } = null!;

        [Resolved]
        private LyricEditState state { get; set; } = null!;

        [Resolved]
        private EditorClock editorClock { get; set; } = null!;

        private readonly Container blockLayer;
        private readonly Box windowBackground;
        private readonly Box playhead;
        private readonly BoundaryHandle startHandle;
        private readonly SingEndFlag singEndFlag;
        private readonly ResizeCursorContainer resizeCursor;

        private TypeBeatHitObject? displayedObject;
        private string? displayedTokenSignature;
        private double windowStart, windowLength;

        // While a boundary handle is dragged the view is held stable (the handle moves, the
        // strip does not) — otherwise moving line.StartTime/EndTime rescales the whole window
        // every frame, zooming the view and shoving the word blocks around.
        private bool windowFrozen;
        private double frozenWindowStart, frozenWindowLength;

        // User view controls (reset when the active line changes): 1 = fit line, higher = zoom in.
        private double userZoom = 1;
        private double? panCentre;

        // Base (zoom == 1) window derived from the active line; captured each frame for the
        // cursor-anchored zoom + pan maths.
        private double baseStart, baseLength, resolvedCentre;

        private bool edgeHovered;

        public WordStrip()
        {
            RelativeSizeAxes = Axes.Both;

            InternalChildren = new Drawable[]
            {
                new Container
                {
                    // Masked so word blocks/handles outside the (possibly zoomed) window are clipped.
                    RelativeSizeAxes = Axes.Both,
                    Masking = true,
                    Children = new Drawable[]
                    {
                        windowBackground = new Box
                        {
                            RelativeSizeAxes = Axes.Both,
                            Colour = TypeBeatStyle.PanelBackground,
                        },
                        blockLayer = new Container { RelativeSizeAxes = Axes.Both },
                        startHandle = new BoundaryHandle(this),
                        singEndFlag = new SingEndFlag(this),
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

        private TypeBeatHitObject? active => state.ActiveLine.Value;

        protected override void Update()
        {
            base.Update();

            var line = active;

            if (line == null || !editorBeatmap.HitObjects.Contains(line))
            {
                blockLayer.Clear();
                displayedObject = null;
                displayedTokenSignature = null;
                startHandle.Alpha = singEndFlag.Alpha = playhead.Alpha = 0;
                windowBackground.Alpha = 0.3f;
                edgeHovered = false;
                return;
            }

            windowBackground.Alpha = 1;

            string signature = line.Line.RawText;

            if (displayedObject != line || displayedTokenSignature != signature)
            {
                bool lineChanged = displayedObject != line;
                displayedObject = line;
                displayedTokenSignature = signature;

                // Fit-to-line resets the view; a mere text edit on the same line keeps the zoom.
                if (lineChanged)
                {
                    userZoom = 1;
                    panCentre = null;
                }

                rebuildBlocks(line);
            }

            if (windowFrozen)
            {
                // Boundary drag in progress: hold the window exactly as it was grabbed so the
                // handle glides and the blocks stay put (matches the sung-end flag's feel).
                windowStart = frozenWindowStart;
                windowLength = frozenWindowLength;
                baseStart = windowStart;
                baseLength = windowLength;
                resolvedCentre = windowStart + windowLength / 2;
            }
            else
            {
                // Base window: the line span padded 15% each side (min 400ms padding for tiny lines).
                double lineSpan = line.Line.EndTime - line.Line.StartTime;
                double pad = Math.Max(400, lineSpan * 0.15);
                baseStart = line.Line.StartTime - pad;
                baseLength = lineSpan + pad * 2;

                // Apply zoom + pan, clamped so the view can't stray past the padded line window.
                windowLength = baseLength / userZoom;
                double lineCentre = line.Line.StartTime + lineSpan / 2;
                double centre = panCentre ?? lineCentre;
                double lo = baseStart + windowLength / 2;
                double hi = baseStart + baseLength - windowLength / 2;
                resolvedCentre = lo <= hi ? Math.Clamp(centre, lo, hi) : (baseStart + baseLength / 2);
                windowStart = resolvedCentre - windowLength / 2;
            }

            // Reposition everything in place from the current (possibly mid-drag) model.
            foreach (var block in blockLayer.OfType<WordBlock>())
                block.UpdateLayout(this);

            startHandle.Alpha = 1;
            startHandle.X = PositionOf(line.Line.StartTime);
            singEndFlag.Alpha = 1;
            singEndFlag.X = PositionOf(line.Line.SingEndTime);

            double now = editorClock.CurrentTime;
            bool playheadVisible = now >= windowStart && now <= windowStart + windowLength;
            playhead.Alpha = playheadVisible ? 0.7f : 0;
            if (playheadVisible)
                playhead.X = PositionOf(now);
        }

        protected override bool OnScroll(ScrollEvent e)
        {
            if (active == null || DrawWidth <= 0)
                return false;

            float cursorX = ToLocalSpace(e.ScreenSpaceMousePosition).X;
            double cursorTime = TimeAt(cursorX);

            userZoom = Math.Clamp(userZoom * Math.Pow(1.2, e.ScrollDelta.Y), 1, max_zoom);

            // Keep the time under the cursor fixed: solve windowStart + f*newLength == cursorTime.
            double newLength = baseLength / userZoom;
            float f = cursorX / DrawWidth;
            panCentre = cursorTime + newLength * (0.5 - f);
            return true;
        }

        private double panGrabCentre;

        protected override bool OnDragStart(DragStartEvent e)
        {
            // Only pan when zoomed in (otherwise the whole line is already visible).
            if (userZoom <= 1)
                return false;

            panGrabCentre = resolvedCentre;
            return true;
        }

        protected override void OnDrag(DragEvent e)
        {
            if (DrawWidth <= 0)
                return;

            double deltaX = ToLocalSpace(e.ScreenSpaceMousePosition).X - ToLocalSpace(e.ScreenSpaceMouseDownPosition).X;
            panCentre = panGrabCentre - deltaX / DrawWidth * windowLength;
        }

        private void rebuildBlocks(TypeBeatHitObject line)
        {
            blockLayer.Clear();

            for (int i = 0; i < line.Line.Units.Count; i++)
                blockLayer.Add(new WordBlock(this, line, i));
        }

        /// <summary>Window-relative time → local X pixels.</summary>
        public float PositionOf(double time) => (float)((time - windowStart) / windowLength * DrawWidth);

        /// <summary>Local X pixels → time.</summary>
        public double TimeAt(float x) => windowStart + x / DrawWidth * windowLength;

        /// <summary>
        /// Holds the current view window steady for the duration of a boundary-handle drag, so the
        /// handle moves against a stationary strip instead of the whole view rescaling under it.
        /// </summary>
        public void FreezeWindow()
        {
            frozenWindowStart = windowStart;
            frozenWindowLength = windowLength;
            windowFrozen = true;
        }

        public void ThawWindow() => windowFrozen = false;

        /// <summary>
        /// While frozen, pans the held window just enough to keep <paramref name="time"/> in view —
        /// so a boundary dragged toward the edge scrolls the strip rather than sliding out of sight.
        /// </summary>
        public void KeepVisible(double time)
        {
            if (!windowFrozen || frozenWindowLength <= 0)
                return;

            double margin = frozenWindowLength * 0.12;

            if (time < frozenWindowStart + margin)
                frozenWindowStart = time - margin;
            else if (time > frozenWindowStart + frozenWindowLength - margin)
                frozenWindowStart = time - frozenWindowLength + margin;
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

        private partial class WordBlock : CompositeDrawable
        {
            // Edge grab zone: fixed pixels, but never more than 40% of a thin block (so a narrow
            // word still has a central move region). Window-style: near an edge = resize.
            private const float edge_px = 7;

            private enum Grab { Move, ResizeStart, ResizeEnd }

            private readonly WordStrip strip;
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

            public WordBlock(WordStrip strip, TypeBeatHitObject hitObject, int index)
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

            public void UpdateLayout(WordStrip parent)
            {
                if (index >= hitObject.Line.Units.Count)
                    return;

                X = parent.PositionOf(unit.StartTime);
                Width = Math.Max(4, parent.PositionOf(unit.EndTime) - parent.PositionOf(unit.StartTime));
                label.Text = unit.Text;
                label.MaxWidth = Math.Max(1, Width - 6);
                label.Alpha = Width < 16 ? 0 : 1;

                bool selected = state.SelectedUnitIndices.Contains(index) && state.ActiveLine.Value == hitObject;
                bool explicitTiming = unit.Source == TimingSource.Explicit;

                body.Colour = selected
                    ? TypeBeatStyle.Caret
                    : explicitTiming ? TypeBeatStyle.SungAccent.Darken(0.4f) : TypeBeatStyle.UntypedChar.Darken(0.2f);

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

                var sel = state.SelectedUnitIndices;

                // Dragging a block that is part of a multi-selection drags the whole group; grabbing
                // any other block collapses the selection to just it (standard editor feel).
                groupDrag = sel.Count > 1 && sel.Contains(index);

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

        /// <summary>The line-start grabber: dragging it moves this line's start AND the previous line's end.</summary>
        private partial class BoundaryHandle : CompositeDrawable
        {
            private readonly WordStrip strip;
            private readonly Box line;

            [Resolved]
            private EditorBeatmap editorBeatmap { get; set; } = null!;

            [Resolved]
            private LyricEditState state { get; set; } = null!;

            public BoundaryHandle(WordStrip strip)
            {
                this.strip = strip;

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
                if (state.ActiveLine.Value == null)
                    return false;

                state.BeginInteraction();
                strip.FreezeWindow();
                editorBeatmap.BeginChange();
                return true;
            }

            protected override void OnDrag(DragEvent e)
            {
                if (state.ActiveLine.Value is TypeBeatHitObject line)
                {
                    TypeBeatEditorOperations.SetLineStart(editorBeatmap, line, strip.TimeAt(strip.ToLocalSpace(e.ScreenSpaceMousePosition).X));
                    strip.KeepVisible(line.Line.StartTime);
                }
            }

            protected override void OnDragEnd(DragEndEvent e)
            {
                editorBeatmap.EndChange();
                strip.ThawWindow();
                state.EndInteraction();
            }
        }

        /// <summary>The sung-end flag (persisted end_ms): where the vocal stops.</summary>
        private partial class SingEndFlag : CompositeDrawable
        {
            private readonly WordStrip strip;

            [Resolved]
            private EditorBeatmap editorBeatmap { get; set; } = null!;

            [Resolved]
            private LyricEditState state { get; set; } = null!;

            public SingEndFlag(WordStrip strip)
            {
                this.strip = strip;

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

            public override bool HandlePositionalInput => true;

            protected override bool OnMouseDown(MouseDownEvent e) => true;

            protected override bool OnDragStart(DragStartEvent e)
            {
                if (state.ActiveLine.Value == null)
                    return false;

                state.BeginInteraction();
                strip.FreezeWindow();
                editorBeatmap.BeginChange();
                return true;
            }

            protected override void OnDrag(DragEvent e)
            {
                if (state.ActiveLine.Value is TypeBeatHitObject line)
                {
                    TypeBeatEditorOperations.SetSingEnd(editorBeatmap, line, strip.TimeAt(strip.ToLocalSpace(e.ScreenSpaceMousePosition).X));
                    strip.KeepVisible(line.Line.SingEndTime);
                }
            }

            protected override void OnDragEnd(DragEndEvent e)
            {
                editorBeatmap.EndChange();
                strip.ThawWindow();
                state.EndInteraction();
            }
        }
    }
}
