// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using osu.Framework.Platform;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.Formats;
using typebeat.Game.Database;
using typebeat.Game.IO;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Storyboards;
using Decoder = typebeat.Game.Beatmaps.Formats.Decoder;

namespace typebeat.Game.Screens.Edit.Submission
{
    /// <summary>
    /// Exports a beatmap set for submission, stamping the server-allocated online IDs into each
    /// difficulty. Unlike lazer's submission exporter this re-encodes each <c>.osu</c> through the
    /// ruleset's NATIVE format encoder (the "type!beat file format v1" with its [Lyrics] section),
    /// the legacy encoder cannot represent [Lyrics] and would destroy the map. All other files
    /// (audio, background, video) are copied byte-for-byte by the base exporter.
    /// </summary>
    public class SubmissionBeatmapExporter : BeatmapExporter
    {
        private readonly uint beatmapSetId;
        private readonly HashSet<int> allocatedBeatmapIds;

        public SubmissionBeatmapExporter(Storage storage, PutBeatmapSetResponse putBeatmapSetResponse)
            : base(storage)
        {
            beatmapSetId = putBeatmapSetResponse.BeatmapSetId;
            allocatedBeatmapIds = putBeatmapSetResponse.BeatmapIds.Select(id => (int)id).ToHashSet();
        }

        protected override Stream? GetFileContents(BeatmapSetInfo model, INamedFileUsage file)
        {
            var beatmapInfo = model.Beatmaps.SingleOrDefault(o => o.Hash == file.File.Hash);

            if (beatmapInfo == null)
                return base.GetFileContents(model, file);

            using var contentStream = base.GetFileContents(model, file);

            if (contentStream == null)
                return null;

            Beatmap beatmap;

            using (var reader = new LineBufferedReader(contentStream))
                beatmap = Decoder.GetDecoder<Beatmap>(reader).Decode(reader);

            // The native format carries the video reference as a storyboard element of the same
            // file; it must be decoded and passed to the encoder or it would be dropped.
            using var storyboardStream = base.GetFileContents(model, file);

            if (storyboardStream == null)
                return null;

            Storyboard storyboard;

            using (var reader = new LineBufferedReader(storyboardStream))
                storyboard = Decoder.GetDecoder<Storyboard>(reader).Decode(reader);

            // The database model is the ID authority: files saved before online submission
            // existed carry no embedded IDs, but the realm-side OnlineID always reflects the
            // last submission.
            beatmap.BeatmapInfo.OnlineID = resolveOnlineId(beatmapInfo);
            beatmap.BeatmapInfo.BeatmapSet = new BeatmapSetInfo { OnlineID = (int)beatmapSetId };

            var rulesetInstance = beatmapInfo.Ruleset.CreateInstance();

            if (!rulesetInstance.CanEncodeToNativeFormat)
                throw new InvalidOperationException($@"Difficulty ""{beatmapInfo.DifficultyName}"" has no native format encoder to stamp online IDs with.");

            var stream = new MemoryStream();

            using (var sw = new StreamWriter(stream, Encoding.UTF8, 1024, true))
                rulesetInstance.EncodeToNativeFormat(beatmap, storyboard, sw);

            stream.Seek(0, SeekOrigin.Begin);

            return stream;
        }

        /// <summary>
        /// Mirrors lazer's <c>SubmissionBeatmapExporter.MutateBeatmap</c> ID assignment: an ID the
        /// server allocated is kept, an unrecognised positive ID is an error, and IDs minted for
        /// new difficulties are consumed in file order.
        /// </summary>
        private int resolveOnlineId(BeatmapInfo beatmapInfo)
        {
            if (allocatedBeatmapIds.Remove(beatmapInfo.OnlineID))
                return beatmapInfo.OnlineID;

            if (beatmapInfo.OnlineID > 0)
                throw new InvalidOperationException($@"Difficulty ""{beatmapInfo.DifficultyName}"" has BeatmapID {beatmapInfo.OnlineID} that has not been assigned to it by the server!");

            if (allocatedBeatmapIds.Count == 0)
                throw new InvalidOperationException(@"Ran out of new beatmap IDs to assign to unsubmitted beatmaps!");

            int newId = allocatedBeatmapIds.First();
            allocatedBeatmapIds.Remove(newId);
            return newId;
        }
    }
}
