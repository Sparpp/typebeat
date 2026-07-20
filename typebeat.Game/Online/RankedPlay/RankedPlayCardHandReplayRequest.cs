// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using MessagePack;
using typebeat.Game.Online.Multiplayer;

namespace typebeat.Game.Online.RankedPlay
{
    [Serializable]
    [MessagePackObject]
    public class RankedPlayCardHandReplayRequest : MatchUserRequest
    {
        [Key(0)]
        public required RankedPlayCardHandReplayFrame[] Frames { get; init; }
    }
}
