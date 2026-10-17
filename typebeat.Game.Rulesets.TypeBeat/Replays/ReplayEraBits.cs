// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.Replays
{
    /// <summary>Which header frame's flags word an era bit rides on.</summary>
    public enum EraWord
    {
        /// <summary>The first word, on the <see cref="TypeBeatReplayFrame.CONFIG"/> frame.</summary>
        Config,

        /// <summary>The second word, on the <see cref="TypeBeatReplayFrame.CONFIG_EXTENDED"/> frame (backlog 347).</summary>
        ConfigExtended,
    }

    /// <summary>
    /// One era bit: where it rides (<see cref="Word"/>, <see cref="Mask"/>), the frame field that
    /// holds it, and the engine flag it is recorded from and applied to. See
    /// <see cref="ReplayEraBits"/>.
    /// </summary>
    public sealed class ReplayEraBit
    {
        public EraWord Word { get; }

        public int Mask { get; }

        /// <summary>The <see cref="TypeBeatReplayFrame"/> field this bit is decoded into.</summary>
        public string Name { get; }

        private readonly Func<TypeBeatReplayFrame, bool> get;
        private readonly Action<TypeBeatReplayFrame, bool> set;
        private readonly Func<TypingEngine, bool>? read;
        private readonly Action<TypingEngine, bool>? write;

        public ReplayEraBit(EraWord word, int mask, string name, Func<TypeBeatReplayFrame, bool> get, Action<TypeBeatReplayFrame, bool> set,
                            Func<TypingEngine, bool>? read, Action<TypingEngine, bool>? write)
        {
            Word = word;
            Mask = mask;
            Name = name;
            this.get = get;
            this.set = set;
            this.read = read;
            this.write = write;
        }

        /// <summary>
        /// Whether the bit is an ENGINE flag the recorder reads and playback writes. False only for
        /// <see cref="TypeBeatReplayFrame.WallClockFrames"/>, which describes the frames' axis and
        /// is resolved before any frame reaches an engine.
        /// </summary>
        public bool IsEngineFlag => read != null;

        public bool Get(TypeBeatReplayFrame frame) => get(frame);

        public void Set(TypeBeatReplayFrame frame, bool value) => set(frame, value);

        /// <summary>The value the recorder stamps for this bit off a live engine.</summary>
        public bool Read(TypingEngine engine) => read?.Invoke(engine) ?? throw new InvalidOperationException($"{Name} is not an engine flag.");

        /// <summary>What playback does with this bit's value (<see cref="ReplayEngineFeed.Apply"/>).</summary>
        public void Write(TypingEngine engine, bool value)
        {
            if (write == null)
                throw new InvalidOperationException($"{Name} is not an engine flag.");

            write(engine, value);
        }
    }

    /// <summary>
    /// THE ERA BIT TABLE: every bit either CONFIG flags word carries, in one list, read by every site
    /// that moves an era between an engine, a frame and the .osr. Encoding and decoding
    /// (<see cref="TypeBeatReplayFrame.ToLegacy"/> / <see cref="TypeBeatReplayFrame.FromLegacy"/>),
    /// the recorder's header frames (<see cref="TypeBeatReplayRecorder.HeaderFramesFor"/>), playback
    /// (<see cref="ReplayEngineFeed.Apply"/> and <see cref="ReplayEngineFeed.ClearExtendedEras"/>),
    /// the Puppeteer transform's frame clone and the watch path's capture and restore of the live
    /// second-word eras all walk this table, so a new era is ONE entry here (plus its frame field,
    /// its factory parameter and its engine flag) and cannot be wired into some of them and not
    /// others. <c>ReplayEraBitsTest</c> walks the frame's fields and the engine's flags by
    /// reflection and fails on one that has no entry.
    ///
    /// <para>The bit positions are the wire format and are never renumbered: each word holds bits 0
    /// to 16 (the .osr decoder's coordinate ceiling, see <see cref="TypeBeatReplayFrame"/>), and a
    /// new bit is appended above the last one of its word. <c>ReplayEraBitsGoldenTest</c> pins the
    /// bytes.</para>
    /// </summary>
    public static class ReplayEraBits
    {
        public static readonly IReadOnlyList<ReplayEraBit> ALL = new[]
        {
            // Bit 0: wrong input allowed (fixed by every replay on disk).
            bit(EraWord.Config, 1 << 0, nameof(TypeBeatReplayFrame.AllowWrongInput), f => f.AllowWrongInput, (f, v) => f.AllowWrongInput = v,
                e => e.AllowWrongInput, (e, v) => e.AllowWrongInput = v),

            // Bit 1: space skips the current word.
            bit(EraWord.Config, 1 << 1, nameof(TypeBeatReplayFrame.SpaceSkipsWord), f => f.SpaceSkipsWord, (f, v) => f.SpaceSkipsWord = v,
                e => e.SpaceSkipsWord, (e, v) => e.SpaceSkipsWord = v),

            // Bit 2: presses are judged against syllable spans.
            bit(EraWord.Config, 1 << 2, nameof(TypeBeatReplayFrame.SyllableTiming), f => f.SyllableTiming, (f, v) => f.SyllableTiming = v,
                e => e.SyllableTiming, (e, v) => e.SyllableTiming = v),

            // Bit 3: a wrong letter on a word gap is typed through.
            bit(EraWord.Config, 1 << 3, nameof(TypeBeatReplayFrame.WrongInputOnWordGaps), f => f.WrongInputOnWordGaps, (f, v) => f.WrongInputOnWordGaps = v,
                e => e.WrongInputOnWordGaps, (e, v) => e.WrongInputOnWordGaps = v),

            // Bit 4: the spacebar is the word boundary (a gap typo parks the caret, a mid-word space
            // is a typo rather than a rejection).
            bit(EraWord.Config, 1 << 4, nameof(TypeBeatReplayFrame.StrictSpaces), f => f.StrictSpaces, (f, v) => f.StrictSpaces = v,
                e => e.StrictSpaces, (e, v) => e.StrictSpaces = v),

            // Bit 5: the caret was unpinned from the playhead and snapped forward when the next line
            // started (backlog 208's flexible-lines default). The one bit whose engine side needs more
            // than an assignment, because the axis it selects has TWO sources. The bit says "this run
            // was played with the caret unpinned AND snapped forward at each line start". Everything
            // older carries it clear, and clear means two different things depending on the score's
            // mods: a plain pre-208 run was played PINNED, and a run carrying the retired "FT" mod was
            // played unpinned but with no snap, because the snap did not exist yet. So the bit decides
            // the snap outright and is OR-ed with the mod-derived half for the caret itself (see
            // TypingEngine.FlexibleCaretFromMod, set by that mod's ApplyToEngine). Clobber rather than
            // OR on the snap: an FT replay whose caret this leaves unpinned must still re-derive
            // without the snap, or it drags its player onto lines they were still parked behind. The
            // recorder stamps the snap, which is the half the bit owns.
            bit(EraWord.Config, 1 << 5, nameof(TypeBeatReplayFrame.FlexibleLines), f => f.FlexibleLines, (f, v) => f.FlexibleLines = v,
                e => e.FlexibleLineSnap, (e, v) =>
                {
                    e.FlexibleLineSnap = v;
                    e.FletcherEnabled = v || e.FlexibleCaretFromMod;
                }),

            // Bit 6: a freestyle slot or a stretched identical-character run is judged on its own
            // character target rather than its syllable's span.
            bit(EraWord.Config, 1 << 6, nameof(TypeBeatReplayFrame.CharTimedStretch), f => f.CharTimedStretch, (f, v) => f.CharTimedStretch = v,
                e => e.CharTimedStretch, (e, v) => e.CharTimedStretch = v),

            // Bit 7: rushing onto the next line was bounded to the same margin dragging is granted at
            // the other end of a line (backlog 218).
            bit(EraWord.Config, 1 << 7, nameof(TypeBeatReplayFrame.BoundedRush), f => f.BoundedRush, (f, v) => f.BoundedRush = v,
                e => e.BoundedRush, (e, v) => e.BoundedRush = v),

            // Bit 8: the first character of a syllable is judged on its distance from the syllable's
            // start rather than paid 0 anywhere in the sung span (backlog 247).
            bit(EraWord.Config, 1 << 8, nameof(TypeBeatReplayFrame.FirstCharTiming), f => f.FirstCharTiming, (f, v) => f.FirstCharTiming = v,
                e => e.FirstCharTiming, (e, v) => e.FirstCharTiming = v),

            // Bit 9: every frame's time in this run is a WALL stamp on the axis anchored at the CONFIG
            // frame's own time, not a lyric time (backlog 256, the Puppeteer era). The only bit here
            // that describes the FRAMES rather than the engine, so it has no engine side: the recorder
            // stamps it from its own axis decision, and its one consumer is PuppeteerReplayTransform.
            bit(EraWord.Config, 1 << 9, nameof(TypeBeatReplayFrame.WallClockFrames), f => f.WallClockFrames, (f, v) => f.WallClockFrames = v,
                null, null),

            // Bit 10: a sealed line's combo break was back-dated to the cells it missed, so a run
            // built past them survived it (backlog 259).
            bit(EraWord.Config, 1 << 10, nameof(TypeBeatReplayFrame.BackDatedSealBreak), f => f.BackDatedSealBreak, (f, v) => f.BackDatedSealBreak = v,
                e => e.BackDatedSealBreak, (e, v) => e.BackDatedSealBreak = v),

            // Bit 11: a word given up by accident and then typed out in full cost the run nothing
            // (backlog 260).
            bit(EraWord.Config, 1 << 11, nameof(TypeBeatReplayFrame.LosslessSkipReclaim), f => f.LosslessSkipReclaim, (f, v) => f.LosslessSkipReclaim = v,
                e => e.LosslessSkipReclaim, (e, v) => e.LosslessSkipReclaim = v),

            // Bit 12: a break taking the claim off an older break folded that claim into its own
            // rather than discarding it, so two accidents corrected in full cost the run nothing
            // (backlog 262).
            bit(EraWord.Config, 1 << 12, nameof(TypeBeatReplayFrame.FoldsDisplacedClaim), f => f.FoldsDisplacedClaim, (f, v) => f.FoldsDisplacedClaim = v,
                e => e.FoldsDisplacedClaim, (e, v) => e.FoldsDisplacedClaim = v),

            // Bit 13: this run's Hard Rock left the judgement windows at their normal width instead of
            // halving them (backlog 264).
            bit(EraWord.Config, 1 << 13, nameof(TypeBeatReplayFrame.UnhalvedHardRockWindows), f => f.UnhalvedHardRockWindows, (f, v) => f.UnhalvedHardRockWindows = v,
                e => e.UnhalvedHardRockWindows, (e, v) => e.UnhalvedHardRockWindows = v),

            // Bit 14: a finished line was handed over by the player's own space or Enter rather than
            // automatically (the manual-newlines setting).
            bit(EraWord.Config, 1 << 14, nameof(TypeBeatReplayFrame.ManualNewlines), f => f.ManualNewlines, (f, v) => f.ManualNewlines = v,
                e => e.ManualNewlines, (e, v) => e.ManualNewlines = v),

            // Bit 15: a finished line may also be handed over by typing the next line's first slot.
            bit(EraWord.Config, 1 << 15, nameof(TypeBeatReplayFrame.NewlineOnTypedLetter), f => f.NewlineOnTypedLetter, (f, v) => f.NewlineOnTypedLetter = v,
                e => e.NewlineOnTypedLetter, (e, v) => e.NewlineOnTypedLetter = v),

            // Bit 16: a press shortly before the map's first vocal opened the first line. The highest
            // bit the .osr carrier can hold (see the TypeBeatReplayFrame summary).
            bit(EraWord.Config, 1 << 16, nameof(TypeBeatReplayFrame.FirstLineLeadIn), f => f.FirstLineLeadIn, (f, v) => f.FirstLineLeadIn = v,
                e => e.FirstLineLeadIn, (e, v) => e.FirstLineLeadIn = v),

            // Second word, bit 0: an over-cap press was awarded Meh and credited combo, with the cap at
            // six, rather than breaking the combo at five (backlog 347).
            bit(EraWord.ConfigExtended, 1 << 0, nameof(TypeBeatReplayFrame.RushCapCostsAccuracy), f => f.RushCapCostsAccuracy, (f, v) => f.RushCapCostsAccuracy = v,
                e => e.RushCapCostsAccuracy, (e, v) => e.RushCapCostsAccuracy = v),

            // Second word, bit 1: the run was played in the second input era.
            bit(EraWord.ConfigExtended, 1 << 1, nameof(TypeBeatReplayFrame.InputEra2), f => f.InputEra2, (f, v) => f.InputEra2 = v,
                e => e.InputEra2, (e, v) => e.InputEra2 = v),

            // Second word, bit 2: only authored syllable boundaries split a word (backlog 363).
            bit(EraWord.ConfigExtended, 1 << 2, nameof(TypeBeatReplayFrame.AuthoredSyllablesOnly), f => f.AuthoredSyllablesOnly, (f, v) => f.AuthoredSyllablesOnly = v,
                e => e.AuthoredSyllablesOnly, (e, v) => e.AuthoredSyllablesOnly = v),

            // Second word, bit 3: per-character timing follows the editor's effective subdivision cuts.
            bit(EraWord.ConfigExtended, 1 << 3, nameof(TypeBeatReplayFrame.AlignSubdivisionTargets), f => f.AlignSubdivisionTargets, (f, v) => f.AlignSubdivisionTargets = v,
                e => e.AlignSubdivisionTargets, (e, v) => e.AlignSubdivisionTargets = v),

            // Second word, bit 4: the final line seals as soon as it is fully typed.
            bit(EraWord.ConfigExtended, 1 << 4, nameof(TypeBeatReplayFrame.EarlyFinish), f => f.EarlyFinish, (f, v) => f.EarlyFinish = v,
                e => e.EarlyFinish, (e, v) => e.EarlyFinish = v),

            // Second word, bit 5: a Japanese Polyglot run typed romaji through the internal processor,
            // so its frames carry raw romaji rather than committed text.
            bit(EraWord.ConfigExtended, 1 << 5, nameof(TypeBeatReplayFrame.JapaneseRomajiInput), f => f.JapaneseRomajiInput, (f, v) => f.JapaneseRomajiInput = v,
                e => e.JapaneseRomajiInput, (e, v) => e.JapaneseRomajiInput = v),

            // Second word, bit 6: Japanese Polyglot words were timed as whole words, ignoring
            // romanised subdivisions.
            bit(EraWord.ConfigExtended, 1 << 6, nameof(TypeBeatReplayFrame.JapaneseWordTiming), f => f.JapaneseWordTiming, (f, v) => f.JapaneseWordTiming = v,
                e => e.JapaneseWordTiming, (e, v) => e.JapaneseWordTiming = v),

            // Second word, bit 7: the second Japanese input era, where Space skips one unfinished
            // character and WPM counts actual inputs.
            bit(EraWord.ConfigExtended, 1 << 7, nameof(TypeBeatReplayFrame.JapaneseInputEra2), f => f.JapaneseInputEra2, (f, v) => f.JapaneseInputEra2 = v,
                e => e.JapaneseInputEra2, (e, v) => e.JapaneseInputEra2 = v),

            // Second word, bit 8: the third Japanese input era, where Space skips the remainder of the
            // authored word on the word-aware romaji input plan.
            bit(EraWord.ConfigExtended, 1 << 8, nameof(TypeBeatReplayFrame.JapaneseInputEra3), f => f.JapaneseInputEra3, (f, v) => f.JapaneseInputEra3 = v,
                e => e.JapaneseInputEra3, (e, v) => e.JapaneseInputEra3 = v),

            // Second word, bit 9: typed characters were stamped at their key's time. No rule in the
            // engine reads it: like the first word's bit 9 it says when the frames were stamped, and
            // the engine carries it only so the live key handler (and a watch path handing a play
            // back) can.
            bit(EraWord.ConfigExtended, 1 << 9, nameof(TypeBeatReplayFrame.KeyTimeJudgement), f => f.KeyTimeJudgement, (f, v) => f.KeyTimeJudgement = v,
                e => e.KeyTimeJudgement, (e, v) => e.KeyTimeJudgement = v),

            bit(EraWord.ConfigExtended, 1 << 10, nameof(TypeBeatReplayFrame.JapaneseCharacterTiming), f => f.JapaneseCharacterTiming, (f, v) => f.JapaneseCharacterTiming = v,
                e => e.JapaneseCharacterTiming, (e, v) => e.JapaneseCharacterTiming = v),

            bit(EraWord.ConfigExtended, 1 << 11, nameof(TypeBeatReplayFrame.JapaneseInputScoring), f => f.JapaneseInputScoring, (f, v) => f.JapaneseInputScoring = v,
                e => e.JapaneseInputScoring, (e, v) => e.JapaneseInputScoring = v),

            bit(EraWord.ConfigExtended, 1 << 12, nameof(TypeBeatReplayFrame.JapaneseKanaInput), f => f.JapaneseKanaInput, (f, v) => f.JapaneseKanaInput = v,
                e => e.JapaneseKanaInput, (e, v) => e.JapaneseKanaInput = v),

            bit(EraWord.ConfigExtended, 1 << 13, nameof(TypeBeatReplayFrame.BestRetypeJudgement), f => f.BestRetypeJudgement, (f, v) => f.BestRetypeJudgement = v,
                e => e.BestRetypeJudgement, (e, v) => e.BestRetypeJudgement = v),
        };

        private static readonly ReplayEraBit[] config_bits = ALL.Where(b => b.Word == EraWord.Config).ToArray();

        private static readonly ReplayEraBit[] extended_bits = ALL.Where(b => b.Word == EraWord.ConfigExtended).ToArray();

        /// <summary>The bits of one word, in bit order.</summary>
        public static IReadOnlyList<ReplayEraBit> Of(EraWord word) => word == EraWord.Config ? config_bits : extended_bits;

        /// <summary>The flags word <paramref name="frame"/>'s fields encode to.</summary>
        public static int Encode(TypeBeatReplayFrame frame, EraWord word)
        {
            int flags = 0;

            foreach (var bit in Of(word))
            {
                if (bit.Get(frame))
                    flags |= bit.Mask;
            }

            return flags;
        }

        /// <summary>Set every field of <paramref name="word"/> on <paramref name="frame"/> from <paramref name="flags"/>.</summary>
        public static void Decode(TypeBeatReplayFrame frame, EraWord word, int flags)
        {
            foreach (var bit in Of(word))
                bit.Set(frame, (flags & bit.Mask) != 0);
        }

        /// <summary>Carry every era field, both words, from <paramref name="source"/> to <paramref name="target"/>.</summary>
        public static void Copy(TypeBeatReplayFrame source, TypeBeatReplayFrame target)
        {
            foreach (var bit in ALL)
                bit.Set(target, bit.Get(source));
        }

        /// <summary>
        /// Stamp every engine flag of <paramref name="word"/> onto <paramref name="frame"/>, the
        /// recorder's half. <see cref="TypeBeatReplayFrame.WallClockFrames"/> is not an engine flag
        /// and is left for the caller.
        /// </summary>
        public static void Record(TypingEngine engine, TypeBeatReplayFrame frame, EraWord word)
        {
            foreach (var bit in Of(word))
            {
                if (bit.IsEngineFlag)
                    bit.Set(frame, bit.Read(engine));
            }
        }

        /// <summary>Apply every engine flag of <paramref name="word"/> from <paramref name="frame"/>, playback's half.</summary>
        public static void Apply(TypingEngine engine, TypeBeatReplayFrame frame, EraWord word)
        {
            foreach (var bit in Of(word))
            {
                if (bit.IsEngineFlag)
                    bit.Write(engine, bit.Get(frame));
            }
        }

        /// <summary>Write every engine flag of <paramref name="word"/> to <paramref name="value"/>.</summary>
        public static void WriteAll(TypingEngine engine, EraWord word, bool value)
        {
            foreach (var bit in Of(word))
            {
                if (bit.IsEngineFlag)
                    bit.Write(engine, value);
            }
        }

        /// <summary>The engine's current values for <paramref name="word"/>, in <see cref="Of"/> order, for <see cref="Restore"/>.</summary>
        public static bool[] Capture(TypingEngine engine, EraWord word)
            => Of(word).Select(bit => bit.IsEngineFlag && bit.Read(engine)).ToArray();

        /// <summary>Put back values taken by <see cref="Capture"/>.</summary>
        public static void Restore(TypingEngine engine, EraWord word, bool[] values)
        {
            var bits = Of(word);

            for (int i = 0; i < bits.Count; i++)
            {
                if (bits[i].IsEngineFlag)
                    bits[i].Write(engine, values[i]);
            }
        }

        private static ReplayEraBit bit(EraWord word, int mask, string name, Func<TypeBeatReplayFrame, bool> get, Action<TypeBeatReplayFrame, bool> set,
                                        Func<TypingEngine, bool>? read, Action<TypingEngine, bool>? write)
            => new ReplayEraBit(word, mask, name, get, set, read, write);
    }
}
