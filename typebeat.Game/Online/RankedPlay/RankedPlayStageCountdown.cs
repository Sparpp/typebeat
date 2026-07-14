// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using MessagePack;
using typebeat.Game.Online.Multiplayer;
using typebeat.Game.Online.Multiplayer.MatchTypes.RankedPlay;

namespace typebeat.Game.Online.RankedPlay
{
    [MessagePackObject]
    public class RankedPlayStageCountdown : MultiplayerCountdown
    {
        [Key(2)]
        public RankedPlayStage Stage { get; set; }
    }
}
