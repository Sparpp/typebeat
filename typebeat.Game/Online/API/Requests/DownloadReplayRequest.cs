// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Scoring;

namespace typebeat.Game.Online.API.Requests
{
    public class DownloadReplayRequest : ArchiveDownloadRequest<IScoreInfo>
    {
        public DownloadReplayRequest(IScoreInfo score)
            : base(score)
        {
        }

        protected override string FileExtension => ".osr";

        protected override string Target => $@"scores/{Model.OnlineID}/download";
    }
}
