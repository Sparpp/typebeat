// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Bindables;
using typebeat.Game.Screens.Play;

namespace typebeat.Game.Rulesets.UI
{
    public interface IFrameStableClock : IGameplayClock
    {
        IBindable<bool> IsCatchingUp { get; }

        /// <summary>
        /// Whether the frame stable clock is waiting on new frames to arrive to be able to progress time.
        /// </summary>
        IBindable<bool> WaitingOnFrames { get; }

        /// <summary>
        /// Whether the time frame-stable children are being updated at right now is still short of
        /// the source clock's, i.e. this update is one step of a catch-up (a seek being walked one
        /// frame interval at a time) and the container will update them again, this frame or the
        /// next, at a time closer to it. Unlike <see cref="IsCatchingUp"/> it has no threshold, so
        /// false means this update is at the time the source asked for. Meaningful to the children,
        /// during their update.
        /// </summary>
        bool CatchUpPending { get; }
    }
}
