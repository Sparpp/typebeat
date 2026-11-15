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
    /// <summary>
    /// The set's song languages (backlog 472: a set may declare several), comma separated, each a link to the listing's
    /// language filter where osu's language list has it.
    /// </summary>
    public partial class MetadataSectionLanguage : MetadataSection<BeatmapSetOnlineLanguage[]>
    {
        public MetadataSectionLanguage(Action<BeatmapSetOnlineLanguage[]>? searchAction = null)
            : base(MetadataType.Language, searchAction)
        {
        }

        protected override void AddMetadata(BeatmapSetOnlineLanguage[] metadata, LinkFlowContainer loaded)
        {
            for (int i = 0; i < metadata.Length; i++)
            {
                if (i > 0)
                    loaded.AddText(", ");

                var language = (SearchLanguage)metadata[i].Id;

                if (Enum.IsDefined(language))
                    loaded.AddLink(language.GetLocalisableDescription(), LinkAction.FilterBeatmapSetLanguage, language);
                else
                    loaded.AddText(metadata[i].Name);
            }
        }
    }
}
