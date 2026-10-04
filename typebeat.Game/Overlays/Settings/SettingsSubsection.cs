// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osuTK;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using typebeat.Game.Graphics.Sprites;
using System.Collections.Generic;
using osu.Framework.Allocation;
using osu.Framework.Localisation;
using typebeat.Game.Graphics;

namespace typebeat.Game.Overlays.Settings
{
    public abstract partial class SettingsSubsection : FillFlowContainer, IFilterable
    {
        public const float VERTICAL_PADDING = (header_height - header_font_size) * 0.5f;

        protected override Container<Drawable> Content => FlowContent;

        protected readonly FillFlowContainer FlowContent;

        protected abstract LocalisableString Header { get; }

        public virtual IEnumerable<LocalisableString> FilterTerms => new[] { Header };

        public bool MatchingFilter
        {
            set => this.FadeTo(value ? 1 : 0);
        }

        public bool FilteringActive { get; set; }

        protected SettingsSubsection()
        {
            RelativeSizeAxes = Axes.X;
            AutoSizeAxes = Axes.Y;
            Direction = FillDirection.Vertical;

            FlowContent = new GroupingFlow
            {
                Margin = new MarginPadding { Top = SettingsSection.ITEM_SPACING_V2 },
                Direction = FillDirection.Vertical,
                Spacing = new Vector2(0, SettingsSection.ITEM_SPACING_V2),
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
            };
        }

        private const int header_height = 43;
        private const int header_font_size = 20;

        [BackgroundDependencyLoader]
        private void load()
        {
            AddRangeInternal(new[]
            {
                CreateHeader(),
                FlowContent
            });
        }

        protected virtual Drawable CreateHeader()
            => CreateSubsectionHeader(Header);

        /// <summary>
        /// A heading. As the subsection's own header (see <see cref="CreateHeader"/>) it is plain text;
        /// placed among a subsection's children it starts a group: every child added after it, up to
        /// the next heading, is gathered under it (see <see cref="GroupingFlow"/>), so the settings
        /// search shows the heading only while something under it matches, or when the heading
        /// itself does, in which case the whole group shows.
        /// </summary>
        protected static Drawable CreateSubsectionHeader(LocalisableString header)
        {
            return new SubsectionHeading
            {
                Text = header,
                Font = OsuFont.GetFont(size: header_font_size),
                Margin = new MarginPadding { Vertical = VERTICAL_PADDING },
                Padding = SettingsPanel.CONTENT_PADDING,
            };
        }

        private partial class SubsectionHeading : OsuSpriteText
        {
        }

        /// <summary>
        /// The subsection's content flow. Children arrive flat, headings among them; each heading opens
        /// a <see cref="HeadingGroup"/> that the children after it are routed into. Without the groups
        /// the search left every heading on screen, since a bare heading is not filterable: searching
        /// for one setting showed the headings of every group in the subsection around it.
        /// </summary>
        private partial class GroupingFlow : FillFlowContainer
        {
            private HeadingGroup? currentGroup;

            public override void Add(Drawable drawable)
            {
                if (drawable is SubsectionHeading heading)
                {
                    base.Add(currentGroup = new HeadingGroup(heading, Spacing));
                    return;
                }

                if (currentGroup != null)
                    currentGroup.Add(drawable);
                else
                    base.Add(drawable);
            }

            public override void Clear(bool disposeChildren)
            {
                currentGroup = null;
                base.Clear(disposeChildren);
            }
        }

        /// <summary>
        /// A heading and the settings under it, laid out exactly as they were in the flat flow (same
        /// spacing), and filterable as one: shown while the heading or anything under it matches.
        /// </summary>
        private partial class HeadingGroup : FillFlowContainer, IFilterable
        {
            private readonly SubsectionHeading heading;

            public HeadingGroup(SubsectionHeading heading, Vector2 spacing)
            {
                this.heading = heading;

                RelativeSizeAxes = Axes.X;
                AutoSizeAxes = Axes.Y;
                Direction = FillDirection.Vertical;
                Spacing = spacing;

                Add(heading);
            }

            public IEnumerable<LocalisableString> FilterTerms => new[] { heading.Text };

            public bool MatchingFilter
            {
                set => Alpha = value ? 1 : 0;
            }

            public bool FilteringActive { get; set; }
        }
    }
}
