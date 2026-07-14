// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Rulesets;

namespace typebeat.Game.Online.Rooms
{
    public class LoungeFilterCriteria
    {
        public string SearchString = string.Empty;
        public RoomModeFilter Mode;
        public RoomStatusFilter? Status;
        public string Category = string.Empty;
        public RulesetInfo? Ruleset;
        public RoomPermissionsFilter Permissions;
    }
}
