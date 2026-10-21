// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// TypingEngine, JAPANESE ROMAJI INPUT: the internal romaji processor a Japanese Polyglot run types
// through, its pending reading, the commit that turns a finished reading into ordinary keypresses,
// and the Japanese input eras.

using System;

using System.Linq;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    public sealed partial class TypingEngine
    {
        /// <summary>Japanese Polyglot uses the internal romaji processor instead of an OS IME.</summary>
        public bool UsesJapaneseRomaji { get; }

        /// <summary>Extended replay bit 5; absent on older, committed-text Polyglot replays.</summary>
        private bool japaneseRomajiInput;

        public bool JapaneseRomajiInput
        {
            get => japaneseRomajiInput;
            set
            {
                if (japaneseRomajiInput == value)
                    return;
                japaneseRomajiInput = value;
                clearJapanesePending();
            }
        }

        private bool japaneseWordTiming = true;

        /// <summary>Japanese Polyglot words ignore romanised subdivisions. Extended replay bit 6.</summary>
        public bool JapaneseWordTiming
        {
            get => japaneseWordTiming;
            set
            {
                if (japaneseWordTiming == value)
                    return;
                japaneseWordTiming = value;
                if (!UsesJapaneseRomaji)
                    return;
                foreach (var line in lines)
                    line.SetJapaneseWordTiming(value);
                clearJapanesePending();
                rebuildCountableTargets();
                JapaneseWordTimingChanged?.Invoke();
            }
        }

        public event Action? JapaneseWordTimingChanged;

        /// <summary>Extended replay bit 7: single-character Japanese skips and keystroke WPM.</summary>
        public bool JapaneseInputEra2 { get; set; } = true;

        private bool japaneseInputEra3 = true;

        /// <summary>
        /// Extended replay bit 8, the third Japanese input era: Space skips the remainder of the
        /// authored Japanese word, and each line's romaji input plan is the word-aware one (a
        /// single-word line original read per timed unit, punctuation-tolerant surface matching,
        /// recorded word windows). Clear, a replay re-derives the single-character skip and the plan
        /// it was played on. The plan is swapped on the SAME cells, like
        /// <see cref="JapaneseWordTiming"/>'s timing.
        /// </summary>
        public bool JapaneseInputEra3
        {
            get => japaneseInputEra3;
            set
            {
                if (japaneseInputEra3 == value)
                    return;
                japaneseInputEra3 = value;
                if (!UsesJapaneseRomaji)
                    return;
                foreach (var line in lines)
                    line.SetJapaneseInputEra3(value);
                clearJapanesePending();
            }
        }

        private bool UsesJapaneseKeystrokeWpm => JapaneseInputEra2 && JapaneseRomajiInput && UsesJapaneseRomaji;

        private int japaneseWpmInputs;

        private int japaneseCompletionLine = -1;

        private int japaneseCompletionCaret = -1;

        private void protectJapaneseCompletion()
        {
            japaneseCompletionLine = activeLineIndex;
            japaneseCompletionCaret = caretIndex;
        }

        private void recordJapaneseInput()
        {
            if (!UsesJapaneseKeystrokeWpm)
                return;
            japaneseWpmInputs++;
            pushRollingSample();
        }

        private string japanesePending = string.Empty;

        private int japanesePendingLine = -1;

        private int japanesePendingCell = -1;

        private double japanesePendingTime;

        private bool committingJapanese;

        public string JapanesePending => japanesePendingLine == activeLineIndex && japanesePendingCell == caretIndex
            ? japanesePending : string.Empty;

        private void clearJapanesePending()
        {
            japanesePending = string.Empty;
            japanesePendingTimes.Clear();
            japanesePendingKanaCount = 0;
            japanesePendingLine = japanesePendingCell = -1;
        }

        private bool tryCommitJapanesePending()
        {
            if (!JapaneseRomajiInput || !UsesJapaneseRomaji || committingJapanese || JapanesePending.Length == 0)
                return false;
            var plan = lines[activeLineIndex].JapaneseInput;
            var group = JapaneseInputEra2 ? plan?.AtForInput(caretIndex) : plan?.At(caretIndex);
            // Space/Enter explicitly confirm a word, so a trailing n needs no next-letter lookahead.
            if (group == null || (JapaneseRomaji.Matches(group.Reading, JapanesePending, allowKana: JapaneseKanaInput) & JapaneseRomaji.Match.Complete) == 0)
                return false;
            commitJapanese(group, japanesePendingTime);
            return true;
        }

        private bool? processJapaneseKey(char c, double time, TypingLine line)
        {
            if (!JapaneseRomajiInput || !UsesJapaneseRomaji || committingJapanese || MashingEnabled || line.Cells[caretIndex].IsFreestyle)
                return null;

            if (c != ' ')
                japaneseCompletionLine = japaneseCompletionCaret = -1;
            if (japanesePendingLine != activeLineIndex || japanesePendingCell != caretIndex)
                clearJapanesePending();

            var plan = line.JapaneseInput;
            if (c == ' ' && line.Cells[caretIndex].Expected != ' ' && plan?.IsRomanCell(caretIndex) == false)
            {
                int originalLine = activeLineIndex;
                if (!tryCommitJapanesePending())
                {
                    bool confirmation = japaneseCompletionLine == activeLineIndex && japaneseCompletionCaret == caretIndex;
                    if (!JapaneseInputEra2 || !SpaceSkipsWord || !(AllowWrongInput || !InputEra2) || confirmation
                        || plan?.IsJapaneseCell(caretIndex) != true || line.Cells[caretIndex].State == CellState.Correct)
                        return false;

                    clearJapanesePending();
                    skipCurrentWord(time, singleJapaneseCharacter: !JapaneseInputEra3, japaneseWord: JapaneseInputEra3);
                    advanceCaretToFrontier();
                    rollForwardIfFinishedEarly(time);
                    return true;
                }

                // Confirmation may also supply a genuine lyric space or a manual newline. Never
                // run it as a word skip on a cell the confirmation just moved the caret onto.
                if (activeLineIndex == originalLine
                    && (caretIndex >= line.Cells.Count || line.Cells[caretIndex].Expected == ' '))
                    ProcessKey(' ', time);
                return true;
            }

            if (charMatches(c, line.Cells[caretIndex].Expected))
            {
                clearJapanesePending();
                return null; // Direct kana layouts and legacy committed characters still work.
            }

            var group = JapaneseInputEra2 ? plan?.AtForInput(caretIndex) : plan?.At(caretIndex);
            if (group == null)
            {
                char expected = line.Cells[caretIndex].Expected;
                char equivalent = expected is >= '\uff01' and <= '\uff5e' ? (char)(expected - 0xfee0) : expected switch
                {
                    '、' => ',', '。' => '.', '「' or '『' => '[', '」' or '』' => ']',
                    _ => expected,
                };
                if (equivalent != expected && (CaseSensitive ? c == equivalent : char.ToLowerInvariant(c) == char.ToLowerInvariant(equivalent)))
                {
                    clearJapanesePending();
                    committingJapanese = true;
                    try
                    {
                        bool accepted = ProcessKey(expected, time);
                        if (accepted)
                        {
                            recordJapaneseInput();
                            protectJapaneseCompletion();
                        }
                        return accepted;
                    }
                    finally
                    {
                        committingJapanese = false;
                    }
                }
                return null;
            }

            char lower = char.ToLowerInvariant(c);
            if (JapaneseKanaInput && lower is >= 'ぁ' and <= 'ゖ' or >= 'ァ' and <= 'ヶ')
                lower = JapaneseRomaji.Hiragana(lower.ToString())[0];
            string input = japanesePending + lower;
            var match = JapaneseRomaji.Matches(group.Reading, input, plan!.Following(group), allowKana: JapaneseKanaInput);
            if (match == JapaneseRomaji.Match.Invalid)
            {
                // A nasal's first n may also begin nn. The following consonant resolves that
                // ambiguity without being lost: commit n at its own time, then process this key.
                // Inside the line, a vowel pronunciation alias still needs nn/n'; a word/line
                // boundary can explicitly finish n before the next word's first vowel.
                string following = plan.Following(group);
                if ((JapaneseRomaji.Matches(group.Reading, japanesePending, following, allowKana: JapaneseKanaInput) & JapaneseRomaji.Match.Complete) != 0
                    && (following.Length == 0 || char.IsWhiteSpace(following[0]) || lower is not ('a' or 'i' or 'u' or 'e' or 'o' or 'y')))
                {
                    commitJapanese(group, japanesePendingTime);
                    return ProcessKey(c, time);
                }
                if (AllowWrongInput)
                    clearJapanesePending();
                return null; // Gatekeeper keeps a valid prefix when it rejects the wrong key.
            }

            recordJapaneseInput();
            if (japanesePending.Length == 0)
                japanesePendingStartTime = time;
            japanesePendingTimes.Add(time);
            japanesePending = input;
            japanesePendingLine = activeLineIndex;
            japanesePendingCell = caretIndex;
            japanesePendingTime = time;
            updateJapanesePendingProgress();
            if (match == JapaneseRomaji.Match.Complete)
                commitJapanese(group, time);
            return true;
        }

        private void commitJapanese(JapaneseInputPlan.Group group, double time)
        {
            int lineIndex = activeLineIndex;
            int startCell = caretIndex;
            double startTime = japanesePending.Length > 0 ? japanesePendingStartTime : time;
            double[] inputTimes = japanesePendingTimes.ToArray();
            var plan = lines[lineIndex].JapaneseInput!;
            int[]? inputEnds = JapaneseRomaji.InputEnds(group.Reading, japanesePending, plan.Following(group), allowKana: JapaneseKanaInput);
            int readingOffset = 0, inputOffset = 0;
            clearJapanesePending();
            committingJapanese = true;
            try
            {
                while (activeLineIndex == lineIndex && caretIndex < group.EndCellExclusive)
                {
                    int before = caretIndex;
                    double cellStart = startTime, cellEnd = time;
                    japaneseCommitInputTimes = null;
                    string? characterReading = plan.CharacterReadingAt(caretIndex);
                    if (JapaneseCharacterTiming && inputEnds != null && characterReading != null
                        && readingOffset + characterReading.Length <= inputEnds.Length && inputTimes.Length > 0)
                    {
                        int endInput = inputEnds[readingOffset + characterReading.Length - 1];
                        cellStart = inputTimes[Math.Min(inputOffset, inputTimes.Length - 1)];
                        cellEnd = inputTimes[Math.Min(endInput - 1, inputTimes.Length - 1)];
                        japaneseCommitInputTimes = inputTimes.Skip(inputOffset).Take(endInput - inputOffset).ToArray();
                        readingOffset += characterReading.Length;
                        inputOffset = endInput;
                    }
                    japaneseCommitEndTime = cellEnd;
                    ProcessKey(lines[lineIndex].Cells[caretIndex].Expected, JapaneseCharacterTiming ? cellStart : time);
                    if (caretIndex == before && activeLineIndex == lineIndex)
                        break;
                }
            }
            finally
            {
                committingJapanese = false;
                japaneseCommitEndTime = null;
                japaneseCommitInputTimes = null;
                if (JapaneseInputEra2 && (activeLineIndex != lineIndex || caretIndex != startCell))
                    protectJapaneseCompletion();
            }
        }
    }
}
