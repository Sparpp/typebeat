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
using osu.Framework.Input.Events;
using osu.Framework.Localisation;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Screens.Edit;
using osuTK;

namespace typebeat.Game.Rulesets.TypeBeat.Edit
{
    /// <summary>
    /// The BACKING VOCAL lane (backlog 445): a second track across the top of the strip, over the
    /// main word blocks, which are squeezed to four fifths of their height to make room for it and
    /// stay centred on the strip (<see cref="MAIN_TRACK_HEIGHT"/>). It shares the lines' bands and boundaries, so both tracks
    /// always cut the song at the same line divisions.
    ///
    /// <para>A backing word is a block placed by WHEN it is sung: its body moves it and its edges
    /// resize it, like a word block, with the backing words either side of it as walls on its start
    /// (see <see cref="TypeBeatEditorOperations.MoveBackingVocal"/>). Below each block sits its
    /// ANCHOR, joined to the centre of the block's bottom edge by a dotted line: the anchor says
    /// WHERE across the line the word pops up in gameplay, as a fraction of the line's width, drawn
    /// here against the line's band (the band's left edge is the lyric's left end). Anchors spread
    /// evenly across the line until one is dragged (<see cref="BackingVocals.DefaultAnchor"/>), and
    /// double-clicking one puts it back. Display data only: nothing here changes what is typed.</para>
    ///
    /// <para>The two tracks are SELECTED like a word is (<see cref="LyricEditState.SelectedTrack"/>):
    /// a click in a track, on a block, an anchor or its empty space, picks it for the word actions
    /// ("add word" adds a backing word on the backing track), and the other track is drawn dimmed.
    /// A backing word's text is edited in the line list, in brackets after the line's own.</para>
    /// </summary>
    public partial class LyricTimeline
    {
        /// <summary>The share of the strip's height the MAIN track keeps, centred on the strip. Its word blocks and their handles shrink with it.</summary>
        public const float MAIN_TRACK_HEIGHT = 0.8f;

        /// <summary>
        /// The backing lane's height as a share of the strip: the room above the main track's word
        /// blocks, which sit at 55% of the main track, centred on the strip, so they start at 28% of it.
        /// </summary>
        public const float BACKING_LANE_HEIGHT = 0.26f;

        // Inside the lane (its own 0..1): the blocks across the upper half, the anchors' centres low
        // down, and the dotted lines between them.
        private const float backing_block_top = 0.08f;
        private const float backing_block_height = 0.46f;
        private const float backing_anchor_y = 0.82f;

        // The anchor dot's radius: the dotted line stops at the dot rather than running into it.
        private const float backing_anchor_radius = 5;

        /// <summary>How opaque the track the word actions are NOT addressing is drawn (see <see cref="LyricEditState.SelectedTrack"/>).</summary>
        public const float UNSELECTED_TRACK_ALPHA = 0.35f;

        // The selected track's light and the other one's shade: a wash of the lyric colour over the
        // first, of the background over the second.
        private const float selected_track_light = 0.07f;
        private const float unselected_track_shade = 0.4f;

        private readonly Box backingTrackShade;
        private readonly Box mainTrackShade;

        private readonly Container mainBlockTrack;
        private readonly Container mainHandleTrack;
        private readonly Container backingBlockLane;
        private readonly Container backingAnchorLane;

        private readonly Container backingConnectorLayer;
        private readonly Container backingBlockLayer;
        private readonly Container backingAnchorLayer;

        private readonly List<BackingBlock> backingBlocks = new List<BackingBlock>();
        private readonly List<BackingAnchor> backingAnchors = new List<BackingAnchor>();
        private readonly List<BackingConnector> backingConnectors = new List<BackingConnector>();

        /// <summary>A container over the main track: <see cref="MAIN_TRACK_HEIGHT"/> of the strip, centred on it, so the word blocks sit in the middle of the view.</summary>
        private static Container mainTrack(Drawable content) => new Container
        {
            Anchor = Anchor.CentreLeft,
            Origin = Anchor.CentreLeft,
            RelativeSizeAxes = Axes.Both,
            Height = MAIN_TRACK_HEIGHT,
            Child = content,
        };

        /// <summary>A container over the backing lane: the top <see cref="BACKING_LANE_HEIGHT"/> of the strip.</summary>
        private static Container backingLane(params Drawable[] content) => new Container
        {
            RelativeSizeAxes = Axes.Both,
            Height = BACKING_LANE_HEIGHT,
            Children = content,
        };

        /// <summary>A wash over one track's area (the lane above its bottom edge, or the main track below it), coloured by <see cref="updateTrackDim"/>.</summary>
        private static Box trackShade(bool backing) => new Box
        {
            Anchor = backing ? Anchor.TopLeft : Anchor.BottomLeft,
            Origin = backing ? Anchor.TopLeft : Anchor.BottomLeft,
            RelativeSizeAxes = Axes.Both,
            Height = backing ? BACKING_LANE_HEIGHT : 1 - BACKING_LANE_HEIGHT,
        };

        /// <summary>The lane's quiet bottom edge, so the two tracks read as two.</summary>
        private static Drawable backingLaneSeparator() => new Box
        {
            Anchor = Anchor.BottomLeft,
            Origin = Anchor.BottomLeft,
            RelativeSizeAxes = Axes.X,
            Height = 1,
            Colour = TypeBeatStyle.TypedChar,
            Alpha = 0.08f,
        };

        private void addBackingVocals(TypeBeatHitObject hitObject)
        {
            for (int k = 0; k < hitObject.Line.BackingVocals.Count; k++)
            {
                backingConnectorLayer.Add(addTo(backingConnectors, new BackingConnector(hitObject, k)));
                backingBlockLayer.Add(addTo(backingBlocks, new BackingBlock(this, hitObject, k)));
                backingAnchorLayer.Add(addTo(backingAnchors, new BackingAnchor(this, hitObject, k)));
            }
        }

        private void clearBackingVocals()
        {
            backingConnectorLayer.Clear();
            backingBlockLayer.Clear();
            backingAnchorLayer.Clear();
            backingBlocks.Clear();
            backingAnchors.Clear();
            backingConnectors.Clear();
        }

        private void updateBackingVocalLayout()
        {
            for (int i = 0; i < backingBlocks.Count; i++)
                backingBlocks[i].UpdateLayout(this);

            for (int i = 0; i < backingAnchors.Count; i++)
                backingAnchors[i].UpdateLayout(this);

            float laneHeight = backingConnectorLayer.DrawHeight;

            for (int i = 0; i < backingConnectors.Count; i++)
                backingConnectors[i].UpdateLayout(this, laneHeight);
        }

        /// <summary>
        /// Dims whichever track the word actions are not addressing (backlog 445). Still clickable:
        /// a press in the dimmed track is what picks it.
        /// </summary>
        private void updateTrackDim()
        {
            bool backing = state.BackingTrackSelected;
            float main = backing ? UNSELECTED_TRACK_ALPHA : 1;
            float lane = backing ? 1 : UNSELECTED_TRACK_ALPHA;

            mainBlockTrack.Alpha = mainHandleTrack.Alpha = main;
            backingBlockLane.Alpha = backingAnchorLane.Alpha = lane;

            shade(backingTrackShade, selected: backing);
            shade(mainTrackShade, selected: !backing);

            static void shade(Box box, bool selected)
            {
                box.Colour = selected ? TypeBeatStyle.TypedChar : TypeBeatStyle.Background;
                box.Alpha = selected ? selected_track_light : unselected_track_shade;
            }
        }

        /// <summary>Picks the track a click on empty strip space landed in: the backing lane at the top, the main track below it.</summary>
        private void selectTrackAt(Vector2 screenSpacePosition)
        {
            if (ToLocalSpace(screenSpacePosition).Y < DrawHeight * BACKING_LANE_HEIGHT)
                state.SelectBackingTrack();
            else
                state.SelectMainTrack();
        }

        /// <summary>Selects backing word <paramref name="index"/> of <paramref name="hitObject"/>'s line, making that line active first when it is not.</summary>
        private void selectBackingWord(TypeBeatHitObject hitObject, int index)
        {
            if (state.ActiveLine.Value != hitObject)
                state.SelectLineAtBackingWord(hitObject, index);
            else
                state.SelectBackingWord(index);
        }

        /// <summary>How opaque the strip draws the main track's word blocks right now (test support).</summary>
        internal float MainTrackAlpha => mainBlockTrack.Alpha;

        /// <summary>Whether the strip lights the backing lane (rather than shading it) right now (test support).</summary>
        internal bool BackingLaneLit => backingTrackShade.Alpha == selected_track_light && mainTrackShade.Alpha == unselected_track_shade;

        /// <summary>How opaque the strip draws the backing lane right now (test support).</summary>
        internal float BackingLaneAlpha => backingBlockLane.Alpha;

        private static T addTo<T>(List<T> list, T drawable)
        {
            list.Add(drawable);
            return drawable;
        }

        /// <summary>A backing word's block on screen: its left edge and width, never narrower than a sliver.</summary>
        private (float X, float Width) backingBlockSpan(BackingVocal word)
        {
            float x = PositionOf(word.StartTime);
            return (x, Math.Max(4, PositionOf(word.EndTime) - x));
        }

        /// <summary>Where backing word <paramref name="index"/>'s anchor sits on screen: its fraction of the way across the line's band.</summary>
        private float backingAnchorX(LyricLine line, int index)
        {
            float from = PositionOf(line.StartTime);
            return from + (float)BackingVocals.AnchorOf(line.BackingVocals, index) * (PositionOf(line.EndTime) - from);
        }

        /// <summary>
        /// The fraction of the way across <paramref name="line"/>'s band that a local X sits at, clamped
        /// to the band, or null when the band has no width to measure against.
        /// </summary>
        private double? backingAnchorAt(LyricLine line, float x)
        {
            float from = PositionOf(line.StartTime);
            float width = PositionOf(line.EndTime) - from;

            return width > 0 ? Math.Clamp((x - from) / width, 0, 1) : null;
        }

        /// <summary>Word <paramref name="index"/>'s block on <paramref name="hitObject"/>'s line, in the main track (test support).</summary>
        internal Drawable WordBlockOf(TypeBeatHitObject hitObject, int index) => blocks.Single(b => b.HitObject == hitObject && b.Index == index);

        /// <summary>The line boundaries the strip is drawing, one per line (test support).</summary>
        internal IReadOnlyList<Drawable> BoundaryHandles => boundaryHandles;

        /// <summary>How many backing word blocks the strip is drawing for <paramref name="hitObject"/> (test support).</summary>
        internal int BackingBlockCount(TypeBeatHitObject hitObject) => backingBlocks.Count(b => b.HitObject == hitObject && b.Alpha > 0);

        /// <summary>Backing word <paramref name="index"/>'s block on <paramref name="hitObject"/>'s line (test support).</summary>
        internal Drawable BackingBlockOf(TypeBeatHitObject hitObject, int index) => backingBlocks.Single(b => b.HitObject == hitObject && b.Index == index);

        /// <summary>Backing word <paramref name="index"/>'s anchor on <paramref name="hitObject"/>'s line (test support).</summary>
        internal Drawable BackingAnchorOf(TypeBeatHitObject hitObject, int index) => backingAnchors.Single(a => a.HitObject == hitObject && a.Index == index);

        /// <summary>
        /// The two ends of backing word <paramref name="index"/>'s dotted line in screen space, the
        /// block end first (test support): where the line really is drawn, read off its own transform.
        /// </summary>
        internal (Vector2 Block, Vector2 Anchor) BackingConnectorEnds(TypeBeatHitObject hitObject, int index)
            => backingConnectors.Single(c => c.HitObject == hitObject && c.Index == index).ScreenSpaceEnds;

        /// <summary>
        /// A backing word: a block placed by when it is sung. The body moves it (keeping its length) and
        /// the edges resize it, both clamped by <see cref="TypeBeatEditorOperations.SetBackingVocalTiming"/>;
        /// a click makes its line the active one, and a double click seeks there, as on a word block.
        /// </summary>
        private partial class BackingBlock : CompositeDrawable
        {
            // The word block's edge zone: fixed pixels, never more than 40% of a thin block.
            private const float edge_px = 7;

            private enum Grab { Move, ResizeStart, ResizeEnd }

            private readonly LyricTimeline strip;
            private readonly Box body;
            private readonly TruncatingSpriteText label;

            [Resolved]
            private EditorBeatmap editorBeatmap { get; set; } = null!;

            [Resolved]
            private LyricEditState state { get; set; } = null!;

            private Grab grab;
            private double grabStart, grabEnd, grabTime;
            private bool dragging;

            // A group drag (the body of a block in a multi-selection): every selected word moves by the
            // same distance, captured at drag start as the word blocks' own group drag is.
            private int[] groupIndices = Array.Empty<int>();
            private double[] groupStarts = Array.Empty<double>();
            private double[] groupEnds = Array.Empty<double>();
            private string? lastFontFamily;

            public TypeBeatHitObject HitObject { get; }
            public int Index { get; }

            public BackingBlock(LyricTimeline strip, TypeBeatHitObject hitObject, int index)
            {
                this.strip = strip;
                HitObject = hitObject;
                Index = index;

                RelativePositionAxes = Axes.Y;
                RelativeSizeAxes = Axes.Y;
                Y = backing_block_top;
                Height = backing_block_height;
                Masking = true;
                CornerRadius = 4;
                BorderThickness = 1.5f;

                InternalChildren = new Drawable[]
                {
                    // A lighter, hollower block than a sung word's: a voice behind the lyric.
                    body = new Box { RelativeSizeAxes = Axes.Both, Colour = TypeBeatStyle.TypedChar, Alpha = 0.16f },
                    label = new TruncatingSpriteText
                    {
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        Font = TypeBeatStyle.Lyric(13),
                        Colour = TypeBeatStyle.TypedChar,
                    },
                };
            }

            /// <summary>The word this block draws, or null once an edit has dropped it (until the next rebuild).</summary>
            private BackingVocal? word => Index < HitObject.Line.BackingVocals.Count ? HitObject.Line.BackingVocals[Index] : null;

            public void UpdateLayout(LyricTimeline parent)
            {
                if (word is not BackingVocal current || state.HiddenByTapScope(HitObject))
                {
                    Alpha = 0;
                    return;
                }

                bool active = state.ActiveLine.Value == HitObject;
                Alpha = active ? 1 : 0.55f;

                var (x, width) = parent.backingBlockSpan(current);
                X = x;
                Width = width;

                if (!string.Equals(lastFontFamily, parent.lyricFontFamily, StringComparison.Ordinal))
                {
                    label.Font = TypeBeatStyle.Lyric(13, parent.lyricFontFamily);
                    lastFontFamily = parent.lyricFontFamily;
                }

                label.Text = current.Text;
                label.MaxWidth = Math.Max(1, width - 6);
                label.Alpha = width < 14 ? 0 : 1;

                bool selected = active && state.BackingTrackSelected && state.SelectedBackingIndices.Contains(Index);

                BorderColour = selected || dragging || IsHovered ? TypeBeatStyle.Caret : TypeBeatStyle.TypedChar.Opacity(active ? 0.7f : 0.45f);
                body.Colour = selected ? TypeBeatStyle.Caret : TypeBeatStyle.TypedChar;
                body.Alpha = selected ? 0.4f : dragging ? 0.3f : 0.16f;
            }

            private Grab grabAt(float localX)
            {
                float zone = Math.Min(edge_px, DrawWidth * 0.4f);

                if (localX <= zone)
                    return Grab.ResizeStart;
                if (localX >= DrawWidth - zone)
                    return Grab.ResizeEnd;

                return Grab.Move;
            }

            protected override bool OnMouseMove(MouseMoveEvent e)
            {
                strip.SetEdgeHovered(grabAt(ToLocalSpace(e.ScreenSpaceMousePosition).X) != Grab.Move);
                return false;
            }

            protected override void OnHoverLost(HoverLostEvent e) => strip.SetEdgeHovered(false);

            protected override bool OnMouseDown(MouseDownEvent e)
            {
                grab = grabAt(ToLocalSpace(e.ScreenSpaceMousePosition).X);
                return true;
            }

            protected override bool OnClick(ClickEvent e)
            {
                // The word blocks' own selection gestures: Ctrl toggles a block in or out, Shift
                // selects the run from the primary one, a plain click selects just this one. A block
                // on another line selects that line first, and this word with it.
                if (state.ActiveLine.Value == HitObject && state.BackingTrackSelected && (e.ControlPressed || e.SuperPressed))
                    state.ToggleBackingWord(Index);
                else if (state.ActiveLine.Value == HitObject && state.BackingTrackSelected && e.ShiftPressed && state.SelectedBackingIndex.Value >= 0)
                    state.SelectBackingRange(state.SelectedBackingIndex.Value, Index);
                else
                    strip.selectBackingWord(HitObject, Index);

                return true;
            }

            protected override bool OnDoubleClick(DoubleClickEvent e)
            {
                strip.SeekToScreenSpace(e.ScreenSpaceMousePosition);
                return true;
            }

            protected override bool OnDragStart(DragStartEvent e)
            {
                if (word is not BackingVocal current)
                    return false;

                grabStart = current.StartTime;
                grabEnd = current.EndTime;
                grabTime = strip.TimeAt(strip.ToLocalSpace(e.ScreenSpaceMouseDownPosition).X);

                // The body of a block in a multi-selection drags the whole selection; anything else
                // collapses the selection to this block, as a word block does.
                var words = HitObject.Line.BackingVocals;
                var selected = state.SelectedBackingInOrder(words.Count);
                bool group = grab == Grab.Move && state.ActiveLine.Value == HitObject && state.BackingTrackSelected
                             && selected.Length > 1 && selected.Contains(Index);

                if (group)
                {
                    groupIndices = selected;
                    groupStarts = selected.Select(i => words[i].StartTime).ToArray();
                    groupEnds = selected.Select(i => words[i].EndTime).ToArray();
                }
                else
                {
                    groupIndices = Array.Empty<int>();
                    strip.selectBackingWord(HitObject, Index);
                }

                dragging = true;
                state.BeginInteraction();
                editorBeatmap.BeginChange();
                return true;
            }

            protected override void OnDrag(DragEvent e)
            {
                double cursorTime = strip.TimeAt(strip.ToLocalSpace(e.ScreenSpaceMousePosition).X);

                // An edge is a boundary, snapped or magneted like a word edge; a move snaps its
                // start by the dragged distance, keeping the length (as a word block's body does).
                double edgeTime = strip.timingSettings.SnapToGrid.Value ? strip.SnapToGrid(cursorTime) : strip.MagnetToCaret(cursorTime);

                switch (grab)
                {
                    case Grab.ResizeStart:
                        TypeBeatEditorOperations.SetBackingVocalTiming(editorBeatmap, HitObject, Index, edgeTime, grabEnd);
                        break;

                    case Grab.ResizeEnd:
                        TypeBeatEditorOperations.SetBackingVocalTiming(editorBeatmap, HitObject, Index, grabStart, edgeTime);
                        break;

                    default:
                        if (groupIndices.Length > 0)
                        {
                            // The group's first word snaps, and the rest follow it by the same distance.
                            double delta = strip.SnapToGrid(groupStarts[0] + cursorTime - grabTime) - groupStarts[0];
                            TypeBeatEditorOperations.MoveBackingVocals(editorBeatmap, HitObject, groupIndices, groupStarts, groupEnds, delta);
                        }
                        else
                            TypeBeatEditorOperations.MoveBackingVocal(editorBeatmap, HitObject, Index, strip.SnapToGrid(grabStart + cursorTime - grabTime));

                        break;
                }
            }

            protected override void OnDragEnd(DragEndEvent e)
            {
                dragging = false;
                editorBeatmap.EndChange();
                state.EndInteraction();
            }
        }

        /// <summary>
        /// Where a backing word pops up: a dot below its block, dragged sideways across its line's band.
        /// Hollow while it sits on its default place, filled once the mapper has put it somewhere, and
        /// double-clicking it puts it back.
        /// </summary>
        private partial class BackingAnchor : CompositeDrawable, IHasTooltip
        {
            private const float dot_size = backing_anchor_radius * 2;

            // The grab zone around the dot, as wide as the other handles' on this strip.
            private const float grab_size = 16;

            private readonly LyricTimeline strip;
            private readonly Circle dot;
            private readonly Circle ring;

            [Resolved]
            private EditorBeatmap editorBeatmap { get; set; } = null!;

            [Resolved]
            private LyricEditState state { get; set; } = null!;

            private bool dragging;
            private float grabOffset;

            public TypeBeatHitObject HitObject { get; }
            public int Index { get; }

            public LocalisableString TooltipText => "Drag to choose where this backing vocal pops up across the line, double-click to put it back";

            public BackingAnchor(LyricTimeline strip, TypeBeatHitObject hitObject, int index)
            {
                this.strip = strip;
                HitObject = hitObject;
                Index = index;

                RelativePositionAxes = Axes.Y;
                Y = backing_anchor_y;
                Origin = Anchor.Centre;
                Size = new Vector2(grab_size);

                InternalChildren = new Drawable[]
                {
                    ring = new Circle
                    {
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        Size = new Vector2(dot_size),
                        Colour = TypeBeatStyle.TypedChar,
                    },
                    // The fill: the background colour while the anchor is on its default place (so the
                    // dot reads as a ring), gone once it has been placed.
                    dot = new Circle
                    {
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        Size = new Vector2(dot_size - 4),
                        Colour = TypeBeatStyle.Background,
                    },
                };
            }

            public override bool HandlePositionalInput => true;

            public void UpdateLayout(LyricTimeline parent)
            {
                var words = HitObject.Line.BackingVocals;

                if (Index >= words.Count || state.HiddenByTapScope(HitObject))
                {
                    Alpha = 0;
                    return;
                }

                Alpha = state.ActiveLine.Value == HitObject ? 1 : 0.55f;
                X = parent.backingAnchorX(HitObject.Line, Index);

                bool hot = dragging || IsHovered;
                ring.Colour = hot ? TypeBeatStyle.Caret : words[Index].Anchor != null ? TypeBeatStyle.SungAccent : TypeBeatStyle.TypedChar;
                ring.Size = new Vector2(hot ? dot_size + 2 : dot_size);
                dot.Alpha = words[Index].Anchor != null ? 0 : 1;
            }

            protected override bool OnMouseMove(MouseMoveEvent e)
            {
                strip.SetEdgeHovered(true);
                return false;
            }

            protected override void OnHoverLost(HoverLostEvent e) => strip.SetEdgeHovered(false);

            // The anchor owns the press, so neither the band nor the strip sees the gesture; claiming
            // the click is what makes the double click reachable (see BoundaryHandle).
            protected override bool OnMouseDown(MouseDownEvent e) => true;

            protected override bool OnClick(ClickEvent e)
            {
                strip.selectBackingWord(HitObject, Index);
                return true;
            }

            protected override bool OnDoubleClick(DoubleClickEvent e)
            {
                TypeBeatEditorOperations.SetBackingVocalAnchor(editorBeatmap, HitObject, Index, null);
                return true;
            }

            protected override bool OnDragStart(DragStartEvent e)
            {
                if (Index >= HitObject.Line.BackingVocals.Count)
                    return false;

                strip.selectBackingWord(HitObject, Index);

                // Grabbed off-centre, the dot keeps that offset rather than jumping to the cursor.
                grabOffset = strip.ToLocalSpace(e.ScreenSpaceMouseDownPosition).X - strip.backingAnchorX(HitObject.Line, Index);

                dragging = true;
                state.BeginInteraction();
                editorBeatmap.BeginChange();
                return true;
            }

            protected override void OnDrag(DragEvent e)
            {
                float x = strip.ToLocalSpace(e.ScreenSpaceMousePosition).X - grabOffset;

                if (strip.backingAnchorAt(HitObject.Line, x) is double anchor)
                    TypeBeatEditorOperations.SetBackingVocalAnchor(editorBeatmap, HitObject, Index, anchor);
            }

            protected override void OnDragEnd(DragEndEvent e)
            {
                dragging = false;
                editorBeatmap.EndChange();
                state.EndInteraction();
            }
        }

        /// <summary>
        /// The dotted line from a backing word's anchor up to the centre of its block's bottom edge: one
        /// rotated, masked row of dashes, so moving either end costs a transform, and dashes are only
        /// ever added (when the line grows past the ones it has), never per frame.
        /// </summary>
        private partial class BackingConnector : CompositeDrawable
        {
            private const float dash = 3;
            private const float gap = 3;

            private int dashCount;

            [Resolved]
            private LyricEditState state { get; set; } = null!;

            public TypeBeatHitObject HitObject { get; }
            public int Index { get; }

            public BackingConnector(TypeBeatHitObject hitObject, int index)
            {
                HitObject = hitObject;
                Index = index;

                Origin = Anchor.CentreLeft;
                Height = 1.5f;
                Masking = true;
                Colour = TypeBeatStyle.TypedChar;
            }

            /// <summary>Where the line starts (the block's bottom-centre) and ends (the anchor's top), in screen space.</summary>
            public (Vector2 Block, Vector2 Anchor) ScreenSpaceEnds
                => (ToScreenSpace(new Vector2(0, DrawHeight / 2)), ToScreenSpace(new Vector2(DrawWidth, DrawHeight / 2)));

            public void UpdateLayout(LyricTimeline parent, float laneHeight)
            {
                var words = HitObject.Line.BackingVocals;

                if (Index >= words.Count || state.HiddenByTapScope(HitObject))
                {
                    Alpha = 0;
                    return;
                }

                Alpha = state.ActiveLine.Value == HitObject ? 0.6f : 0.3f;

                var (x, width) = parent.backingBlockSpan(words[Index]);
                var from = new Vector2(x + width / 2, (backing_block_top + backing_block_height) * laneHeight);
                var to = new Vector2(parent.backingAnchorX(HitObject.Line, Index), backing_anchor_y * laneHeight - backing_anchor_radius);
                var delta = to - from;
                float length = delta.Length;

                Position = from;
                Width = length;
                Rotation = MathF.Atan2(delta.Y, delta.X) * 180 / MathF.PI;

                while (dashCount * (dash + gap) < length)
                {
                    AddInternal(new Box
                    {
                        RelativeSizeAxes = Axes.Y,
                        Width = dash,
                        X = dashCount * (dash + gap),
                    });
                    dashCount++;
                }
            }
        }
    }
}
