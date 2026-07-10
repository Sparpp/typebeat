// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Bindables;
using osu.Framework.Screens;
using typebeat.Game.Beatmaps;
using typebeat.Game.Online.Rooms;
using typebeat.Game.Rulesets;

namespace typebeat.Game.Screens.OnlinePlay
{
    public partial class PlaylistsRoomFreestyleSelect : OnlinePlayFreestyleSelect
    {
        public new readonly Bindable<BeatmapInfo?> Beatmap = new Bindable<BeatmapInfo?>();
        public new readonly Bindable<RulesetInfo?> Ruleset = new Bindable<RulesetInfo?>();

        public PlaylistsRoomFreestyleSelect(PlaylistItem item)
            : base(item)
        {
        }

        protected override void StartAction()
        {
            Beatmap.Value = base.Beatmap.Value.BeatmapInfo;
            Ruleset.Value = base.Ruleset.Value;

            this.Exit();
        }
    }
}
