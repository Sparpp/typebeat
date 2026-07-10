// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Edit.Checks.Components;

namespace typebeat.Game.Rulesets.Edit.Checks
{
    public class CheckAudioPresence : CheckFilePresence
    {
        protected override CheckCategory Category => CheckCategory.Audio;
        protected override string TypeOfFile => "audio";
        protected override string GetFilename(IBeatmap beatmap) => beatmap.Metadata.AudioFile;
    }
}
