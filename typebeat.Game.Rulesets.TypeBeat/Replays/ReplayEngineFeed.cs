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
                // nine of them: a replay of a run played WITHOUT space-skip must not start skipping
                // words because the watcher turned the setting on, and vice versa. Every replay
                // recorded before a setting existed carries its bit clear, which decodes to false,
                // i.e. to exactly the model those runs were played under.
                //
                // SyllableTiming (backlog 179) is the same seam doing ERA work: the live client
                // records the bit set, so a new replay re-derives on syllable spans, and every
                // replay written before it re-derives on the classic point targets it was judged on.
                // WrongInputOnWordGaps (backlog 181) is the second such bit, and the one that has to
                // be applied HERE rather than anywhere later: an old replay's wrong key on a word
                // gap was rejected, so the caret did not move, and typing it through instead would
                // shift every keystroke after it onto the wrong cell. StrictSpaces (backlog 184) is
                // the third, and the sharpest of the three on exactly that point: with it clear a gap
                // typo carries the caret into the next word and a mid-word space is refused, with it
                // set the caret parks and the space lands, so the two arms disagree about where the
                // caret is from the first misplaced space onwards. Applied here rather than at each of
                // the three call sites for the same reason the others are: this is the one place a
                // recorded frame reaches an engine. CharTimedStretch (backlog 209) is the fourth
                // era bit, and it narrows SyllableTiming rather than competing with it: with it
                // clear a mashed freestyle section or identical-character run keeps the delta of
                // zero its whole syllable span paid it, which is what those runs were scored on.
                engine.AllowWrongInput = frame.AllowWrongInput;
                engine.SpaceSkipsWord = frame.SpaceSkipsWord;
                engine.SyllableTiming = frame.SyllableTiming;
                engine.WrongInputOnWordGaps = frame.WrongInputOnWordGaps;
                engine.StrictSpaces = frame.StrictSpaces;
                engine.CharTimedStretch = frame.CharTimedStretch;

                // FirstCharTiming (backlog 247) is the second narrowing on SyllableTiming, and an
                // era bit for the same reason bit 6 is: with it clear a syllable's first character
                // pressed late in the sung span keeps the delta of zero its whole span paid it,
                // which is what every run stored before the hybrid was scored on.
                engine.FirstCharTiming = frame.FirstCharTiming;

                // BoundedRush (backlog 218) is the fifth, and the sharpest of them on the caret
                // question: with it clear a player's finished line handed them the next one however
                // many seconds early, and the keystrokes they then typed into it LANDED. Re-derived
                // under the bound those same keystrokes would be refused (a complete line takes no
                // input), so an old run would lose whole lines' worth of judgements. Clobbered from
                // the frame like every bit here, and inert whenever the caret is pinned, since
                // FletcherEnabled gates every roll it bounds.
                engine.BoundedRush = frame.BoundedRush;

                // FlexibleLines (backlog 208) is the one bit here that needs a word more than an
                // assignment, because the axis it selects has TWO sources. The bit says
                // "this run was played with the caret unpinned AND snapped forward at each line
                // start", which is the live default since 208. Everything older carries it clear,
                // and clear means two different things depending on the score's mods: a plain
                // pre-208 run was played PINNED, and a run carrying the retired "FT" mod was played
                // unpinned but with no snap, because the snap did not exist yet. So the bit decides
                // the snap outright and is OR-ed with the mod-derived half for the caret itself
                // (see TypingEngine.FlexibleCaretFromMod, set by the two engine factories). Clobber
                // rather than OR on the snap: an FT replay whose caret this leaves unpinned must
                // still re-derive without the snap, or it drags its player onto lines they were
                // still parked behind.
                engine.FlexibleLineSnap = frame.FlexibleLines;
                engine.FletcherEnabled = frame.FlexibleLines || engine.FlexibleCaretFromMod;

                // WallClockFrames (backlog 256, bit 9) is deliberately NOT applied to anything here,
                // and it is the only bit on the header that is not. Every bit above selects a rule
                // the engine judges under; that one says what the numbers on the frames MEAN, which
                // has to be settled before a frame reaches an engine at all. Its single consumer is
                // Scoring.PuppeteerReplayTransform, which both the headless scorer and the watch
                // path run up front; by the time a frame arrives here it is always on the track
                // axis, and a derived stream carries the bit clear to say so.
                return;
            }

            engine.Update(frame.Time, clockRate);

            // The final arm is guarded rather than open, and that guard is the whole point: a frame
            // kind some LATER client records and this one has never heard of would otherwise fall
            // into ProcessKey, match no cell and be judged as a WRONG KEY (a typo and a combo break
            // under the live model, a rejection fed to the mash-fail streak under Gatekeeper).
            // Ignoring it costs the run whatever that input did, which is honest for something this
            // build cannot perform; misjudging it desynchronises every keystroke after it. Nothing
            // below the space can be a real keystroke, the typeable surface being printable ASCII.
            if (frame.IsBackspace)
                engine.ProcessBackspace();
            else if (frame.IsEnter)
                engine.ProcessEnter(frame.Time);
            else if (frame.Character >= first_typeable_code_point)
                engine.ProcessKey(frame.Character, frame.Time);
        }

        /// <summary>
        /// The lowest code point a real keystroke can carry: the space, the first character of the
        /// typeable surface (see <see cref="TypeBeatReplayFrame"/>, whose whole surface is printable
        /// ASCII). Everything below it is a sentinel, known or not yet known.
        /// </summary>
        private const char first_typeable_code_point = ' ';

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
