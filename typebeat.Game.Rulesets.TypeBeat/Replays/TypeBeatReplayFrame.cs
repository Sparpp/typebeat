// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Beatmaps;
using typebeat.Game.Replays.Legacy;
using typebeat.Game.Rulesets.Replays;
using typebeat.Game.Rulesets.Replays.Types;

namespace typebeat.Game.Rulesets.TypeBeat.Replays
{
    /// <summary>
    /// One discrete typing input event. Typing has no positional state, so unlike the circle games a
    /// type!beat replay is not a sampled stream: it is exactly the sequence of engine mutations, one
    /// frame per accepted call into <see cref="Gameplay.TypingEngine"/>.
    ///
    /// <para><b>Frame format (the recalculation contract).</b> A frame is (Time, Character):</para>
    /// <list type="bullet">
    /// <item><see cref="ReplayFrame.Time"/> is the ENGINE (lyric-clock) time in milliseconds, already
    /// rounded to an integer at capture so the legacy .osr encoding (integral frame deltas) is
    /// lossless. It is the exact time value passed to <c>TypingEngine.Update</c>/<c>ProcessKey</c>,
    /// so judgement deltas recompute bit-identically.</item>
    /// <item><see cref="Character"/> is the exact character fed to the engine, AFTER keyboard-layout
    /// remapping and Shift application (so it carries the case the Literate mod judges on, and is
    /// independent of the player's physical layout). Three sentinels reuse ASCII control codes:
    /// <see cref="BACKSPACE"/> (0x08) is a backspace erase, <see cref="ENTER"/> (0x0A) is a line
    /// skip (backlog 241), and <see cref="CONFIG"/> (0x00) is a
    /// settings header frame carrying the judgement-relevant settings as BITS: bit 0
    /// <see cref="AllowWrongInput"/> (the wrong-key model the run was judged under), bit 1
    /// <see cref="SpaceSkipsWord"/> (whether a space pressed inside a word abandoned it), bit 2
    /// <see cref="SyllableTiming"/> (whether a press was graded against its syllable's sung span or
    /// against its cell's point target), bit 3 <see cref="WrongInputOnWordGaps"/> (whether a
    /// wrong letter on a word gap was typed through or rejected), bit 4
    /// <see cref="StrictSpaces"/> (whether the spacebar was the word boundary the player owed at
    /// every gap), bit 5 <see cref="FlexibleLines"/> (whether the caret was unpinned from the
    /// playhead AND snapped forward when the next line started), bit 6
    /// <see cref="CharTimedStretch"/> (whether a freestyle slot or a stretched identical-character
    /// run was judged on its own character target rather than its syllable's span), bit 7
    /// <see cref="BoundedRush"/> (whether rushing onto the next line was bounded to the same
    /// 1500 ms before its cue that dragging is granted past a line's end) and bit 8
    /// <see cref="FirstCharTiming"/> (whether the first character of a syllable was judged on its
    /// distance from the syllable's start rather than paid 0 anywhere in the span) and bit 9
    /// <see cref="WallClockFrames"/> (whether every frame's <see cref="ReplayFrame.Time"/> in this
    /// run is a WALL-CLOCK stamp rather than a lyric time, which is the Puppeteer era, backlog 256).
    /// Other mods
    /// (Literate/Mashing/rate) travel in the score itself and need no frames.
    ///
    /// <para>Backlog 107 turned that model from a local SETTING into a mod (Gatekeeper), so it now
    /// travels in the score's mods too, and the header frame is kept anyway, for two reasons. It is
    /// what makes a replay self-describing, so playback reproduces the run without depending on the
    /// mod list surviving; and, more concretely, every replay recorded BEFORE the flip carries
    /// bit 0 = 0 for a strict run that has no Gatekeeper mod on it, so the header is the only thing
    /// that still judges those runs the way they were played. The BIT therefore keeps its original
    /// meaning exactly (1 = wrong input allowed), which is also why the engine property was not
    /// renamed to match the mod.</para></item>
    /// </list>
    ///
    /// <para><b>Legacy (.osr) mapping</b>, chosen to round-trip through
    /// <see cref="typebeat.Game.Scoring.Legacy.LegacyScoreEncoder"/>/<c>Decoder</c> untouched:
    /// MouseX = character code, MouseY = config flags (bit 0 = allow-wrong-input, bit 1 =
    /// space-skips-word, bit 2 = syllable-span timing, bit 3 = wrong-input-on-word-gaps, bit 4 =
    /// strict-spaces, bit 5 = flexible-lines, bit 6 = char-timed-stretch, bit 7 = bounded-rush,
    /// bit 8 = first-char-timing, bit 9 = wall-clock-frames; only
    /// meaningful on CONFIG frames),
    /// ButtonState = None, time = the integral frame time. A flags word of at most 1023 is as harmless
    /// to the encoder as the single bit was, and each new bit is appended ABOVE the existing ones,
    /// never renumbered: bits 0 to 4 keep their meaning and their positions untouched, so every
    /// replay already on disk decodes identically and simply reads false for the newer bits. All
    /// typeable characters (a-z, A-Z, 0-9, space, plus the Literate mod's punctuation, whose
    /// highest code point is ']' at 0x5D) and all three sentinels are far below the decoder's
    /// coordinate parse limits and its (256, -500) stable-header positions, so no stable fixup can
    /// mangle them. Bits 8 and 9 push the flags word itself to 256 and then past 512, and the safety
    /// argument does not depend on the word's size at all: the stable-header strip matches the
    /// POSITION PAIR (256, -500) exactly, and a CONFIG frame's MouseX is 0x00 with a MouseY that is
    /// never negative, so neither coordinate can match whatever the flags word grows to. The
    /// sentinels sit at 0x00, 0x08 and 0x0A, below every printable mark, so nothing
    /// collides.</para>
    ///
    /// <para><b>The WALL-CLOCK axis (bit 9, backlog 256).</b> Ordinarily a frame's time is a lyric
    /// time and can be fed to the engine as it stands. Under the Puppeteer mod the song's position
    /// is a FUNCTION of the typing, so the lyric time of a keystroke is an OUTPUT of the model
    /// rather than an input to it, and storing only that would leave the run unable to reproduce the
    /// tape it was played on. Such a run therefore stores the one axis that is an input: WALL time.
    /// Bit 9 says so, and under it every frame's time (the CONFIG frame's included) is
    /// <c>anchor + model ticks</c>, where the ANCHOR is the CONFIG frame's own time and is the track
    /// position the tape started at. <c>PuppeteerReplayTransform</c> turns such a stream back into
    /// ordinary track-time frames by re-running the model, and is the ONE consumer of this bit:
    /// unlike every bit below it this is a FRAME-AXIS marker and not an engine flag, so
    /// <c>ReplayEngineFeed.Apply</c> deliberately does not apply it to anything.</para>
    ///
    /// <para><b>A sentinel this client does not know</b> is IGNORED rather than typed
    /// (<c>ReplayEngineFeed.Apply</c>): any character below 0x20 that is not one of the three above
    /// resolves no cell and mutates nothing, so a frame kind added later degrades to a missing input
    /// on an older client instead of a wrong-key judgement that desynchronises every keystroke after
    /// it. <see cref="ENTER"/> itself needs no era bit for the opposite reason: a replay recorded
    /// before backlog 241 contains no such frame at all, and the skip it encodes changes no judged
    /// value or time (see <c>TypingEngine.ProcessEnter</c>), only which line the caret is on.</para>
    ///
    /// <para>Only EFFECTIVE inputs are recorded (calls where the engine mutated state), which is what
    /// makes playback deterministic: replaying performs, per frame, <c>Update(Time)</c> then the
    /// keystroke, the same call sequence live play makes.</para>
    /// </summary>
    public class TypeBeatReplayFrame : ReplayFrame, IConvertibleReplayFrame
    {
        /// <summary>Sentinel character for a backspace erase (ASCII BS).</summary>
        public const char BACKSPACE = '\b';

        /// <summary>
        /// Sentinel character for a LINE SKIP (ASCII LF), backlog 241: the player gave up the rest of
        /// the line and parked the caret past its end. Played back through
        /// <c>TypingEngine.ProcessEnter</c>, which takes the frame's own time (the skip's landing
        /// place depends on it, through the rush bound), unlike <see cref="BACKSPACE"/>.
        /// </summary>
        public const char ENTER = '\n';

        /// <summary>Sentinel character for the settings header frame (ASCII NUL).</summary>
        public const char CONFIG = '\0';

        /// <summary>
        /// The character fed to the engine (layout-remapped, Shift-cased), or a sentinel
        /// (<see cref="BACKSPACE"/>/<see cref="ENTER"/>/<see cref="CONFIG"/>). Never a sentinel value for real typing:
        /// the typeable surface is a-z/A-Z/0-9/space, widened under the Literate mod by the
        /// supported punctuation marks, all of them printable ASCII.
        /// </summary>
        public char Character;

        /// <summary>
        /// The engine's allow-wrong-input setting at record time. Only meaningful on
        /// <see cref="CONFIG"/> frames; playback applies it to the engine so a replay judges
        /// identically regardless of the watching machine's local setting.
        /// </summary>
        public bool AllowWrongInput;

        /// <summary>
        /// The engine's space-to-skip-a-word setting at record time (see
        /// <see cref="Gameplay.TypingEngine.SpaceSkipsWord"/>). Only meaningful on
        /// <see cref="CONFIG"/> frames. Judgement-relevant, hence recorded: the same recorded space
        /// keystroke abandons a word with it on and is rejected with it off, so a replay watched on a
        /// machine with the opposite setting would otherwise diverge from the run it stores.
        /// </summary>
        public bool SpaceSkipsWord;

        /// <summary>
        /// The engine's syllable-span judgement setting at record time (see
        /// <see cref="Gameplay.TypingEngine.SyllableTiming"/>). Only meaningful on
        /// <see cref="CONFIG"/> frames, and the ERA carrier for backlog 179: the live client records
        /// it true, and every replay stored before it existed carries the bit clear and so
        /// re-derives under the classic point-target rule it was actually judged on. Judgement
        /// relevant in the strongest sense, since it decides the delta of every press on a grouped
        /// cell.
        /// </summary>
        public bool SyllableTiming;

        /// <summary>
        /// The engine's wrong-letter-on-a-word-gap setting at record time (see
        /// <see cref="Gameplay.TypingEngine.WrongInputOnWordGaps"/>). Only meaningful on
        /// <see cref="CONFIG"/> frames, and the ERA carrier for backlog 181: the live client records
        /// it true, and every replay stored before it existed carries the bit clear, so a wrong key
        /// its player pressed on a word gap is REJECTED on re-derivation exactly as it was when the
        /// run was played. Judgement relevant in the strongest sense: the two arms disagree about
        /// whether the caret moved at all, so a single such frame decoded under the wrong one
        /// desynchronises every keystroke after it.
        /// </summary>
        public bool WrongInputOnWordGaps;

        /// <summary>
        /// The engine's monkeytype space-discipline setting at record time (see
        /// <see cref="Gameplay.TypingEngine.StrictSpaces"/>). Only meaningful on
        /// <see cref="CONFIG"/> frames, and the ERA carrier for backlog 184: the live client records
        /// it true, and every replay stored before it existed carries the bit clear, so a gap typo
        /// its player made still carries the caret forward and a mid-word space its player pressed is
        /// still rejected, exactly as they were when the run was played. Judgement relevant in the
        /// strongest sense, on both halves: each of them decides where the caret ends up after an
        /// already-recorded keystroke, so one frame decoded under the wrong arm desynchronises every
        /// keystroke after it.
        /// </summary>
        public bool StrictSpaces;

        /// <summary>
        /// The engine's flexible-lines setting at record time (see
        /// <see cref="Gameplay.TypingEngine.FlexibleLineSnap"/>). Only meaningful on
        /// <see cref="CONFIG"/> frames, and the ERA carrier for backlog 208, which made the unpinned
        /// caret the DEFAULT and added the one behaviour the old "FT" mod never had: a caret sitting
        /// past the end of its line is snapped onto the next line when that line starts. The live
        /// client records it set for every stack but the pinning mod's, and every replay stored
        /// before it existed carries the bit clear, which is exactly right for both kinds of older
        /// run: a plain one re-derives PINNED (the caret it was played with), and an "FT" one
        /// re-derives unpinned but WITHOUT the snap, because the mod is on its score and
        /// <see cref="Gameplay.TypingEngine.FlexibleCaretFromMod"/> carries that half.
        ///
        /// <para>Judgement relevant in the strongest sense: each arm decides which line the caret is
        /// on at a given time, so one frame decoded under the wrong one lands every keystroke after
        /// it on different cells.</para>
        /// </summary>
        public bool FlexibleLines;

        /// <summary>
        /// The engine's stretch-timing setting at record time (see
        /// <see cref="Gameplay.TypingEngine.CharTimedStretch"/>). Only meaningful on
        /// <see cref="CONFIG"/> frames, and the ERA carrier for backlog 209: the live client records
        /// it true, and every replay stored before it existed carries the bit clear, so a freestyle
        /// section or an identical-character run its player mashed ahead of the vocal still grades a
        /// delta of zero across the whole syllable span, exactly as it did when the run was played.
        /// Judgement relevant in the strongest sense, since it decides the delta of every press on
        /// such a cell.
        /// </summary>
        public bool CharTimedStretch;

        /// <summary>
        /// The engine's rush-bound setting at record time (see
        /// <see cref="Gameplay.TypingEngine.BoundedRush"/>). Only meaningful on
        /// <see cref="CONFIG"/> frames, and the ERA carrier for backlog 218, which made rushing onto
        /// the next line symmetric with dragging behind on the current one: entry opens 1500 ms
        /// before that line's cue, the same 1500 ms drag borrows past a line's end. The live client
        /// records it true for every stack (inert under the pinning mod, which has no roll at all),
        /// and every replay stored before it existed carries the bit clear, so a run whose player
        /// typed a line out seconds before its vocals re-derives with the UNBOUNDED roll it was
        /// actually played with.
        ///
        /// <para>Judgement relevant in the strongest sense, exactly like <see cref="FlexibleLines"/>:
        /// it decides which line the caret is on at a given time, so one frame decoded under the
        /// wrong arm lands every keystroke after it on different cells (under the bound those
        /// keystrokes are refused outright, which moves nothing at all).</para>
        /// </summary>
        public bool BoundedRush;

        /// <summary>
        /// The engine's first-char-timing setting at record time (see
        /// <see cref="Gameplay.TypingEngine.FirstCharTiming"/>). Only meaningful on
        /// <see cref="CONFIG"/> frames, and the ERA carrier for backlog 247: the live client records
        /// it true, and every replay stored before it existed carries the bit clear, so a syllable
        /// whose first character its player pressed late in the sung span still grades that press a
        /// delta of zero, exactly as it did when the run was played. Judgement relevant in the
        /// strongest sense, since it decides the delta of every press that opens a syllable.
        /// </summary>
        public bool FirstCharTiming;

        /// <summary>
        /// Whether every frame in this run is stamped on the WALL clock rather than the lyric clock.
        /// Only meaningful on <see cref="CONFIG"/> frames, and the ERA carrier for backlog 256: the
        /// live client records it set only for a Puppeteer run, and every other replay, before and
        /// after, carries the bit clear, which means what it has always meant, that a frame's time
        /// IS the lyric time the engine was fed at.
        ///
        /// <para>Unlike every other bit here this is not an engine flag, and
        /// <c>ReplayEngineFeed.Apply</c> never applies it: it describes the AXIS the frames are on,
        /// which has to be resolved before a single frame reaches an engine. Its one consumer is
        /// <c>PuppeteerReplayTransform</c>, which both the headless scorer and the watch path run up
        /// front; downstream of it every frame is an ordinary track-time frame again, and the
        /// derived stream carries this bit CLEAR, so the transform is idempotent.</para>
        ///
        /// <para>Judgement relevant in the strongest sense, and in a way no earlier bit is: it does
        /// not select a rule the engine judges under, it decides what the numbers on the frames MEAN.
        /// Read on the wrong axis, a Puppeteer run's keystrokes are fed at wall times that have no
        /// relation to the song, so every judgement, every seal and the caret itself land somewhere
        /// else.</para>
        /// </summary>
        public bool WallClockFrames;

        /// <summary>
        /// The ANCHOR carried by a bit-9 CONFIG frame: the track position the tape was started at,
        /// which is also the origin of the wall axis every other frame in the run is stamped on. It
        /// is simply this frame's own <see cref="ReplayFrame.Time"/>, named here because that is a
        /// second meaning for one field and the transform reads it through this name.
        /// </summary>
        public double AnchorMs => Time;

        public bool IsBackspace => Character == BACKSPACE;

        public bool IsEnter => Character == ENTER;

        public bool IsConfig => Character == CONFIG;

        public TypeBeatReplayFrame()
        {
        }

        public TypeBeatReplayFrame(double time, char character)
            : base(time)
        {
            Character = character;
        }

        /// <summary>
        /// The header frame for a run. <paramref name="spaceSkipsWord"/>,
        /// <paramref name="syllableTiming"/>, <paramref name="wrongInputOnWordGaps"/>,
        /// <paramref name="strictSpaces"/>, <paramref name="charTimedStretch"/>,
        /// <paramref name="flexibleLines"/>, <paramref name="boundedRush"/> and
        /// <paramref name="firstCharTiming"/> are optional so the
        /// older call sites keep meaning what they always did (bit clear = no word skipping, classic
        /// point-target judgement, a wrong key on a word gap rejected, a gap typo carrying the caret
        /// forward, a mid-word space rejected, a mashed stretch paid across its whole syllable span,
        /// a caret pinned to the playhead, an unbounded rush onto the next line and a syllable's
        /// first character paid anywhere in its span), which is also
        /// exactly how a replay recorded before each setting existed decodes.
        ///
        /// <para>The PARAMETER order is append-only and therefore does NOT track bit order:
        /// <paramref name="charTimedStretch"/> (bit 6) shipped first and holds slot 7, so
        /// <paramref name="flexibleLines"/> (bit 5) is appended after it rather than renumbering a
        /// positional argument out from under a call site that already passes it,
        /// <paramref name="boundedRush"/> (bit 7) is appended after both,
        /// <paramref name="firstCharTiming"/> (bit 8) after that, and
        /// <paramref name="wallClockFrames"/> (bit 9) after that again. Pass the newer five by
        /// name.</para>
        /// </summary>
        public static TypeBeatReplayFrame CreateConfigFrame(double time, bool allowWrongInput, bool spaceSkipsWord = false, bool syllableTiming = false, bool wrongInputOnWordGaps = false, bool strictSpaces = false, bool charTimedStretch = false, bool flexibleLines = false, bool boundedRush = false, bool firstCharTiming = false, bool wallClockFrames = false) => new TypeBeatReplayFrame(time, CONFIG)
        {
            AllowWrongInput = allowWrongInput,
            SpaceSkipsWord = spaceSkipsWord,
            SyllableTiming = syllableTiming,
            WrongInputOnWordGaps = wrongInputOnWordGaps,
            StrictSpaces = strictSpaces,
            FlexibleLines = flexibleLines,
            CharTimedStretch = charTimedStretch,
            BoundedRush = boundedRush,
            FirstCharTiming = firstCharTiming,
            WallClockFrames = wallClockFrames,
        };

        /// <summary>Bit 0 of the CONFIG frame's flags word: wrong input allowed (fixed by every replay on disk).</summary>
        private const int flag_allow_wrong_input = 1;

        /// <summary>Bit 1 of the CONFIG frame's flags word: space skips the current word.</summary>
        private const int flag_space_skips_word = 2;

        /// <summary>Bit 2 of the CONFIG frame's flags word: presses are judged against syllable spans.</summary>
        private const int flag_syllable_timing = 4;

        /// <summary>Bit 3 of the CONFIG frame's flags word: a wrong letter on a word gap is typed through.</summary>
        private const int flag_wrong_input_on_word_gaps = 8;

        /// <summary>Bit 4 of the CONFIG frame's flags word: the spacebar is the word boundary (a gap
        /// typo parks the caret, a mid-word space is a typo rather than a rejection).</summary>
        private const int flag_strict_spaces = 16;

        /// <summary>Bit 5 of the CONFIG frame's flags word: the caret was unpinned from the playhead
        /// and snapped forward when the next line started (backlog 208's flexible-lines default).</summary>
        private const int flag_flexible_lines = 32;

        /// <summary>Bit 6 of the CONFIG frame's flags word: a freestyle slot or a stretched
        /// identical-character run is judged on its own character target rather than its syllable's
        /// span.</summary>
        private const int flag_char_timed_stretch = 64;

        /// <summary>Bit 7 of the CONFIG frame's flags word: rushing onto the next line was bounded to
        /// the same margin dragging is granted at the other end of a line (backlog 218).</summary>
        private const int flag_bounded_rush = 128;

        /// <summary>Bit 8 of the CONFIG frame's flags word: the first character of a syllable is
        /// judged on its distance from the syllable's start rather than paid 0 anywhere in the sung
        /// span (backlog 247).</summary>
        private const int flag_first_char_timing = 256;

        /// <summary>Bit 9 of the CONFIG frame's flags word: every frame's time in this run is a WALL
        /// stamp on the axis anchored at the CONFIG frame's own time, not a lyric time (backlog 256,
        /// the Puppeteer era). The only bit here that describes the FRAMES rather than the
        /// engine.</summary>
        private const int flag_wall_clock_frames = 512;

        public void FromLegacy(LegacyReplayFrame currentFrame, IBeatmap beatmap, ReplayFrame? lastFrame = null)
        {
            Character = (char)(int)(currentFrame.MouseX ?? 0);

            int flags = (int)(currentFrame.MouseY ?? 0);

            AllowWrongInput = (flags & flag_allow_wrong_input) != 0;
            SpaceSkipsWord = (flags & flag_space_skips_word) != 0;
            SyllableTiming = (flags & flag_syllable_timing) != 0;
            WrongInputOnWordGaps = (flags & flag_wrong_input_on_word_gaps) != 0;
            StrictSpaces = (flags & flag_strict_spaces) != 0;
            FlexibleLines = (flags & flag_flexible_lines) != 0;
            CharTimedStretch = (flags & flag_char_timed_stretch) != 0;
            BoundedRush = (flags & flag_bounded_rush) != 0;
            FirstCharTiming = (flags & flag_first_char_timing) != 0;
            WallClockFrames = (flags & flag_wall_clock_frames) != 0;
        }

        public LegacyReplayFrame ToLegacy(IBeatmap beatmap) =>
            new LegacyReplayFrame(Time, Character, IsConfig ? configFlags() : 0, ReplayButtonState.None);

        private int configFlags() =>
            (AllowWrongInput ? flag_allow_wrong_input : 0)
            | (SpaceSkipsWord ? flag_space_skips_word : 0)
            | (SyllableTiming ? flag_syllable_timing : 0)
            | (WrongInputOnWordGaps ? flag_wrong_input_on_word_gaps : 0)
            | (StrictSpaces ? flag_strict_spaces : 0)
            | (FlexibleLines ? flag_flexible_lines : 0)
            | (CharTimedStretch ? flag_char_timed_stretch : 0)
            | (BoundedRush ? flag_bounded_rush : 0)
            | (FirstCharTiming ? flag_first_char_timing : 0)
            | (WallClockFrames ? flag_wall_clock_frames : 0);

        /// <summary>
        /// Never equivalent: every frame is a discrete keystroke. Two identical characters at the
        /// same (rounded) time are two real keypresses and must both survive recording, so the
        /// recorder's frame-collapse optimisation is disabled outright.
        /// </summary>
        public override bool IsEquivalentTo(ReplayFrame other) => false;
    }
}
