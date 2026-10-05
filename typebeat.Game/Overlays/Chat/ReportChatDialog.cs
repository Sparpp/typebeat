// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Online.API;
using typebeat.Game.Online.API.Requests;
using typebeat.Game.Online.Chat;
using typebeat.Game.Resources.Localisation.Web;

namespace typebeat.Game.Overlays.Chat
{
    public partial class ReportChatDialog : ReportDialog<ChatReportReason>
    {
        private readonly Message message;

        public ReportChatDialog(Message message)
            : base(ReportStrings.UserTitle(message.Sender?.Username ?? @"Someone"), false)
        {
            this.message = message;
        }

        protected override APIRequest CreateRequest(ChatReportReason reason, string comments) => new ChatReportRequest(message.Id, reason, comments);

        protected override bool IsCommentRequired(ChatReportReason reason) => reason == ChatReportReason.Other;
    }
}
