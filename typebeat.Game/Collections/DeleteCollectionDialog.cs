// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Localisation;
using typebeat.Game.Database;
using typebeat.Game.Localisation;
using typebeat.Game.Overlays.Dialog;

namespace typebeat.Game.Collections
{
    public partial class DeleteCollectionDialog : DeletionDialog
    {
        public DeleteCollectionDialog(Live<BeatmapCollection> collection, Action deleteAction)
        {
            BodyText = collection.PerformRead(c => LocalisableString.Interpolate($"{c.Name} ({CommonStrings.BeatmapsCount(c.BeatmapMD5Hashes.Count)})"));
            DangerousAction = deleteAction;
        }
    }
}
