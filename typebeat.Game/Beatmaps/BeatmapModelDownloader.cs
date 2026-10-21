// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Database;
using typebeat.Game.Online.API;
using typebeat.Game.Online.API.Requests;

namespace typebeat.Game.Beatmaps
{
    public class BeatmapModelDownloader : ModelDownloader<BeatmapSetInfo, IBeatmapSetInfo>
    {
        protected override ArchiveDownloadRequest<IBeatmapSetInfo> CreateDownloadRequest(IBeatmapSetInfo set, bool minimiseDownloadSize) =>
            new DownloadBeatmapSetRequest(set, minimiseDownloadSize);

        public override ArchiveDownloadRequest<IBeatmapSetInfo>? GetExistingDownload(IBeatmapSetInfo model)
            => CurrentDownloads.Find(r => r.Model.OnlineID == model.OnlineID);

        public bool Download(IBeatmapSetInfo model, bool withoutVideo) => Download(model, withoutVideo, null);

        public void DownloadAsUpdate(BeatmapSetInfo originalModel, bool withoutVideo) => Download(originalModel, withoutVideo, originalModel);

        public BeatmapModelDownloader(IModelImporter<BeatmapSetInfo> beatmapImporter, IAPIProvider api)
            : base(beatmapImporter, api)
        {
        }
    }
}
