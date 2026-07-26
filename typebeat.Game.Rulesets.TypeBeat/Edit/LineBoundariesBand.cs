// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Input.Events;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Screens.Edit;

namespace typebeat.Game.Rulesets.TypeBeat.Edit
{
    /// <summary>
    /// Minimal boundaries view: a thin band directly beneath the waveform timeline showing lyric
    /// LINE boundaries (full-height marks + subtle alternating extent shading) and WORD-block
    /// boundaries (shorter, fainter ticks within each line). No text, no drag handles; the full
    /// interactive word strip lives in <see cref="ActiveLineDetailPanel"/>; this band only keeps
    /// the whole-map structure readable against the audio.
    ///
    /// The visible window is mirrored from the waveform timeline per-frame (one window, two
    /// viewports), so the band's marks always line up with the waveform above. Poll-synced:
    /// children are rebuilt only when the line set / unit counts change identity and are retimed
    /// in place otherwise, so undo storms (which replace every hit object instance) just work.
    ///
    /// Affordances (matching the old per-line overview bars): click a line's extent to select it
    /// and seek to its start; double-click empty space to add a line at that time.
    /// </summary>
    public partial class LineBoundariesBand : CompositeDrawable
    {
        [Resolved]
        private EditorBeatmap editorBeatmap { get; set; } = null!;

        [Resolved]
        private LyricEditState state { get; set; } = null!;

        [Resolved]
        private EditorClock editorClock { get; set; } = null!;

        [Resolved]
        private EditorScreenWithTimeline screen { get; set; } = null!;

        private readonly Container shadeLayer;
        private readonly Container markLayer;
        private readonly TapGhostLayer ghostLayer;
        private readonly Box playhead;

        private double windowStart, windowLength = 1;

        // Rebuild signature: line identities + unit counts (positions are re-polled per frame).
        private readonly List<(TypeBeatHitObject hitObject, int unitCount)> displayed = new List<(TypeBeatHitObject, int)>();

        public LineBoundariesBand()
        {
            RelativeSizeAxes = Axes.Both;
            Masking = true;

            InternalChildren = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = TypeBeatStyle.Background,
                    Alpha = 0.6f,
                },
                shadeLayer = new Container { RelativeSizeAxes = Axes.Both },
                markLayer = new Container { RelativeSizeAxes = Axes.Both },
                ghostLayer = new TapGhostLayer(),
                playhead = new Box
                {
                    RelativeSizeAxes = Axes.Y,
                    Width = 2,
                    Colour = TypeBeatStyle.TypedChar,
                    Alpha = 0,
                },
            };
        }

        protected override void Update()
        {
            base.Update();

            var timeline = screen.TimelineArea?.Timeline;

            if (timeline == null || !timeline.IsLoaded)
                return;

            // Mirror the waveform timeline's visible window every frame: scroll, zoom and seek
            // all stay in lockstep with the surface directly above.
            double windowCentre = timeline.TimeAtPosition(timeline.Current);
            double visibleRange = timeline.VisibleRange;

            // Early frames (track/content not sized yet) yield NaN/Infinity; skip until sane.
            if (!double.IsFinite(windowCentre) || !double.IsFinite(visibleRange))
                return;

            windowLength = Math.Max(1, visibleRange);
            windowStart = windowCentre - windowLength / 2;

            var ordered = TypeBeatEditorOperations.OrderedLines(editorBeatmap);

            if (signatureChanged(ordered))
                rebuild(ordered);

            foreach (var shade in shadeLayer.OfType<LineShade>())
                shade.UpdateLayout(this);

            foreach (var mark in markLayer.OfType<LineMark>())
                mark.UpdateLayout(this);

            foreach (var tick in markLayer.OfType<WordTick>())
                tick.UpdateLayout(this);

            // Ghost markers of a live tap-timing pass (nothing committed yet).
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
                if (ordered[i] != displayed[i].hitObject || ordered[i].Line.Units.Count != displayed[i].unitCount)
                    return true;
            }

            return false;
        }

        private void rebuild(IReadOnlyList<TypeBeatHitObject> ordered)
        {
            displayed.Clear();
            shadeLayer.Clear();
            markLayer.Clear();

            for (int i = 0; i < ordered.Count; i++)
            {
                var hitObject = ordered[i];
                displayed.Add((hitObject, hitObject.Line.Units.Count));

                shadeLayer.Add(new LineShade(hitObject, i));

                // Fainter/shorter word ticks first, then the line mark on top so a word start that
                // coincides with the line start simply disappears under the prominent mark.
                for (int j = 0; j < hitObject.Line.Units.Count; j++)
                    markLayer.Add(new WordTick(hitObject, j));

                markLayer.Add(new LineMark(hitObject));
            }
        }

        /// <summary>Window-relative time → local X pixels.</summary>
        public float PositionOf(double time) => (float)((time - windowStart) / windowLength * DrawWidth);

        /// <summary>Local X pixels → time.</summary>
        public double TimeAt(float x) => windowStart + x / DrawWidth * windowLength;

        protected override bool OnClick(ClickEvent e)
        {
            // Click a line's extent: select it and bring the playhead to its start (same as the
            // old per-line overview bars in the waveform). Empty space just seeks to that time,
            // consistent with the word strip, and it also keeps the band the click-owner so a
            // double-click on empty space reaches OnDoubleClick below.
            double time = TimeAt(ToLocalSpace(e.ScreenSpaceMousePosition).X);
            var hit = TypeBeatEditorOperations.OrderedLines(editorBeatmap)
                                              .FirstOrDefault(o => o.Line.StartTime <= time && time <= o.Line.EndTime);

            if (hit != null)
            {
                state.SelectedLine.Value = hit;
                editorClock.SeekSmoothlyTo(hit.Line.StartTime);
            }
            else
                editorClock.SeekSmoothlyTo(Math.Max(0, time));

            return true;
        }

        protected override bool OnDoubleClick(DoubleClickEvent e)
        {
            // Empty-gap double click = author a new line at that time (AddLine itself rejects
            // times colliding with an existing line start).
            double time = TimeAt(ToLocalSpace(e.ScreenSpaceMousePosition).X);
            var added = TypeBeatEditorOperations.AddLine(editorBeatmap, time);

            if (added != null)
            {
                state.SelectedLine.Value = added;
                editorClock.SeekSmoothlyTo(added.Line.StartTime);
            }

            return true;
        }

        /// <summary>Subtle full-height shading spanning one line's extent (alternating tint, active line lifted).</summary>
        private partial class LineShade : CompositeDrawable
        {
            private readonly TypeBeatHitObject hitObject;
            private readonly int lineIndex;
            private readonly Box body;

            [Resolved]
            private LyricEditState state { get; set; } = null!;

            public LineShade(TypeBeatHitObject hitObject, int lineIndex)
            {
                this.hitObject = hitObject;
                this.lineIndex = lineIndex;

                RelativeSizeAxes = Axes.Y;
                InternalChild = body = new Box { RelativeSizeAxes = Axes.Both };
            }

            public void UpdateLayout(LineBoundariesBand parent)
            {
                // During a tap-timing pass the band keeps its time axis (it is the mapper's place in
                // the song) but every line outside the pass is hidden outright rather than dimmed.
                if (state.HiddenByTapScope(hitObject))
                {
                    Alpha = 0;
                    return;
                }

                Alpha = 1;
                X = parent.PositionOf(hitObject.Line.StartTime);
                Width = Math.Max(0, parent.PositionOf(hitObject.Line.EndTime) - X);

                bool active = state.ActiveLine.Value == hitObject;

                // A multi-line SECTION reads on the band too, so the mapper can see the run they
                // ctrl/shift-picked in the line list against the audio before acting on it.
                bool sectioned = state.MultiSelectedLines.Contains(hitObject);

                body.Colour = TypeBeatStyle.PanelBackground.Lighten(active ? 0.6f : sectioned ? 0.35f : lineIndex % 2 == 0 ? 0.15f : 0f);
                body.Alpha = active || sectioned ? 0.9f : 0.7f;
            }
        }

        /// <summary>Prominent full-height thin mark at a line's start boundary.</summary>
        private partial class LineMark : CompositeDrawable
        {
            private readonly TypeBeatHitObject hitObject;

            [Resolved]
            private LyricEditState state { get; set; } = null!;

            public LineMark(TypeBeatHitObject hitObject)
            {
                this.hitObject = hitObject;

                Origin = Anchor.TopCentre;
                RelativeSizeAxes = Axes.Y;
                Width = 2;

                InternalChild = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = TypeBeatStyle.SungAccent,
                    Alpha = 0.9f,
                };
            }

            public void UpdateLayout(LineBoundariesBand parent)
            {
                Alpha = state.HiddenByTapScope(hitObject) ? 0 : 1;

                if (Alpha > 0)
                    X = parent.PositionOf(hitObject.Line.StartTime);
            }
        }

        /// <summary>Fainter, shorter tick at a word-unit start within a line.</summary>
        private partial class WordTick : CompositeDrawable
        {
            private readonly TypeBeatHitObject hitObject;
            private readonly int index;

            [Resolved]
            private LyricEditState state { get; set; } = null!;

            public WordTick(TypeBeatHitObject hitObject, int index)
            {
                this.hitObject = hitObject;
                this.index = index;

                Anchor = Anchor.CentreLeft;
                Origin = Anchor.Centre;
                RelativeSizeAxes = Axes.Y;
                Height = 0.5f;
                Width = 1;

                InternalChild = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = TypeBeatStyle.TypedChar,
                    Alpha = 0.45f,
                };
            }

            public void UpdateLayout(LineBoundariesBand parent)
            {
                // A retime may have dropped units since the last rebuild; hide until rebuilt. A word
                // outside a live pass's scope hides for the duration of the pass.
                if (index >= hitObject.Line.Units.Count || state.HiddenByTapScope(hitObject, index))
                {
                    Alpha = 0;
                    return;
                }

                Alpha = 1;
                X = parent.PositionOf(hitObject.Line.Units[index].StartTime);
            }
        }
    }
}
