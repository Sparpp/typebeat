// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using typebeat.Game.IO.Serialization.Converters;
using typebeat.Game.Rulesets.Objects;

namespace typebeat.Game.Screens.Edit
{
    public class ClipboardContent
    {
        [JsonConverter(typeof(TypedListConverter<HitObject>))]
        public IList<HitObject> HitObjects;

        public ClipboardContent()
        {
        }

        public ClipboardContent(EditorBeatmap editorBeatmap)
        {
            HitObjects = editorBeatmap.SelectedHitObjects.ToList();
        }
    }
}
