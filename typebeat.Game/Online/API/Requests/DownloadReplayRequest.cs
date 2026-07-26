// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Scoring;

namespace typebeat.Game.Online.API.Requests
{
    /// <summary>
    /// Fetches the stored .osr for an online score.
    /// </summary>
    /// <remarks>
    /// Wire contract, fixed against the server: <c>GET /api/v2/scores/{scoreId}/replay</c>, public,
    /// raw .osr bytes, 404 when nothing is stored. Note this keys off the score's <c>OnlineID</c>
    /// only, so a legacy-only score (no solo id) cannot be fetched.
    /// </remarks>
    public class DownloadReplayRequest : ArchiveDownloadRequest<IScoreInfo>
    {
        public DownloadReplayRequest(IScoreInfo score)
            : base(score)
        {
        }

        protected override string FileExtension => ".osr";

        /// <summary>
        /// The API target path for a given score. Exposed so the wire shape can be pinned by tests
        /// without reaching through the protected <see cref="Target"/> member.
        /// </summary>
        public static string TargetFor(long scoreId) => $@"scores/{scoreId}/replay";

        protected override string Target => TargetFor(Model.OnlineID);
    }
}
