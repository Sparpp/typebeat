// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Online.API;
using typebeat.Game.Online.API.Requests;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Resources.Localisation.Web;

namespace typebeat.Game.Overlays.Profile
{
    public partial class ReportUserPopover : ReportPopover<UserReportReason>
    {
        private readonly APIUser user;

        public ReportUserPopover(APIUser user)
            : base(ReportStrings.UserTitle(user.Username))
        {
            this.user = user;
        }

        protected override APIRequest GetRequest(UserReportReason reason, string comments) => new UserReportRequest(user.Id, reason, comments);
    }
}
