// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using typebeat.Game.Graphics;
using typebeat.Game.Graphics.Containers;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Resources.Localisation.Web;
using osuTK;

namespace typebeat.Game.Overlays.Comments
{
    public partial class CommentAuthorLine : FillFlowContainer
    {
        private readonly Comment comment;
        private readonly IReadOnlyList<CommentableMeta> meta;

        public CommentAuthorLine(Comment comment, IReadOnlyList<CommentableMeta> meta)
        {
            this.comment = comment;
            this.meta = meta;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            AutoSizeAxes = Axes.Both;
            Direction = FillDirection.Horizontal;
            Spacing = new Vector2(4, 0);

            Add(new LinkFlowContainer(s => s.Font = OsuFont.GetFont(size: 14, weight: FontWeight.Bold))
            {
                AutoSizeAxes = Axes.Both
            }.With(username =>
            {
                if (comment.UserId.HasValue)
                    username.AddUserLink(comment.User);
                else
                    username.AddText(comment.LegacyName!);
            }));

            var ownerMeta = meta.FirstOrDefault(m => m.Id == comment.CommentableId && m.Type == comment.CommentableType);

            if (ownerMeta?.OwnerId != null && ownerMeta.OwnerId == comment.UserId)
            {
                Add(new OwnerTitleBadge(ownerMeta.OwnerTitle ?? string.Empty)
                {
                    // add top space to align with username
                    Margin = new MarginPadding { Top = 1f },
                });
            }

            // No pinned notice, reply-to link or "deleted" label: comments are never pinned, have no replies, and are never
            // listed once deleted.
        }

        private partial class OwnerTitleBadge : CircularContainer
        {
            private readonly string title;

            public OwnerTitleBadge(string title)
            {
                this.title = title;
            }

            [BackgroundDependencyLoader]
            private void load(OverlayColourProvider colourProvider)
            {
                AutoSizeAxes = Axes.Both;
                Masking = true;

                InternalChildren = new Drawable[]
                {
                    new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = colourProvider.Light1,
                    },
                    new OsuSpriteText
                    {
                        Text = title,
                        Font = OsuFont.Default.With(size: 10, weight: FontWeight.Bold),
                        Margin = new MarginPadding { Vertical = 2, Horizontal = 5 },
                        Colour = colourProvider.Background6,
                    },
                };
            }
        }
    }
}
