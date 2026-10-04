// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Audio.Sample;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics;
using osu.Framework.Bindables;
using osu.Framework.Graphics.Shapes;
using typebeat.Game.Graphics;
using osu.Framework.Graphics.Sprites;
using osuTK;
using osu.Framework.Input.Events;
using osu.Framework.Localisation;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Resources.Localisation.Web;

namespace typebeat.Game.Overlays.Comments
{
    public partial class CommentsHeader : CompositeDrawable
    {
        public readonly Bindable<CommentsSortCriteria> Sort = new Bindable<CommentsSortCriteria>();

        private readonly Box background;

        public CommentsHeader()
        {
            RelativeSizeAxes = Axes.X;
            Height = 40;

            AddRangeInternal(new Drawable[]
            {
                background = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                },
                new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Padding = new MarginPadding { Horizontal = WaveOverlayContainer.HORIZONTAL_PADDING },
                    // No "show deleted" toggle: the server never sends deleted comments.
                    Child = sortTabs = new OverlaySortTabControl<CommentsSortCriteria>
                    {
                        Anchor = Anchor.CentreLeft,
                        Origin = Anchor.CentreLeft,
                        Current = Sort
                    },
                }
            });

            // No "top" sort: the server's comments carry no votes to rank by.
            sortTabs.TabControl.RemoveItem(CommentsSortCriteria.Top);
        }

        private readonly OverlaySortTabControl<CommentsSortCriteria> sortTabs;

        [BackgroundDependencyLoader]
        private void load(OverlayColourProvider colourProvider)
        {
            background.Colour = colourProvider.Background4;
        }
    }

    public enum CommentsSortCriteria
    {
        [LocalisableDescription(typeof(SortStrings), nameof(SortStrings.New))]
        New,

        [LocalisableDescription(typeof(SortStrings), nameof(SortStrings.Old))]
        Old,

        [LocalisableDescription(typeof(SortStrings), nameof(SortStrings.Top))]
        Top
    }
}
