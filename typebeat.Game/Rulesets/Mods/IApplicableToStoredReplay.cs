// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Replays;

namespace typebeat.Game.Rulesets.Mods
{
    /// <summary>
    /// Interface for a <see cref="Mod"/> whose effect on the playable beatmap depends on the stored
    /// replay being played back, because the replay records an era that changes what the beatmap is
    /// built into. <see cref="Screens.Play.Player"/> calls it on its own copies of the gameplay mods
    /// BEFORE the playable beatmap is built, whenever the replay is known by then (a
    /// <see cref="Screens.Play.ReplayPlayer"/> handed a stored score); a live play never calls it.
    /// </summary>
    public interface IApplicableToStoredReplay : IApplicableMod
    {
        /// <summary>
        /// Reads whatever this <see cref="Mod"/> needs from <paramref name="replay"/> before the
        /// playable beatmap is built.
        /// </summary>
        /// <param name="replay">The stored replay about to be played back.</param>
        void ApplyToStoredReplay(Replay replay);
    }
}
