// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Extensions;
using typebeat.Game.Beatmaps;
using typebeat.Game.Graphics.Containers;
using typebeat.Game.Online.Chat;
using typebeat.Game.Overlays.BeatmapListing;

namespace typebeat.Game.Overlays.BeatmapSet
{
    public partial class MetadataSectionGenre : MetadataSection<BeatmapSetOnlineGenre>
    {
        public MetadataSectionGenre(Action<BeatmapSetOnlineGenre>? searchAction = null)
            : base(MetadataType.Genre, searchAction)
        {
        }

        protected override void AddMetadata(BeatmapSetOnlineGenre metadata, LinkFlowContainer loaded)
        {
            var genre = (SearchGenre)metadata.Id;

            if (Enum.IsDefined(genre))
                loaded.AddLink(genre.GetLocalisableDescription(), LinkAction.FilterBeatmapSetGenre, genre);
            else
                loaded.AddText(metadata.Name);
        }
    }
}
