// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Input.Events;
using osuTK;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Screens.Edit;

namespace typebeat.Game.Rulesets.TypeBeat.Edit
{
    public partial class LyricTimeline
    {
        /// <summary>How many hold tick dots the strip is drawing. A test seam.</summary>
        internal int HoldTickCount => handleLayer.OfType<HoldRegion>().Sum(r => r.VisibleTickCount);

        /// <summary>The drawn hold tick dots, in strip order. A test seam.</summary>
        internal IReadOnlyList<Drawable> HoldTickDots => handleLayer.OfType<HoldRegion>().SelectMany(r => r.VisibleTicks).ToList();

        /// <summary>The <paramref name="index"/>-th hold band. A test seam.</summary>
        internal Drawable HoldRegionAt(int index) => handleLayer.OfType<HoldRegion>().ElementAt(index);

        /// <summary>
        /// A word's part of the rebuild signature: (character, tick count) per usable hold. Adding or removing
        /// a hold or tick changes it; dragging one does not, so the dragged band survives.
        /// </summary>
        internal static int[] HeldCharsOf(TimedUnit unit)
        {
            if (unit.Objects.Holds.Count == 0)
                return Array.Empty<int>();

            return HeldWord.UsableHolds(unit).SelectMany(h => new[] { h.Char, HoldTicks.UsableTicks(h).Length }).ToArray();
        }

        /// <summary>Whether <paramref name="unit"/>'s signature is <paramref name="chars"/>. Only called for a line that changed.</summary>
        internal static bool SameHeldChars(TimedUnit unit, int[] chars) => HeldCharsOf(unit).AsSpan().SequenceEqual(chars);

        /// <summary>The index in the word's holds of the one on <paramref name="holdChar"/>, or -1. Allocation-free.</summary>
        private static int holdIndexOf(TypeBeatHitObject hitObject, int unitIndex, int holdChar)
        {
            var units = hitObject.Line.Units;

            if (unitIndex < 0 || unitIndex >= units.Count)
                return -1;

            var holds = units[unitIndex].Objects.Holds;

            for (int i = 0; i < holds.Count; i++)
            {
                if (holds[i].Char == holdChar)
                    return i;
            }

            return -1;
        }

        /// <summary>One hold on the strip: a purple band with draggable edges and tick dots.</summary>
        private partial class HoldRegion : CompositeDrawable
        {
            private readonly TypeBeatHitObject hitObject;
            private readonly int unitIndex;
            private readonly int holdChar;

            private readonly HoldEdgeHandle startEdge;
            private readonly HoldEdgeHandle endEdge;
            private readonly HoldTickHandle[] tickHandles;

            internal int VisibleTickCount => Alpha > 0 ? tickHandles.Count(h => h.Alpha > 0) : 0;

            internal IEnumerable<Drawable> VisibleTicks => Alpha > 0 ? tickHandles.Where(h => h.Alpha > 0).Select(h => h.Dot) : Enumerable.Empty<Drawable>();

            [Resolved]
            private LyricEditState state { get; set; } = null!;

            public HoldRegion(LyricTimeline strip, TypeBeatHitObject hitObject, int unitIndex, int holdChar, int tickCount)
            {
                this.hitObject = hitObject;
                this.unitIndex = unitIndex;
                this.holdChar = holdChar;

                tickHandles = Enumerable.Range(0, tickCount).Select(k => new HoldTickHandle(strip, hitObject, unitIndex, holdChar, k)).ToArray();

                Anchor = Anchor.CentreLeft;
                Origin = Anchor.CentreLeft;
                RelativeSizeAxes = Axes.Y;
                Height = 0.55f;

                startEdge = new HoldEdgeHandle(strip, hitObject, unitIndex, holdChar, start: true);
                endEdge = new HoldEdgeHandle(strip, hitObject, unitIndex, holdChar, start: false);

                // Edges last, so they win over ticks at the ends.
                InternalChildren = new Drawable[]
                    {
                        new Box
                        {
                            RelativeSizeAxes = Axes.Both,
                            Colour = TypeBeatStyle.HoldChar,
                            Alpha = 0.55f,
                        },
                    }
                    .Concat(tickHandles)
                    .Concat(new Drawable[] { startEdge, endEdge })
                    .ToArray();
            }

            public void UpdateLayout(LyricTimeline parent)
            {
                int holdIndex = holdIndexOf(hitObject, unitIndex, holdChar);

                // Gone before the next rebuild, or out of the tap pass's scope.
                if (holdIndex < 0 || state.HiddenByTapScope(hitObject, unitIndex))
                {
                    Alpha = 0;
                    return;
                }

                Alpha = 1;

                var hold = hitObject.Line.Units[unitIndex].Objects.Holds[holdIndex];
                float x = parent.PositionOf(hold.StartTime);
                X = x;
                Width = Math.Max(2, parent.PositionOf(hold.EndTime) - x);

                startEdge.X = 0;
                endEdge.X = Width;

                double[] ticks = HoldTicks.UsableTicks(hold);

                for (int k = 0; k < tickHandles.Length; k++)
                {
                    tickHandles[k].Alpha = k < ticks.Length ? 1 : 0;

                    if (k < ticks.Length)
                        tickHandles[k].X = parent.PositionOf(ticks[k]) - x;
                }
            }
        }

        /// <summary>
        /// A draggable handle on a hold, which names its hold by character and looks it up at each use.
        /// A click selects the word; a drag is one undo step.
        /// </summary>
        private abstract partial class HoldHandle : CompositeDrawable
        {
            protected readonly LyricTimeline Strip;
            protected readonly TypeBeatHitObject HitObject;
            protected readonly int UnitIndex;
            private readonly int holdChar;

            [Resolved]
            protected EditorBeatmap EditorBeatmap { get; private set; } = null!;

            [Resolved]
            private LyricEditState state { get; set; } = null!;

            protected HoldHandle(LyricTimeline strip, TypeBeatHitObject hitObject, int unitIndex, int holdChar, float grabWidth)
            {
                Strip = strip;
                HitObject = hitObject;
                UnitIndex = unitIndex;
                this.holdChar = holdChar;

                Anchor = Anchor.CentreLeft;
                Origin = Anchor.Centre;
                RelativeSizeAxes = Axes.Y;
                Width = grabWidth;
            }

            /// <summary>The hold's index in the word's holds right now, or -1 when it is gone.</summary>
            protected int HoldIndex => holdIndexOf(HitObject, UnitIndex, holdChar);

            protected abstract void SetHovered(bool hovered);

            public override bool HandlePositionalInput => true;

            // Hover through OnMouseMove, as PauseEdgeHandle does.
            protected override bool OnHover(HoverEvent e) => false;

            protected override bool OnMouseMove(MouseMoveEvent e)
            {
                SetHovered(true);
                Strip.SetEdgeHovered(true);
                return false;
            }

            protected override void OnHoverLost(HoverLostEvent e)
            {
                SetHovered(false);
                Strip.SetEdgeHovered(false);
            }

            // Owning the press makes the double click reachable.
            protected override bool OnMouseDown(MouseDownEvent e) => true;

            protected override bool OnClick(ClickEvent e)
            {
                if (state.ActiveLine.Value != HitObject)
                    state.SelectedLine.Value = HitObject;
                else
                    state.SelectUnit(UnitIndex);

                return true;
            }

            protected override bool OnDragStart(DragStartEvent e)
            {
                state.BeginInteraction();
                EditorBeatmap.BeginChange();
                return true;
            }

            protected override void OnDragEnd(DragEndEvent e)
            {
                EditorBeatmap.EndChange();
                state.EndInteraction();
            }

            protected double CursorTime(DragEvent e) => Strip.TimeAt(Strip.ToLocalSpace(e.ScreenSpaceMousePosition).X);
        }

        /// <summary>One hold tick: a dot dragged along its band (magneted to the playhead), removed by a double-click.</summary>
        private partial class HoldTickHandle : HoldHandle
        {
            private readonly int tickIndex;

            /// <summary>The visible dot. A test seam.</summary>
            internal readonly Circle Dot;

            public HoldTickHandle(LyricTimeline strip, TypeBeatHitObject hitObject, int unitIndex, int holdChar, int tickIndex)
                : base(strip, hitObject, unitIndex, holdChar, grabWidth: 12)
            {
                this.tickIndex = tickIndex;

                // White: a purple dot would vanish on the purple band.
                InternalChild = Dot = new Circle
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Size = new Vector2(7),
                    Colour = TypeBeatStyle.TypedChar,
                };
            }

            protected override void SetHovered(bool hovered) => Dot.Scale = new Vector2(hovered ? 1.4f : 1);

            protected override bool OnDoubleClick(DoubleClickEvent e)
            {
                TypeBeatEditorOperations.RemoveHoldTick(EditorBeatmap, HitObject, UnitIndex, HoldIndex, tickIndex);
                return true;
            }

            protected override void OnDrag(DragEvent e)
            {
                double cursorTime = CursorTime(e);
                double time = Strip.timingSettings.SnapToGrid.Value ? Strip.SnapToGrid(cursorTime) : Strip.MagnetToCaret(cursorTime);

                TypeBeatEditorOperations.SetHoldTickTime(EditorBeatmap, HitObject, UnitIndex, HoldIndex, tickIndex, time);
            }
        }

        /// <summary>One draggable edge of a hold; a double-click removes the hold.</summary>
        private partial class HoldEdgeHandle : HoldHandle
        {
            private readonly bool start;
            private readonly Box line;

            public HoldEdgeHandle(LyricTimeline strip, TypeBeatHitObject hitObject, int unitIndex, int holdChar, bool start)
                : base(strip, hitObject, unitIndex, holdChar, grabWidth: 16)
            {
                this.start = start;

                InternalChild = line = new Box
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    RelativeSizeAxes = Axes.Y,
                    Width = 2,
                    Colour = TypeBeatStyle.TypedChar,
                };
            }

            protected override void SetHovered(bool hovered) => line.Width = hovered ? 4 : 2;

            protected override bool OnDoubleClick(DoubleClickEvent e)
            {
                TypeBeatEditorOperations.RemoveHold(EditorBeatmap, HitObject, UnitIndex, HoldIndex);
                return true;
            }

            protected override void OnDrag(DragEvent e)
            {
                double time = Strip.SnapToGrid(CursorTime(e));

                if (start)
                    TypeBeatEditorOperations.SetHoldStart(EditorBeatmap, HitObject, UnitIndex, HoldIndex, time);
                else
                    TypeBeatEditorOperations.SetHoldEnd(EditorBeatmap, HitObject, UnitIndex, HoldIndex, time);
            }
        }
    }
}
