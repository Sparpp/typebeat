// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Tests.Visual.OnlinePlay;
using typebeat.Game.Tests.Visual.Spectator;

namespace typebeat.Game.Tests.Visual.Multiplayer
{
    /// <summary>
    /// Interface that defines the dependencies required for multiplayer test scenes.
    /// </summary>
    public interface IMultiplayerTestSceneDependencies : IOnlinePlayTestSceneDependencies
    {
        /// <summary>
        /// The cached <see cref="Online.Multiplayer.MultiplayerClient"/>.
        /// </summary>
        TestMultiplayerClient MultiplayerClient { get; }

        /// <summary>
        /// The cached <see cref="typebeat.Game.Online.Spectator.SpectatorClient"/>.
        /// </summary>
        TestSpectatorClient SpectatorClient { get; }
    }
}
