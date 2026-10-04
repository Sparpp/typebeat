// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics;
using typebeat.Game.Graphics;
using osuTK;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Users.Drawables;
using typebeat.Game.Graphics.Containers;
using System.Collections.Generic;
using osu.Framework.Allocation;
using osu.Framework.Extensions.LocalisationExtensions;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Logging;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Localisation;
using typebeat.Game.Online.API;
using typebeat.Game.Online.API.Requests;
using typebeat.Game.Overlays.Dialog;
using WebCommonStrings = typebeat.Game.Resources.Localisation.Web.CommonStrings;

namespace typebeat.Game.Overlays.Comments
{
    /// <summary>
    /// One comment: avatar, author, message, date, and a delete action on your own comments.
    /// </summary>
    /// <remarks>
    /// Lazer's comment is a node in a reply tree with votes, an edit history and pinning. The server's set comments are
    /// flat and keep none of those, so this is the flat row: no vote pill, no reply / show-replies / load-more-replies
    /// buttons, no collapse chevron, no child comments or deleted-replies counter, and no "edited by" line.
    /// </remarks>
    public partial class DrawableComment : CompositeDrawable
    {
        private const int avatar_size = 40;

        public readonly Comment Comment;
        public readonly IReadOnlyList<CommentableMeta> Meta;

        private LinkFlowContainer actionsContainer = null!;
        private LoadingSpinner actionsLoading = null!;

        [Resolved]
        private IDialogOverlay? dialogOverlay { get; set; }

        [Resolved]
        private IAPIProvider api { get; set; } = null!;

        public DrawableComment(Comment comment, IReadOnlyList<CommentableMeta> meta)
        {
            Comment = comment;
            Meta = meta;
        }

        [BackgroundDependencyLoader]
        private void load(OverlayColourProvider colourProvider)
        {
            CommentMarkdownContainer message;

            RelativeSizeAxes = Axes.X;
            AutoSizeAxes = Axes.Y;
            InternalChildren = new Drawable[]
            {
                new Container
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Padding = new MarginPadding
                    {
                        Horizontal = WaveOverlayContainer.HORIZONTAL_PADDING,
                        Vertical = 15
                    },
                    Child = new GridContainer
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        ColumnDimensions = new[]
                        {
                            new Dimension(GridSizeMode.Absolute, size: avatar_size + 10),
                            new Dimension(),
                        },
                        RowDimensions = new[]
                        {
                            new Dimension(GridSizeMode.AutoSize)
                        },
                        Content = new[]
                        {
                            new Drawable[]
                            {
                                new UpdateableAvatar(Comment.User, showUserPanelOnHover: true)
                                {
                                    Size = new Vector2(avatar_size),
                                    Masking = true,
                                    CornerRadius = avatar_size / 2f,
                                    CornerExponent = 2,
                                },
                                new FillFlowContainer
                                {
                                    RelativeSizeAxes = Axes.X,
                                    AutoSizeAxes = Axes.Y,
                                    Direction = FillDirection.Vertical,
                                    Spacing = new Vector2(0, 4),
                                    Margin = new MarginPadding
                                    {
                                        Vertical = 2
                                    },
                                    Children = new Drawable[]
                                    {
                                        new CommentAuthorLine(Comment, Meta),
                                        message = new CommentMarkdownContainer
                                        {
                                            RelativeSizeAxes = Axes.X,
                                            AutoSizeAxes = Axes.Y,
                                            DocumentMargin = new MarginPadding(0),
                                            DocumentPadding = new MarginPadding(0),
                                        },
                                        new FillFlowContainer
                                        {
                                            AutoSizeAxes = Axes.Both,
                                            Direction = FillDirection.Horizontal,
                                            Spacing = new Vector2(10, 0),
                                            Children = new Drawable[]
                                            {
                                                new DrawableDate(Comment.CreatedAt, 12, false)
                                                {
                                                    Colour = colourProvider.Foreground1,
                                                },
                                                // Delete only (your own comments): replies, votes and comment reports are not
                                                // stored, and the website has no comment permalink page.
                                                actionsContainer = new LinkFlowContainer(s => s.Font = OsuFont.GetFont(size: 12, weight: FontWeight.Bold))
                                                {
                                                    Name = @"Actions buttons",
                                                    AutoSizeAxes = Axes.Both,
                                                },
                                                actionsLoading = new LoadingSpinner
                                                {
                                                    Size = new Vector2(12f),
                                                    Anchor = Anchor.TopLeft,
                                                    Origin = Anchor.TopLeft
                                                }
                                            }
                                        },
                                    }
                                }
                            }
                        }
                    }
                },
                new Box
                {
                    Anchor = Anchor.BottomCentre,
                    Origin = Anchor.BottomCentre,
                    RelativeSizeAxes = Axes.X,
                    Height = 1.5f,
                    Colour = OsuColour.Gray(0.1f)
                },
            };

            if (Comment.HasMessage)
                message.Text = Comment.Message;

            if (Comment.UserId.HasValue && Comment.UserId.Value == api.LocalUser.Value.Id)
                actionsContainer.AddLink(WebCommonStrings.ButtonsDelete.ToLower(), deleteComment);
        }

        /// <summary>
        /// Invokes comment deletion with confirmation.
        /// </summary>
        private void deleteComment()
        {
            if (dialogOverlay == null)
                deleteCommentRequest();
            else
                dialogOverlay.Push(new ConfirmDialog(DialogStrings.DeleteCommentBodyText, deleteCommentRequest));
        }

        /// <summary>
        /// Invokes comment deletion directly. A deleted comment leaves the list, as it does on the website (deleted comments
        /// are never served, so there is no "show deleted" to keep it around for).
        /// </summary>
        private void deleteCommentRequest()
        {
            actionsContainer.Hide();
            actionsLoading.Show();
            var request = new CommentDeleteRequest(Comment.Id);
            request.Success += _ => Schedule(() =>
            {
                actionsLoading.Hide();
                Hide();
            });
            request.Failure += e => Schedule(() =>
            {
                Logger.Error(e, "Failed to delete comment");
                actionsLoading.Hide();
                actionsContainer.Show();
            });
            api.Queue(request);
        }
    }
}
