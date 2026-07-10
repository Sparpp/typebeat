// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Online.API;
using typebeat.Game.Online.API.Requests;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Resources.Localisation.Web;

namespace typebeat.Game.Overlays.Comments
{
    public partial class ReportCommentPopover : ReportPopover<CommentReportReason>
    {
        private readonly Comment comment;

        protected override bool IsCommentRequired(CommentReportReason reason) => reason == CommentReportReason.Other;

        public ReportCommentPopover(Comment comment)
            : base(ReportStrings.CommentTitle(comment.User?.Username ?? comment.LegacyName ?? @"Someone"), false)
        {
            this.comment = comment;
        }

        protected override APIRequest GetRequest(CommentReportReason reason, string comments) => new CommentReportRequest(comment.Id, reason, comments);
    }
}
