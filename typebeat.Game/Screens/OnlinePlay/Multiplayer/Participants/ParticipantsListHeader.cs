// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Online.Multiplayer;
using typebeat.Game.Resources.Localisation.Web;

namespace typebeat.Game.Screens.OnlinePlay.Multiplayer.Participants
{
    public partial class ParticipantsListHeader : SectionHeader
    {
        [Resolved]
        private MultiplayerClient client { get; set; } = null!;

        public ParticipantsListHeader()
            : base(RankingsStrings.SpotlightParticipants)
        {
        }

        protected override void Update()
        {
            base.Update();

            var room = client.Room;
            if (room == null)
                return;

            DetailsText.Value = room.Settings.MaxParticipants == null ? $@"{room.Users.Count}" : $@"{room.Users.Count} / {room.Settings.MaxParticipants}";
        }
    }
}
