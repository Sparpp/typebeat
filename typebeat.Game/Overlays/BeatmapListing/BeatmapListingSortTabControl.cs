// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Bindables;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Graphics;
using osuTK.Graphics;
using osuTK;
using osu.Framework.Input.Events;

namespace typebeat.Game.Overlays.BeatmapListing
{
    public partial class BeatmapListingSortTabControl : OverlaySortTabControl<SortCriteria>
    {
        public readonly Bindable<SortDirection> SortDirection = new Bindable<SortDirection>(Overlays.SortDirection.Descending);

        private (SearchCategory category, bool hasQuery)? currentParameters;

        protected override void LoadComplete()
        {
            base.LoadComplete();

            if (currentParameters == null)
                Reset(SearchCategory.Any, false);

            Current.BindValueChanged(_ => SortDirection.Value = Overlays.SortDirection.Descending);
        }

        public void Reset(SearchCategory category, bool hasQuery)
        {
            var newParameters = (category, hasQuery);

            if (currentParameters != newParameters)
            {
                TabControl.Clear();

                // Only orders over data the server stores: no ranked date (none is recorded), rating (no user
                // ratings) or nominations (no nomination process).
                TabControl.AddItem(SortCriteria.Title);
                TabControl.AddItem(SortCriteria.Artist);
                TabControl.AddItem(SortCriteria.Difficulty);
                TabControl.AddItem(SortCriteria.Updated);
                TabControl.AddItem(SortCriteria.Plays);
                TabControl.AddItem(SortCriteria.Favourites);

                if (hasQuery)
                    TabControl.AddItem(SortCriteria.Relevance);
            }

            var nonQueryCriteria = SortCriteria.Updated;

            Current.Value = hasQuery ? SortCriteria.Relevance : nonQueryCriteria;
            SortDirection.Value = Overlays.SortDirection.Descending;

            // if the new criteria isn't different from the previous one,
            // then re-adding tab items will not mark the current tab as selected.
            // see: https://github.com/ppy/osu-framework/issues/5412
            TabControl.Current.TriggerChange();

            currentParameters = newParameters;
        }

        protected override SortTabControl CreateControl() => new BeatmapSortTabControl
        {
            SortDirection = { BindTarget = SortDirection },
        };

        private partial class BeatmapSortTabControl : SortTabControl
        {
            protected override bool AddEnumEntriesAutomatically => false;

            public readonly Bindable<SortDirection> SortDirection = new Bindable<SortDirection>();

            protected override TabItem<SortCriteria> CreateTabItem(SortCriteria value) => new BeatmapSortTabItem(value)
            {
                SortDirection = { BindTarget = SortDirection }
            };
        }

        private partial class BeatmapSortTabItem : SortTabItem
        {
            public readonly Bindable<SortDirection> SortDirection = new Bindable<SortDirection>();

            public BeatmapSortTabItem(SortCriteria value)
                : base(value)
            {
            }

            protected override TabButton CreateTabButton(SortCriteria value) => new BeatmapTabButton(value)
            {
                Active = { BindTarget = Active },
                SortDirection = { BindTarget = SortDirection }
            };
        }

        public partial class BeatmapTabButton : TabButton
        {
            public readonly Bindable<SortDirection> SortDirection = new Bindable<SortDirection>();

            protected override Color4 ContentColour
            {
                set
                {
                    base.ContentColour = value;
                    icon.Colour = value;
                }
            }

            private readonly SpriteIcon icon;

            public BeatmapTabButton(SortCriteria value)
                : base(value)
            {
                Add(icon = new SpriteIcon
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    AlwaysPresent = true,
                    Alpha = 0,
                    Size = new Vector2(6),
                    Icon = FontAwesome.Solid.CaretDown,
                });
            }

            protected override void LoadComplete()
            {
                base.LoadComplete();

                SortDirection.BindValueChanged(direction =>
                {
                    icon.ScaleTo(direction.NewValue == Overlays.SortDirection.Ascending && Active.Value ? new Vector2(1f, -1f) : Vector2.One, 300, Easing.OutQuint);
                }, true);
            }

            protected override void UpdateState()
            {
                base.UpdateState();
                icon.FadeTo(Active.Value || IsHovered ? 1 : 0, 200, Easing.OutQuint);
            }

            protected override bool OnClick(ClickEvent e)
            {
                if (Active.Value)
                    SortDirection.Value = SortDirection.Value == Overlays.SortDirection.Ascending ? Overlays.SortDirection.Descending : Overlays.SortDirection.Ascending;

                return base.OnClick(e);
            }
        }
    }
}
