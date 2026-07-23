// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Platform;
using typebeat.Game.Beatmaps;

namespace typebeat.Game.Database
{
    /// <summary>
    /// Exporter for native beatmap archives (.typb, the "type!beat" package: the same zip layout
    /// as .osz, but with files copied verbatim so the [Lyrics] section survives).
    /// </summary>
    public class BeatmapExporter : LegacyArchiveExporter<BeatmapSetInfo>
    {
        public BeatmapExporter(Storage storage)
            : base(storage)
        {
        }

        protected override bool UseFixedEncoding => false;

        protected override string FileExtension => @".typb";
    }
}
