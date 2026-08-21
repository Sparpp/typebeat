// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using typebeat.Game.Rulesets.Replays;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.Replays
{
    /// <summary>
    /// The one definition of how recorded frames reach a <see cref="TypingEngine"/>. Three callers
    /// share it and MUST NOT drift, because each of them claims to reproduce the others' judgement:
    /// live playback (<c>TypeBeatPlayfield.EngineTicker</c>), headless recalculation
    /// (<see cref="Scoring.TypeBeatReplayScorer"/>), and the backwards-seek rebuild
    /// (<see cref="RebuildTo"/>).
    /// </summary>
    public static class ReplayEngineFeed
    {
        /// <summary>
        /// The display-frame period a rebuild ticks the engine at, matching the 60Hz cadence the
        /// headless scorer uses. Judgement does not depend on it (see <see cref="Apply"/>); the WPM
        /// clock does, because its accrual is sampled per tick, so a rebuild and a straight run
        /// agree on <see cref="TypingEngine.LiveWpm"/> only when both step at the same rate.
        /// </summary>
        public const double FRAME_MS = 1000.0 / 60;

        /// <summary>
        /// Apply one recorded frame: exactly the live sequence, state advanced to the keystroke's own
        /// timestamp and then the keystroke. <see cref="TypingEngine.Update"/> is monotonic and
        /// idempotent, so whatever other times the caller ticks the engine at cannot change a
        /// judgement; the cadence only decides when a seal lands relative to the next keystroke, and
        /// the recorded times already fix that.
        ///
        /// <para><c>clockRate</c> is handed to <see cref="TypingEngine.Update"/> so its WPM clock
        /// counts real seconds rather than beatmap ones under a rate mod. Judgement never reads
        /// it.</para>
        /// </summary>
        public static void Apply(TypingEngine engine, TypeBeatReplayFrame frame, double clockRate = 1)
        {
            if (frame.IsConfig)
            {
                // The recorded machine's judgement-relevant settings win over local config, all
                // three of them: a replay of a run played WITHOUT space-skip must not start skipping
                // words because the watcher turned the setting on, and vice versa. Every replay
                // recorded before a setting existed carries its bit clear, which decodes to false,
                // i.e. to exactly the model those runs were played under.
                //
                // SyllableTiming (backlog 179) is the same seam doing ERA work: the live client
                // records the bit set, so a new replay re-derives on syllable spans, and every
                // replay written before it re-derives on the classic point targets it was judged on.
                // Applied here rather than at each of the three call sites for the same reason the
                // other two are: this is the one place a recorded frame reaches an engine.
                engine.AllowWrongInput = frame.AllowWrongInput;
                engine.SpaceSkipsWord = frame.SpaceSkipsWord;
                engine.SyllableTiming = frame.SyllableTiming;
                return;
            }

            engine.Update(frame.Time, clockRate);

            if (frame.IsBackspace)
                engine.ProcessBackspace();
            else
                engine.ProcessKey(frame.Character, frame.Time);
        }

        /// <summary>
        /// Re-derive engine state at <paramref name="time"/> from scratch, by resetting and replaying
        /// every frame at or before it (see <see cref="TypingEngine.Rebuild"/>, which also makes the
        /// replay silent and raises <c>Rewound</c> at the end). Returns the index of the first frame
        /// NOT applied, which is what a feeder resumes from.
        ///
        /// <para>This is what makes a BACKWARDS SEEK work at all. The engine has no reverse gear:
        /// <see cref="TypingEngine.Update"/> clamps its delta at zero and seals lines in one
        /// direction only, so a rewound clock would otherwise leave every cell, the caret and the
        /// active line pinned at their pre-seek values while the song played on. Replaying the whole
        /// prefix is EXACT rather than an approximation of a rewind, and cheap: a five-minute seek
        /// target is about 18000 ticks of an engine whose idle tick does nothing.</para>
        /// </summary>
        public static int RebuildTo(TypingEngine engine, IReadOnlyList<ReplayFrame> frames, double time, double clockRate = 1)
        {
            int next = 0;

            engine.Rebuild(e =>
            {
                for (double now = 0; now <= time; now += FRAME_MS)
                {
                    next = feedDue(e, frames, now, next, clockRate);
                    e.Update(now, clockRate);
                }

                // The last tick of the loop lands just short of the target (the step rarely divides
                // it), so close the gap: any frame in that remainder still has to be applied, and the
                // engine has to end up reading exactly the time the caller seeked to.
                next = feedDue(e, frames, time, next, clockRate);
                e.Update(time, clockRate);
            });

            return next;
        }

        private static int feedDue(TypingEngine engine, IReadOnlyList<ReplayFrame> frames, double now, int next, double clockRate)
        {
            while (next < frames.Count && frames[next].Time <= now)
            {
                if (frames[next] is TypeBeatReplayFrame frame)
                    Apply(engine, frame, clockRate);

                next++;
            }

            return next;
        }
    }
}
