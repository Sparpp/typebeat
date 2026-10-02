// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Linq;
using System.Text;
using osu.Framework.Platform;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.Formats;

namespace typebeat.Game.Database
{
    /// <summary>
    /// Exporter for native beatmap archives (.typb, the "type!beat" package: the same zip layout
    /// as .osz, preserving file contents except for synchronising stored online IDs in [Metadata]).
    /// </summary>
    public class BeatmapExporter : LegacyArchiveExporter<BeatmapSetInfo>
    {
        public BeatmapExporter(Storage storage)
            : base(storage)
        {
        }

        protected override Stream? GetFileContents(BeatmapSetInfo model, INamedFileUsage file)
        {
            var source = base.GetFileContents(model, file);

            if (source == null || !file.Filename.EndsWith(".osu", StringComparison.OrdinalIgnoreCase))
                return source;

            var beatmapInfo = model.Beatmaps.SingleOrDefault(b => b.Hash == file.File.Hash);

            if (beatmapInfo == null)
                return source;

            // Older imports can have no embedded IDs even after the database has been assigned
            // an online set ID. Patch metadata only, preserving lyrics and other native fields.
            using (source)
            using (var contents = new MemoryStream())
            {
                source.CopyTo(contents);
                string text = Encoding.UTF8.GetString(contents.ToArray());
                string encoded = BeatmapOnlineIdEncoder.Encode(text, beatmapInfo.OnlineID, model.OnlineID);
                return new MemoryStream(Encoding.UTF8.GetBytes(encoded));
            }
        }

        protected override bool UseFixedEncoding => false;

        protected override string FileExtension => @".typb";
    }
}
