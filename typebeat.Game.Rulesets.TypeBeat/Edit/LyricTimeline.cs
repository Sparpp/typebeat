// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using typebeat.Game.Graphics.Cursor;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
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
    /// Word edges resize window-style (horizontal-resize cursor over the grab zone), the
    /// neighbouring word acting as a wall; the block body moves the word. Holding SHIFT while
    /// dragging an edge two TOUCHING words share drags that boundary instead, moving the left
    /// word's end and the right word's start together
    /// (<see cref="TypeBeatEditorOperations.SetSharedUnitBoundary"/>), the word-level echo of the
    /// line boundary above. Per-line sung-end flags and alternating line bands complete the
    /// picture. Poll-synced: children are rebuilt only when the line set / text layout changes
    /// and are repositioned in place otherwise, so a block survives its own drag while the
    /// model updates per frame beneath it.
    ///
    /// A word carrying AUTHORED PAUSES (the Map Editor's Insert Pause) wears a greyed band across each
    /// stretch of the block a rest covers, with a draggable handle on both of its edges (see
    /// <see cref="PauseRegion"/>): a rest is a piece of the word's own timing, so it is adjusted here
    /// rather than in the text, and a word may take several - one per breath.
    /// SHIFT+Dragging a DOTTED line promotes that subdivision into one, the span it was dragged across
    /// becoming the rest (see <see cref="TypeBeatEditorOperations.ExtendSubdivisionIntoPause"/>).
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
        private readonly TapGhostLayer ghostLayer;
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

        // A pending one-shot view pan (LyricEditState.RequestViewSnap): applied in Update, once the
        // window length is known, so a request arriving before the strip has sized itself still
        // centres correctly. It does NOT re-arm follow; playback remains the only thing that does.
        private double? pendingViewSnap;

        private const double zoom_step = 1.2;        // window scale per wheel notch
        private const double min_window_ms = 400;    // deepest zoom-in
        private const double max_window_ms = 120000; // furthest zoom-out

        // Rebuild signature: line identities + text + unit / boundary / pause counts (positions are
        // re-polled, but a HANDLE appearing or vanishing needs a rebuild).
        private readonly List<(TypeBeatHitObject hitObject, string rawText, int unitCount, int syllableCount, int pauseCount)> displayed = new List<(TypeBeatHitObject, string, int, int, int)>();

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
                        ghostLayer = new TapGhostLayer(),
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

        /// <summary>
        /// How many authored-pause regions the strip is drawing right now. A test seam rather than a
        /// public surface: the region type is private, so a visual test has no other way to pin that
        /// adding or removing a rest actually REBUILDS the strip rather than only moving the model.
        /// </summary>
        internal int PauseRegionCount => handleLayer.OfType<PauseRegion>().Count();

        /// <summary>
        /// The width, in pixels, of the live rest PREVIEW a SHIFT+drag is drawing (see
        /// <see cref="SyllableHandle.updatePromotionBand"/>), or 0 when none is showing. A test seam, for
        /// the same reason <see cref="PauseRegionCount"/> is one: the handle that draws it is private, so
        /// a visual test has no other way to pin that sweeping a dotted line really does show the breath
        /// GROWING rather than only applying one on release.
        /// </summary>
        internal float PromotionPreviewWidth
            => handleLayer.OfType<SyllableHandle>().Select(handle => handle.PromotionBandWidth).DefaultIfEmpty(0).Max();

        /// <summary>How opaque that preview currently is (see <see cref="PromotionPreviewWidth"/>).</summary>
        internal float PromotionPreviewAlpha
            => handleLayer.OfType<SyllableHandle>().Select(handle => handle.PromotionBandAlpha).DefaultIfEmpty(0).Max();

        protected override void LoadComplete()
        {
            base.LoadComplete();
            state.ViewSnapRequested += requestViewSnap;
        }

        /// <summary>
        /// Brings <paramref name="time"/> into view with a one-shot pan (the caret is not touched).
        /// Follow is disengaged exactly as a manual pan would, so the view then stays where it was
        /// put until playback re-arms it.
        /// </summary>
        private void requestViewSnap(double time) => pendingViewSnap = time;

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

            // A requested pan wins over the current follow state for this frame and disengages
            // follow, so the view lands on the asked-for time whichever way it was tracking before.
            if (pendingViewSnap is double snapTo)
            {
                following = false;
                viewStart = snapTo - windowLength / 2;
                pendingViewSnap = null;
            }

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

            foreach (var region in handleLayer.OfType<PauseRegion>())
                region.UpdateLayout(this);

            // A live tap-timing pass has committed nothing yet; its taps show as ghosts on top.
            ghostLayer.UpdateGhosts(state.TapSession?.Taps, PositionOf);

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
                var (hitObject, rawText, unitCount, syllableCount, pauseCount) = displayed[i];

                if (ordered[i] != hitObject || ordered[i].Line.RawText != rawText || ordered[i].Line.Units.Count != unitCount
                    || totalSyllableBoundaries(ordered[i].Line) != syllableCount
                    || totalPauses(ordered[i].Line) != pauseCount)
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

        /// <summary>
        /// How many pauses a line carries: the rebuild trigger for the greyed bands and their edge
        /// handles. A COUNT is enough because every operation that writes a pause touches exactly one
        /// word at a time - a rest moving between words cannot leave the count the same without the
        /// line's text or unit count moving with it, and both of those are already in the signature.
        /// </summary>
        private static int totalPauses(LyricLine line)
        {
            int count = 0;

            foreach (var unit in line.Units)
                count += unit.Pauses.Count;

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
                displayed.Add((hitObject, hitObject.Line.RawText, hitObject.Line.Units.Count,
                    totalSyllableBoundaries(hitObject.Line), totalPauses(hitObject.Line)));

                bandLayer.Add(new LineBand(this, hitObject, i));

                for (int j = 0; j < hitObject.Line.Units.Count; j++)
                {
                    blockLayer.Add(new WordBlock(this, hitObject, j));

                    // One draggable dotted line per syllable subdivision inside the word; sits above
                    // the word block so it takes the drag before the block's move/resize.
                    for (int k = 0; k < hitObject.Line.Units[j].SyllableBoundaries.Count; k++)
                        handleLayer.Add(new SyllableHandle(this, hitObject, j, k));

                    // One greyed band with two edge handles per authored pause, same layer for the same
                    // reason: a rest is adjusted in place, not as a word move.
                    for (int p = 0; p < hitObject.Line.Units[j].Pauses.Count; p++)
                        handleLayer.Add(new PauseRegion(this, hitObject, j, p));
                }

                // ONE boundary per line start: dragging it moves this line's start and the
                // previous line's end together (SetLineStart maintains both sides).
                // There is deliberately no sung-end marker: since backlog 246 a line's end_ms is
                // derived from its last word's end, so the last word BLOCK is that marker.
                handleLayer.Add(new BoundaryHandle(this, hitObject));
            }
        }

        /// <summary>Window-relative time → local X pixels.</summary>
        public float PositionOf(double time) => (float)((time - windowStart) / windowLength * DrawWidth);

        /// <summary>Local X pixels → time.</summary>
        public double TimeAt(float x) => windowStart + x / DrawWidth * windowLength;

        /// <summary>
        /// The word-boundary magnet: with "snap to caret" armed, a dragged word edge within
        /// <see cref="EditorSnapMagnet.RADIUS_PX"/> of the caret lands exactly on the caret,
        /// otherwise it follows the cursor untouched. The radius is converted through the strip's
        /// CURRENT scale, so the pull covers the same few pixels at every zoom.
        /// </summary>
        internal double MagnetToCaret(double time)
        {
            if (!state.SnapToCaret.Value || DrawWidth <= 0)
                return time;

            return EditorSnapMagnet.Magnet(time, editorClock.CurrentTime, EditorSnapMagnet.RADIUS_PX / DrawWidth * windowLength);
        }

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
            => SeekTo(TimeAt(ToLocalSpace(screenSpacePosition).X));

        /// <summary>Move the playhead to a time, leaving the view put (see <see cref="SeekToScreenSpace"/>).</summary>
        internal void SeekTo(double time)
        {
            following = false;
            editorClock.SeekSmoothlyTo(time);
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

        protected override void Dispose(bool isDisposing)
        {
            // The shared state outlives this drawable (the screen owns it), so the handler has to go.
            if (state.IsNotNull())
                state.ViewSnapRequested -= requestViewSnap;

            base.Dispose(isDisposing);
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
                // During a tap-timing pass the strip shows ONLY the section being timed. The time
                // axis and playhead stay (the mapper is listening along it), but every band, block
                // and handle belonging to another line is fully hidden, not dimmed.
                if (state.HiddenByTapScope(hitObject))
                {
                    Alpha = 0;
                    return;
                }

                Alpha = 1;
                X = parent.PositionOf(hitObject.Line.StartTime);
                Width = Math.Max(0, parent.PositionOf(hitObject.Line.EndTime) - X);

                bool active = state.ActiveLine.Value == hitObject;

                // Same section tint as the boundaries band: a ctrl/shift-picked run of lines stays
                // visible on the fine-timing strip while the mapper works on it.
                bool sectioned = state.MultiSelectedLines.Contains(hitObject);

                body.Colour = TypeBeatStyle.PanelBackground.Lighten(active ? 0.6f : sectioned ? 0.35f : lineIndex % 2 == 0 ? 0.15f : 0f);
                body.Alpha = active || sectioned ? 0.9f : 0.7f;
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

            // One label per SYLLABLE SEGMENT, positioned between the word's dotted lines: an
            // undivided word has exactly one (the whole word, centred, as it always was).
            private readonly Container labels;
            private readonly List<TruncatingSpriteText> segmentLabels = new List<TruncatingSpriteText>();

            [Resolved]
            private EditorBeatmap editorBeatmap { get; set; } = null!;

            [Resolved]
            private LyricEditState state { get; set; } = null!;

            [Resolved]
            private EditorClock editorClock { get; set; } = null!;

            private Grab grab;
            private double grabStart, grabEnd, grabTime;

            // Shared-boundary drag (Shift on an edge two touching words share): the LEFT unit index
            // of that boundary, or -1 for an ordinary drag. Decided once at drag start.
            private int sharedBoundaryLeftIndex = -1;

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
                    labels = new Container { RelativeSizeAxes = Axes.Both },
                };
            }

            private TimedUnit unit => hitObject.Line.Units[index];

            public void UpdateLayout(LyricTimeline parent)
            {
                if (index >= hitObject.Line.Units.Count)
                    return;

                // Out of the live pass's scope: hidden outright, including the word's label, so the
                // strip carries no lyric the mapper is not currently timing.
                if (state.HiddenByTapScope(hitObject, index))
                {
                    Alpha = 0;
                    return;
                }

                float blockX = parent.PositionOf(unit.StartTime);
                X = blockX;
                Width = Math.Max(4, parent.PositionOf(unit.EndTime) - blockX);

                // Freestyle slots shimmer in the word strip too (fixed-width label font, so the
                // substitution cannot change the word's measured width). Substituted on the WHOLE
                // word before it is cut, so the per-index glyph choice is unaffected by the cut.
                string display = FreestyleGlyphs.Substitute(unit.Text, FreestyleGlyphs.FIXED_WIDTH_POOL, FreestyleGlyphs.TickFor(Time.Current));

                // The characters are cut at the word's EFFECTIVE syllable split, authored or
                // derived, so "ap" sits left of the dotted line and "ple" right of it. Derived is
                // shown as readily as authored on purpose: it is the split gameplay's judgement
                // groups already use, so the strip shows the real grouping rather than a blank.
                //
                // An authored PAUSE cuts the runs again, and moves them: the characters before a
                // breath sit in the first half and the ones after it in the second, so the rest's
                // greyed band lands in the gap between two runs instead of over the letters. That is
                // the very cut the engine times the halves by (see PausedWord), so the way the word
                // reads here is the way it is played.
                var runs = PausedWord.DisplayRuns(display, unit, unit.StartTime, unit.EndTime);
                ensureLabels(runs.Count);

                for (int i = 0; i < runs.Count; i++)
                {
                    float loX = parent.PositionOf(runs[i].StartTime) - blockX;
                    float hiX = parent.PositionOf(runs[i].EndTime) - blockX;

                    var text = segmentLabels[i];
                    text.Text = runs[i].Text;
                    text.X = (loX + hiX) / 2;
                    text.MaxWidth = Math.Max(1, hiX - loX - 6);
                    text.Alpha = hiX - loX < 16 ? 0 : 1;
                }

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

            /// <summary>Grows/shrinks the per-segment label pool to <paramref name="count"/>.</summary>
            private void ensureLabels(int count)
            {
                while (segmentLabels.Count < count)
                {
                    var text = new TruncatingSpriteText
                    {
                        Anchor = Anchor.CentreLeft,
                        Origin = Anchor.Centre,
                        Font = TypeBeatStyle.Mono(16),
                        Colour = TypeBeatStyle.TypedChar,
                    };

                    segmentLabels.Add(text);
                    labels.Add(text);
                }

                for (int i = count; i < segmentLabels.Count; i++)
                    segmentLabels[i].Alpha = 0;
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

            /// <summary>
            /// The LEFT unit index of the boundary an edge grab sits on, when that edge is SHARED
            /// with a touching neighbour (this word's start equals the previous word's end, or its
            /// end equals the next word's start). -1 when there is nothing on the other side to
            /// move: a body grab, an outermost edge, or an edge with a real gap beside it, where a
            /// gap is legal data and only the grabbed word's own edge may move.
            /// </summary>
            private int sharedBoundaryAt(Grab g)
            {
                var units = hitObject.Line.Units;

                if (index < 0 || index >= units.Count)
                    return -1;

                switch (g)
                {
                    case Grab.ResizeStart:
                        return index > 0 && units[index - 1].EndTime == units[index].StartTime ? index - 1 : -1;

                    case Grab.ResizeEnd:
                        return index < units.Count - 1 && units[index].EndTime == units[index + 1].StartTime ? index : -1;

                    default:
                        return -1;
                }
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
                // Same jump the grey space between blocks gives: the caret goes exactly where the
                // mouse is, and nothing plays. (This used to replay the word with a pre-roll and an
                // auto-pause, which moved the caret somewhere the mapper had not pointed at; the R
                // hotkey still replays a whole line.)
                strip.SeekToScreenSpace(e.ScreenSpaceMousePosition);
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

                // Shift on a shared word edge drags the BOUNDARY: both the left word's end and the
                // right word's start follow the cursor, instead of the neighbour walling the drag
                // off. Latched here, once, so a Shift pressed or released mid-drag cannot switch
                // the gesture out from under the mapper's hand. A group drag keeps its own uniform
                // delta semantics, and Shift on the block BODY is still a plain rigid move.
                sharedBoundaryLeftIndex = !groupDrag && e.ShiftPressed ? sharedBoundaryAt(grab) : -1;

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

                // A WORD BOUNDARY is being dragged (one edge, on its own): magnet it to the caret.
                // The group drag keeps its uniform-delta semantics, and a body move drags no
                // boundary at all, so neither is magneted.
                double boundaryTime = strip.MagnetToCaret(cursorTime);

                if (sharedBoundaryLeftIndex >= 0)
                {
                    // Shift+edge: the shared boundary itself follows the cursor, retiming both words.
                    TypeBeatEditorOperations.SetSharedUnitBoundary(editorBeatmap, hitObject, sharedBoundaryLeftIndex, boundaryTime);
                    return;
                }

                switch (grab)
                {
                    case Grab.ResizeStart:
                        // The dragged edge follows the cursor directly (SetUnitTiming clamps it).
                        TypeBeatEditorOperations.SetUnitTiming(editorBeatmap, hitObject, index, boundaryTime, grabEnd);
                        break;

                    case Grab.ResizeEnd:
                        // The line's sung end (end_ms) rides on its LAST word's end, so this drag is
                        // also the sung-end lever the removed blue flag used to be: on a Line map it
                        // re-spreads the whole line, elsewhere end_ms simply follows the word.
                        TypeBeatEditorOperations.SetUnitEnd(editorBeatmap, hitObject, index, grabStart, boundaryTime);
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
                sharedBoundaryLeftIndex = -1;
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

            public void UpdateLayout(LyricTimeline parent)
            {
                Alpha = state.HiddenByTapScope(hitObject) ? 0 : 1;

                if (Alpha > 0)
                    X = parent.PositionOf(hitObject.Line.StartTime);
            }

            public override bool HandlePositionalInput => true;

            protected override bool OnHover(HoverEvent e)
            {
                line.Width = 5;
                return false;
            }

            protected override void OnHoverLost(HoverLostEvent e) => line.Width = 3;

            protected override bool OnMouseDown(MouseDownEvent e) => true;

            // The handle owns the press (OnMouseDown above), so the band underneath never sees the
            // gesture: without an OnClick of its own there is no clicked drawable for the framework
            // to route a double click to, and both clicks are simply eaten. Claiming the single
            // click leaves it inert (the handle is a drag target) but makes the double click
            // reachable, and it stops there, so the empty-space "add line" never fires from a handle.
            protected override bool OnClick(ClickEvent e) => true;

            protected override bool OnDoubleClick(DoubleClickEvent e)
            {
                strip.SeekTo(hitObject.Line.StartTime);
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
        /// re-times that boundary (clamped inside the word); double-clicking removes it; ALT/OPTION
        /// clicking splits the WORD in two at it, consuming the subdivision as the gap between the two
        /// new words (see <see cref="TypeBeatEditorOperations.SplitWord"/>). Added by the "subdivide
        /// word" action, one per boundary. Sits in the handle layer above the word blocks.
        ///
        /// <para>SHIFT+Dragging it PROMOTES it into an authored pause instead: the divider is dragged as
        /// it always is, and the span it was dragged across becomes the rest (see
        /// <see cref="TypeBeatEditorOperations.ExtendSubdivisionIntoPause"/>). The modifier is latched
        /// when the drag begins - like the word blocks' Shift-on-a-shared-edge gesture - so a Shift
        /// pressed or released mid-drag cannot switch the gesture out from under the mapper's hand. The
        /// promotion lands on RELEASE, because the span it needs is only known once the divider has come
        /// to rest, and it is a no-op (leaving the plain re-time the drag already made) wherever a rest
        /// cannot sit - which includes a word that already carries one, so a SHIFT+drag there is simply an
        /// ordinary re-time, with no preview to promise otherwise.</para>
        ///
        /// <para>While such a drag sweeps, the rest it would author is DRAWN as it grows: a greyed band
        /// (the word strip's own rest fill) reaching from where the divider stood to where the cursor
        /// holds it, fading up over <see cref="promotion_fade_ms"/>. It is a preview only - nothing is
        /// authored until the release - so the mapper can size a breath by eye before committing it, and
        /// watch it collapse again if they drag it back to nothing.</para>
        /// </summary>
        private partial class SyllableHandle : CompositeDrawable
        {
            /// <summary>How long the promotion preview takes to fade up (and back down) once a real sweep exists.</summary>
            private const double promotion_fade_ms = 120;

            private readonly LyricTimeline strip;
            private readonly TypeBeatHitObject hitObject;
            private readonly int unitIndex;
            private readonly int boundaryIndex;
            private readonly Container visual;
            private readonly Box promotionBand;

            // Shift latched at drag start, and the run of the drag the promotion needs: where the
            // divider STARTED (the rest's near edge) and where it was last dragged to (its far edge).
            private bool promote;
            private double promotedFrom;
            private double draggedTo;

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

                InternalChildren = new Drawable[]
                {
                    // The live preview of the rest a SHIFT+drag is opening (see promotionBand below): the
                    // same greyed fill the committed rest wears, so what the mapper watches grow is what
                    // they will get.
                    promotionBand = new Box
                    {
                        RelativeSizeAxes = Axes.Y,
                        Width = 0,
                        Colour = TypeBeatStyle.Background,
                        Alpha = 0,
                    },
                    visual = new Container
                    {
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        RelativeSizeAxes = Axes.Y,
                        Width = 2,
                        Masking = true,
                        Alpha = 0.85f,
                        Child = dashes,
                    },
                };
            }

            public override bool HandlePositionalInput => true;

            /// <summary>How wide, in pixels, the live rest preview is right now (0 when none is showing).</summary>
            internal float PromotionBandWidth => promotionBand.Width;

            /// <summary>How opaque the live rest preview is right now.</summary>
            internal float PromotionBandAlpha => promotionBand.Alpha;

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

                // Stale handle (an undo/edit dropped this boundary before the next rebuild), or a
                // word outside the live pass's scope; hide it either way.
                Alpha = time.HasValue && !state.HiddenByTapScope(hitObject, unitIndex) ? 1 : 0;

                if (time.HasValue)
                    X = parent.PositionOf(time.Value);

                updatePromotionBand(parent);
            }

            /// <summary>
            /// Draws the rest a SHIFT+drag would author, as the mapper sweeps it: a band reaching from
            /// where the divider stood when the drag began (<see cref="promotedFrom"/>) to where the
            /// cursor holds it now, so the breath GROWS under their hand - and shrinks back, or flips to
            /// the other side of the divider, if that is where they take it. Nothing is authored here
            /// (that is <see cref="TypeBeatEditorOperations.ExtendSubdivisionIntoPause"/>'s job on
            /// release); this is the preview that lets a breath be sized by eye first.
            ///
            /// <para>It appears only once the sweep is long enough to be one - the same
            /// <see cref="TypeBeatEditorOperations.MIN_SYLLABLE_MS"/> a promotion needs - and fades over
            /// <see cref="promotion_fade_ms"/> so it does not pop into being. Re-anchored on the divider,
            /// whose own position IS the cursor while the drag is live.</para>
            /// </summary>
            private void updatePromotionBand(LyricTimeline parent)
            {
                if (!promote || boundaryTime() is not double now)
                {
                    promotionBand.Alpha = 0;
                    return;
                }

                double from = parent.PositionOf(promotedFrom);
                double to = parent.PositionOf(now);
                float left = (float)Math.Min(from, to);
                float right = (float)Math.Max(from, to);

                // Local space: the handle is a 16 px grab zone whose CENTRE is the divider's own time, so
                // the band's left edge is measured out from that centre.
                promotionBand.X = left - X + Width / 2;
                promotionBand.Width = Math.Max(0, right - left);

                float wanted = wouldAuthor(now) ? 0.6f : 0f;

                // Only re-aimed when it is actually somewhere else, so the fade is not restarted every
                // frame (which would never let it arrive).
                if (Math.Abs(promotionBand.Alpha - wanted) > 0.01f)
                    promotionBand.FadeTo(wanted, promotion_fade_ms, wanted > 0 ? Easing.OutQuint : Easing.Out);
            }

            /// <summary>
            /// Whether releasing here would really author a rest, which is what the preview promises: the
            /// sweep must be long enough to be one AND the rest it makes must fit among the word's own
            /// breaths (see <see cref="TypeBeatEditorOperations.ExtendSubdivisionIntoPause"/>). The second
            /// half is asked through the SAME derivation the operation uses, so the preview cannot promise
            /// a rest the release would refuse.
            /// </summary>
            private bool wouldAuthor(double now)
            {
                if (Math.Abs(now - promotedFrom) < TypeBeatEditorOperations.MIN_SYLLABLE_MS)
                    return false;

                var units = hitObject.Line.Units;

                if (unitIndex < 0 || unitIndex >= units.Count)
                    return false;

                var unit = units[unitIndex];
                double min = TypeBeatEditorOperations.MIN_SYLLABLE_MS;

                if (unit.EndTime - unit.StartTime < min * 3)
                    return false;

                double start = Math.Clamp(Math.Min(promotedFrom, now), unit.StartTime + min, unit.EndTime - min * 2);
                double end = Math.Clamp(Math.Max(promotedFrom, now), start + min, unit.EndTime - min);

                if (end - start < min || boundaryTime() is not double boundary)
                    return false;

                var splits = Gameplay.SyllableSegments.SplitsFor(unit);

                if (boundaryIndex >= splits.Count)
                    return false;

                var candidate = new WordPause(start, end, splits[boundaryIndex]);

                return Gameplay.PausedWord.UsableRests(unit.Text, unit.StartTime, unit.EndTime, unit.Pauses.Append(candidate)).Count
                       == unit.Pauses.Count + 1;
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
                // ALT/OPTION+CLICK turns this subdivision into a WORD BREAK: the characters either
                // side of it become two words, and the dotted line itself becomes the space between
                // them. Offered here because the gesture belongs to the handle the mapper is pointing
                // at, and it is the natural promotion of "this syllable boundary is really a word
                // boundary". Alt is free on this surface: the compose screen uses it only to SUPPRESS
                // its typing hotkeys, and no other timeline gesture claims it.
                if (e.AltPressed && TypeBeatEditorOperations.SplitWord(editorBeatmap, hitObject, unitIndex, boundaryIndex))
                    return true;

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
                double? from = boundaryTime();

                // SHIFT+drag promotes this divider into a rest. Whether THIS sweep is one the word can take
                // (it may already carry breaths, and the new one must fit among them) is asked by
                // updatePromotionBand, which only previews a rest the release would really author.
                promote = e.ShiftPressed && from.HasValue;
                promotedFrom = from ?? 0;
                draggedTo = promotedFrom;
                state.BeginInteraction();
                editorBeatmap.BeginChange();
                return true;
            }

            protected override void OnDrag(DragEvent e)
            {
                draggedTo = strip.TimeAt(strip.ToLocalSpace(e.ScreenSpaceMousePosition).X);

                TypeBeatEditorOperations.SetSyllableBoundary(editorBeatmap, hitObject, unitIndex, boundaryIndex, draggedTo);
            }

            protected override void OnDragEnd(DragEndEvent e)
            {
                // The promotion is a modifier on the WHOLE gesture: the divider re-timed exactly as a
                // plain drag re-times it, and only on release does the span it swept become a rest.
                if (promote)
                    TypeBeatEditorOperations.ExtendSubdivisionIntoPause(editorBeatmap, hitObject, unitIndex, boundaryIndex, promotedFrom, draggedTo);

                promote = false;
                editorBeatmap.EndChange();
                state.EndInteraction();
            }
        }

        /// <summary>
        /// ONE of a word's AUTHORED PAUSES (the Map Editor's Insert Pause), drawn as a greyed-out band
        /// across the word block with a draggable handle on each of its edges. The band says at a glance
        /// that nothing is sung here; the two edges say how long the breath lasts, and the engine times
        /// the characters after the rest from its end (see <see cref="Gameplay.TypingLine"/>). A word may
        /// carry several, one per breath (see <see cref="Beatmaps.TimedUnit.Pauses"/>), each with its own
        /// region and its own pair of edges.
        ///
        /// <para>Both edges reuse the syllable-boundary handle idiom - a wide invisible grab zone
        /// around a thin visual, hover widening it, drag retiming, the strip's horizontal-resize
        /// cursor - in ONE region, because the two edges belong to one rest and their grab zones
        /// therefore stay a fixed number of pixels wide however far the strip is zoomed out. When a
        /// rest is shorter on screen than those zones are wide the two overlap; the END edge is added
        /// last and so takes the press, which is the one a mapper widening a breath reaches for, and
        /// the START edge becomes reachable as soon as the rest is widened or the strip zoomed in.</para>
        /// </summary>
        private partial class PauseRegion : CompositeDrawable
        {
            private readonly TypeBeatHitObject hitObject;
            private readonly int unitIndex;
            private readonly int pauseIndex;

            private readonly Box band;
            private readonly PauseEdgeHandle startEdge;
            private readonly PauseEdgeHandle endEdge;

            [Resolved]
            private LyricEditState state { get; set; } = null!;

            public PauseRegion(LyricTimeline strip, TypeBeatHitObject hitObject, int unitIndex, int pauseIndex)
            {
                this.hitObject = hitObject;
                this.unitIndex = unitIndex;
                this.pauseIndex = pauseIndex;

                Anchor = Anchor.CentreLeft;
                Origin = Anchor.CentreLeft;
                RelativeSizeAxes = Axes.Y;
                // The band matches the word block's own height, so it reads as "this stretch of the
                // word is not sung" rather than as an overlay floating over it.
                Height = 0.55f;

                InternalChildren = new Drawable[]
                {
                    band = new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = TypeBeatStyle.Background,
                        Alpha = 0.6f,
                    },
                    startEdge = new PauseEdgeHandle(strip, hitObject, unitIndex, pauseIndex, start: true),
                    endEdge = new PauseEdgeHandle(strip, hitObject, unitIndex, pauseIndex, start: false),
                };
            }

            public void UpdateLayout(LyricTimeline parent)
            {
                var units = hitObject.Line.Units;

                // Stale (an undo dropped the rest before the next rebuild), out of the live pass's
                // scope, or a word that no longer has this pause: hidden outright, like every other
                // handle on this strip.
                if (unitIndex < 0 || unitIndex >= units.Count || pauseIndex < 0 || pauseIndex >= units[unitIndex].Pauses.Count
                    || state.HiddenByTapScope(hitObject, unitIndex))
                {
                    Alpha = 0;
                    return;
                }

                Alpha = 1;

                var pause = units[unitIndex].Pauses[pauseIndex];
                float x = parent.PositionOf(pause.StartTime);
                X = x;
                // Never narrower than a sliver, so a very short rest still READS as a band even though
                // its edges stay grabbable regardless (their zones are fixed pixels, not proportions).
                Width = Math.Max(2, parent.PositionOf(pause.EndTime) - x);

                startEdge.X = 0;
                endEdge.X = Width;
            }
        }

        /// <summary>
        /// One draggable edge of an authored rest, and the double-click that takes the rest back out.
        /// Deliberately the same interaction as <see cref="SyllableHandle"/>'s: a wide invisible grab
        /// zone around a thin visual, hover widening it, drag retiming (clamped by
        /// <see cref="TypeBeatEditorOperations.SetWordPauseStart"/> / <c>SetWordPauseEnd</c>), and the
        /// press pulling the word's line into the detail panel so the panel's actions point at the same
        /// word. Undo/redo is one step per drag, exactly as for every other handle here.
        /// </summary>
        private partial class PauseEdgeHandle : CompositeDrawable
        {
            // Wide enough to be easy to hit, invisible, and identical to the other handles' grab width.
            private const float grab_width = 16;

            private readonly LyricTimeline strip;
            private readonly TypeBeatHitObject hitObject;
            private readonly int unitIndex;
            private readonly int pauseIndex;
            private readonly bool start;
            private readonly Box line;

            [Resolved]
            private EditorBeatmap editorBeatmap { get; set; } = null!;

            [Resolved]
            private LyricEditState state { get; set; } = null!;

            public PauseEdgeHandle(LyricTimeline strip, TypeBeatHitObject hitObject, int unitIndex, int pauseIndex, bool start)
            {
                this.strip = strip;
                this.hitObject = hitObject;
                this.unitIndex = unitIndex;
                this.pauseIndex = pauseIndex;
                this.start = start;

                Anchor = Anchor.CentreLeft;
                Origin = Anchor.Centre;
                RelativeSizeAxes = Axes.Y;
                Width = grab_width;

                InternalChild = line = new Box
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    RelativeSizeAxes = Axes.Y,
                    Width = 2,
                    Colour = TypeBeatStyle.TypedChar,
                };
            }

            public override bool HandlePositionalInput => true;

            // The edge reports its own hover through OnMouseMove rather than OnHover, exactly as
            // WordBlock's edges do: the two must behave identically for a press that arrives in the
            // same frame as the move that produced it (a scripted drag, and a fast real one), and the
            // block's idiom is the one this strip is already built around.
            protected override bool OnHover(HoverEvent e) => false;

            protected override bool OnMouseMove(MouseMoveEvent e)
            {
                line.Width = 4;
                strip.SetEdgeHovered(true);
                return false;
            }

            protected override void OnHoverLost(HoverLostEvent e)
            {
                line.Width = 2;
                strip.SetEdgeHovered(false);
            }

            // The handle owns the press (so the band, the block and the strip never see the gesture);
            // claiming the single click as inert is what makes the double click reachable, exactly as
            // BoundaryHandle does.
            protected override bool OnMouseDown(MouseDownEvent e) => true;

            protected override bool OnClick(ClickEvent e)
            {
                if (state.ActiveLine.Value != hitObject)
                    state.SelectedLine.Value = hitObject;
                else
                    state.SelectUnit(unitIndex);

                return true;
            }

            protected override bool OnDoubleClick(DoubleClickEvent e)
            {
                TypeBeatEditorOperations.RemoveWordPause(editorBeatmap, hitObject, unitIndex, pauseIndex);
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
                double time = strip.TimeAt(strip.ToLocalSpace(e.ScreenSpaceMousePosition).X);

                if (start)
                    TypeBeatEditorOperations.SetWordPauseStart(editorBeatmap, hitObject, unitIndex, pauseIndex, time);
                else
                    TypeBeatEditorOperations.SetWordPauseEnd(editorBeatmap, hitObject, unitIndex, pauseIndex, time);
            }

            protected override void OnDragEnd(DragEndEvent e)
            {
                editorBeatmap.EndChange();
                state.EndInteraction();
            }
        }

    }
}
