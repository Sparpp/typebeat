// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using typebeat.Game.Rulesets.Replays;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.Replays
{
    /// <summary>
    /// Where a replay feed stands against its engine while the replay is being WATCHED: the next
    /// frame to apply, the lyric time the feed was last advanced under, and a backwards seek still in
    /// progress. This is the replay half of <c>TypeBeatPlayfield.EngineTicker</c>, kept apart from the
    /// drawable so a test can drive it tick by tick exactly as the frame-stable clock does.
    ///
    /// <para>A BACKWARDS SEEK is re-derived with <see cref="ReplayEngineFeed.RebuildTo"/>, which
    /// replays the whole prefix. The frame-stable clock does not jump there: it walks back one
    /// 16.7 ms sub-step at a time, holds for one extra sub-step on every replay frame it crosses
    /// (<c>FramedReplayInputHandler</c> stops on a frame boundary before stepping past it), and ticks
    /// this feed on every one. Rebuilding on each backwards sub-step made a long rewind cost
    /// sub-steps times prefix length (backlog 424 measured 10036 rebuilds and 12.9 s of wall time to
    /// rewind a three-minute map from its end to 10 s, against 2.5 s for the same walk forwards). So
    /// the rebuild is DEFERRED while the walk is still in progress and performed ONCE, at the time the
    /// walk stopped: on the sub-step the clock reports as the one it was asked for (the
    /// <c>clockSettled</c> argument, <c>IFrameStableClock.CatchUpPending</c> clear), or on the first
    /// tick that moves forwards again, whichever comes first. The engine is neither fed nor ticked in
    /// between.</para>
    ///
    /// <para>This changes no state. Per sub-step, every rebuild but the last was thrown away by the
    /// next one (<see cref="TypingEngine.Rebuild"/> starts from a reset), and the ticks between them
    /// re-ticked an engine already standing at their time, so the engine after the walk was
    /// <c>RebuildTo(walk end)</c> at the clock rate of the walk's last backwards sub-step, followed by
    /// the ordinary forward feed; that is exactly the call sequence <see cref="Settle"/> and the
    /// forward feed here make. Nothing reads a judgement off the engine mid-walk either: the drawables
    /// take results only from engine events, and an unfed, unticked engine raises none. What a long
    /// walk does show is the stack as it stood before the seek, until the walk ends.</para>
    /// </summary>
    public sealed class ReplayFeedCursor
    {
        /// <summary>The index of the first frame not yet applied.</summary>
        public int NextFrameIndex { get; private set; }

        /// <summary>
        /// The lyric time of the last tick, i.e. the high-water mark <see cref="NextFrameIndex"/> was
        /// advanced under, or the lowest time a backwards walk has reached so far. Anything earlier
        /// arriving on the next tick is a BACKWARDS SEEK. Negative infinity until the first tick, so
        /// the first tick of a play is never mistaken for one.
        /// </summary>
        public double LastFedTime { get; private set; } = double.NegativeInfinity;

        /// <summary>
        /// The clock rate the walk's latest backwards sub-step sampled, while a walk is waiting for
        /// its one rebuild; null otherwise.
        /// </summary>
        private double? pendingRebuildRate;

        /// <summary>Whether a backwards walk has gone past the engine and its rebuild is still owed.</summary>
        public bool RebuildPending => pendingRebuildRate != null;

        /// <summary>Feed from the first frame again, with no seek owed (a newly attached replay).</summary>
        public void Restart()
        {
            NextFrameIndex = 0;
            LastFedTime = double.NegativeInfinity;
            pendingRebuildRate = null;
        }

        /// <summary>
        /// One tick of the feed at lyric time <paramref name="now"/>. A backwards step records the
        /// walk, and while the walk is still in progress that is all the tick does. Otherwise a walk
        /// that has ended is settled and every frame now due is applied through
        /// <see cref="ReplayEngineFeed.Apply"/>.
        /// </summary>
        /// <param name="engine">The engine being fed.</param>
        /// <param name="frames">The replay's frames.</param>
        /// <param name="now">The lyric time of this tick.</param>
        /// <param name="clockRate">The WPM clock rate (see <see cref="ReplayEngineFeed.Apply"/>).</param>
        /// <param name="clockSettled">Whether this tick is at the time the clock is heading for, so no
        /// further step of a seek follows it. True wherever no frame-stable clock walks the seek, which
        /// rebuilds on the backwards tick itself.</param>
        /// <returns>Whether the engine is current at <paramref name="now"/> and may be ticked to it;
        /// false while a backwards walk is still in progress.</returns>
        public bool Advance(TypingEngine engine, IReadOnlyList<ReplayFrame> frames, double now, double clockRate, bool clockSettled = true)
        {
            if (now < LastFedTime)
            {
                pendingRebuildRate = clockRate;
                LastFedTime = now;
            }

            // Still walking: a backwards step, or the clock holding on a frame boundary on its way
            // back, with more steps to come. A forward tick always settles (at the walk's low point).
            if (RebuildPending && now == LastFedTime && !clockSettled)
                return false;

            Settle(engine, frames);

            LastFedTime = now;

            while (NextFrameIndex < frames.Count && frames[NextFrameIndex].Time <= now)
            {
                if (frames[NextFrameIndex] is TypeBeatReplayFrame frame)
                    ReplayEngineFeed.Apply(engine, frame, clockRate);

                NextFrameIndex++;
            }

            return true;
        }

        /// <summary>
        /// Perform the rebuild a backwards walk owes, if any, to the time the walk reached. Called by
        /// <see cref="Advance"/> once the walk has stopped, and by the ticker before it lets go of
        /// <paramref name="frames"/>' replay (a swap or a detach), so a seek made just before is
        /// never dropped.
        /// </summary>
        public void Settle(TypingEngine engine, IReadOnlyList<ReplayFrame> frames)
        {
            if (pendingRebuildRate is not double rate)
                return;

            pendingRebuildRate = null;
            NextFrameIndex = ReplayEngineFeed.RebuildTo(engine, frames, LastFedTime, rate);
        }
    }
}
