// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Input.Events;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Screens.Edit;
using typebeat.Game.Screens.Edit.Components.Timelines.Summary.Parts;

namespace typebeat.Game.Rulesets.TypeBeat.Edit
{
    /// <summary>
    /// Per-line bars rendered inside the top waveform timeline: the whole-map picture. Click a
    /// bar to select + seek to its line; double-click an empty gap to add a line there.
    /// Poll-synced: children are rebuilt only when the line set changes identity, repositioned
    /// in place otherwise — undo/redo (which replaces every hit object instance) just works.
    /// </summary>
    public partial class LineOverviewPart : TimelinePart
    {
        [Resolved]
        private LyricEditState state { get; set; } = null!;

        [Resolved]
        private EditorClock editorClock { get; set; } = null!;

        private readonly List<TypeBeatHitObject> displayed = new List<TypeBeatHitObject>();

        public LineOverviewPart()
        {
            RelativeSizeAxes = Axes.Both;
        }

        protected override void Update()
        {
            base.Update();

            var current = TypeBeatEditorOperations.OrderedLines(EditorBeatmap);

            if (!current.SequenceEqual(displayed))
            {
                displayed.Clear();
                displayed.AddRange(current);

                Clear();

                foreach (var hitObject in current)
                    Add(new LineBar(hitObject));
            }

            foreach (var child in Children.OfType<LineBar>())
                child.Active = child.HitObject == state.ActiveLine.Value;
        }

        protected override bool OnDoubleClick(DoubleClickEvent e)
        {
            // Empty-gap double click = author a new line at that time (bars consume their own clicks).
            double time = Content.ToLocalSpace(e.ScreenSpaceMousePosition).X / Content.DrawWidth * Content.RelativeChildSize.X;
            var added = TypeBeatEditorOperations.AddLine(EditorBeatmap, time);

            if (added != null)
            {
                state.SelectedLine.Value = added;
                editorClock.SeekSmoothlyTo(added.Line.StartTime);
            }

            return true;
        }

        private partial class LineBar : CompositeDrawable
        {
            public readonly TypeBeatHitObject HitObject;

            [Resolved]
            private LyricEditState state { get; set; } = null!;

            [Resolved]
            private EditorClock editorClock { get; set; } = null!;

            private readonly Box body;

            public bool Active
            {
                set => body.Colour = value ? TypeBeatStyle.Caret : TypeBeatStyle.SungAccent;
            }

            public LineBar(TypeBeatHitObject hitObject)
            {
                HitObject = hitObject;

                RelativePositionAxes = Axes.X;
                RelativeSizeAxes = Axes.X;
                Anchor = Anchor.CentreLeft;
                Origin = Anchor.CentreLeft;
                Height = 14;
                Masking = true;
                CornerRadius = 3;

                InternalChild = body = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = TypeBeatStyle.SungAccent,
                    Alpha = 0.8f,
                };
            }

            protected override void Update()
            {
                base.Update();

                // Positions are milliseconds; TimelinePart's RelativeChildSize maps them to track fraction.
                X = (float)HitObject.Line.StartTime;
                Width = (float)(HitObject.Line.EndTime - HitObject.Line.StartTime);
            }

            protected override bool OnClick(ClickEvent e)
            {
                state.SelectedLine.Value = HitObject;
                editorClock.SeekSmoothlyTo(HitObject.Line.StartTime);
                return true;
            }

            protected override bool OnHover(HoverEvent e)
            {
                body.Alpha = 1;
                return false;
            }

            protected override void OnHoverLost(HoverLostEvent e) => body.Alpha = 0.8f;
        }
    }
}
