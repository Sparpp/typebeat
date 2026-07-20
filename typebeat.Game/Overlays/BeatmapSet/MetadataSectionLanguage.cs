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
    public partial class MetadataSectionLanguage : MetadataSection<BeatmapSetOnlineLanguage>
    {
        public MetadataSectionLanguage(Action<BeatmapSetOnlineLanguage>? searchAction = null)
            : base(MetadataType.Language, searchAction)
        {
        }

        protected override void AddMetadata(BeatmapSetOnlineLanguage metadata, LinkFlowContainer loaded)
        {
            var language = (SearchLanguage)metadata.Id;

            if (Enum.IsDefined(language))
                loaded.AddLink(language.GetLocalisableDescription(), LinkAction.FilterBeatmapSetLanguage, language);
            else
                loaded.AddText(metadata.Name);
        }
    }
}
