// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Replays;

namespace typebeat.Game.Rulesets.TypeBeat.Mods
{
    /// <summary>
    /// Watch a perfect automated play: <see cref="TypeBeatAutoGenerator"/> presses every typeable
    /// cell's expected character exactly at its target time. Rides the standard lazer autoplay
    /// plumbing (song select swaps in a <c>ReplayPlayer</c> fed by <see cref="CreateReplayData"/>,
    /// and the editor's test-play autoplay toggle picks it up too).
    /// </summary>
    public class TypeBeatModAutoplay : ModAutoplay
    {
        public override ModReplayData CreateReplayData(IBeatmap beatmap, IReadOnlyList<Mod> mods)
            => new ModReplayData(new TypeBeatAutoGenerator(beatmap).Generate(), new ModCreatedUser { Username = "typebot" });
    }
}
