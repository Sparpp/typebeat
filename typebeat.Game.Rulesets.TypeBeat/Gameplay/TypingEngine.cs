// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Ported verbatim from type!beat TypeBeat.Game/Gameplay/TypingEngine.cs (regression-anchored).
// type!beat gameplay-core: the headless gameplay/judgement heart.
// Time-driven line activation/sealing, keypress judgement, backspace, auto-skip,
// score/combo/accuracy/active-time-WPM/sync accumulation, SyncTimeline capture.
// Pure C#: zero osu.Framework dependencies. Driven entirely by explicit
// double-millisecond time arguments. Events fire synchronously on the caller thread.

using System;
using System.Collections.Generic;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    public sealed class TypingEngine
    {
        /// <summary>
        /// How long before a line's first typeable cell's target the line becomes typeable
        /// (<see cref="TypingLine.ActivationTime"/>). Also the length of the on-screen approach
        /// cue, so the depleting bar exactly spans "you may type now" to "the word lands".
        /// </summary>
        public const double CUE_LEAD_MS = 1500;

        /// <summary>
        /// Fletcher mod: how many COUNTABLE characters (typeable and not a space, the same currency
        /// the Flashlight window measures in) the player's caret may sit ahead of the playhead before
        /// a keypress stops earning combo. The press still lands and still scores; it simply cannot
        /// build a combo while the caret is out past the cap (see <see cref="FletcherEnabled"/>).
        /// </summary>
        public const int FLETCHER_MAX_CHARS_AHEAD = 5;

        /// <summary>
        /// Fletcher mod: extra time past a line's normal hard deadline
        /// (<see cref="TypingLine.EndTime"/> + <see cref="TypingLine.SealGraceMs"/>) that the engine
        /// holds the line open while the PLAYER is still on it, so a dragging player may finish the
        /// line the song has already left. Deliberately the same magnitude as
        /// <see cref="CUE_LEAD_MS"/>: the beat of grace the game gives you to get ready, granted at
        /// the other end of the line as well. Bounded so a run always terminates: past it the line
        /// force-seals, its untyped cells become misses, and the caret lands on the next line.
        /// </summary>
        public const double FLETCHER_DRAG_GRACE_MS = 1500;

        private const int combo_cap = 50;

        /// <summary>How many of the most recent correct keypresses <see cref="LiveRollingWpm"/> averages over.</summary>
        private const int rolling_wpm_window = 30;

        public LyricBeatmap Beatmap { get; }

        public IReadOnlyList<TypingLine> Lines => lines;

        public SyncWindows Windows { get; }

        /// <summary>-1 before the first line and after finish.</summary>
        public int ActiveLineIndex => activeLineIndex;

        /// <summary>
        /// Whether a lyric line is currently typeable: false during the pre-roll, the dead zone
        /// between a line's seal and the next line's cue, and after the final line. This is the seam
        /// the playfield's raw key handler gates on: keys are consumed for typing only while a line
        /// is active, and fall through to global bindings (so Space can trigger the skip overlay)
        /// once the player has reached the end of the line. Because Space is itself a typeable
        /// character, gating on an active line is what guarantees a skip can never eat a live keystroke.
        /// </summary>
        public bool LineIsActive => activeLineIndex != -1;

        /// <summary>
        /// Whether the PLAYHEAD is inside a typeable line window: the plain time rule
        /// (ActivationTime &lt;= now &lt; EndTime + SealGraceMs on the first unsealed line), read
        /// independently of where the player's caret has got to. Equal to <see cref="LineIsActive"/>
        /// without <see cref="FletcherEnabled"/>; under Fletcher the two diverge, because the caret
        /// can be parked on a line the song has not reached (rush) or still finishing one the song
        /// has left (drag). The key handler uses it so Space still reaches the skip overlay during a
        /// real instrumental gap.
        /// </summary>
        public bool SongWindowOpen
        {
            get
            {
                if (isFinished || nextSealIndex >= lines.Count || lastUpdateTime is not double time)
                    return false;

                var line = lines[nextSealIndex];

                return time >= line.ActivationTime && time < line.EndTime + line.SealGraceMs;
            }
        }

        /// <summary>
        /// True while the player has put nothing into the active line yet (no cell behind the caret
        /// is Correct or Wrong; leading auto-skipped punctuation does not count as progress). Used
        /// by the key handler under <see cref="FletcherEnabled"/> to tell "parked on a line I have
        /// not started" from "typing it".
        /// </summary>
        public bool ActiveLineUntouched
        {
            get
            {
                if (activeLineIndex == -1)
                    return false;

                var cells = lines[activeLineIndex].Cells;
                int end = Math.Min(caretIndex, cells.Count);

                for (int i = 0; i < end; i++)
                {
                    if (cells[i].State == CellState.Correct || cells[i].State == CellState.Wrong)
                        return false;
                }

                return true;
            }
        }

        /// <summary>
        /// The first line that has not sealed yet; -1 once every line has sealed. While no line is
        /// active (pre-roll, or the dead zone between a seal and the next line's cue) this is the
        /// UPCOMING line, the one the stage should focus, dimmed, after the boundary scroll.
        /// </summary>
        public int NextUnsealedLineIndex => nextSealIndex < lines.Count ? nextSealIndex : -1;

        /// <summary>Display-cell index in the active line; == Cells.Count when complete.</summary>
        public int CaretIndex => caretIndex;

        public bool IsLineComplete => activeLineIndex != -1 && caretIndex >= lines[activeLineIndex].Cells.Count;

        public bool IsFinished => isFinished;

        public long Score => score;

        public int Combo => combo;

        public int MaxCombo => maxCombo;

        /// <summary>correctKeypresses / allCharKeypresses; 1.0 before any keypress.</summary>
        public double LiveAccuracy => totalKeypresses == 0 ? 1.0 : correctKeypresses / (double)totalKeypresses;

        /// <summary>Gross WPM over active time only; 0 before any active time.</summary>
        public double LiveWpm
        {
            get
            {
                if (activeTimeMs <= 0)
                    return 0;

                return (countCorrectCells() / 5.0) / (activeTimeMs / 60000.0);
            }
        }

        /// <summary>
        /// Gross WPM over the last <see cref="rolling_wpm_window"/> correct keypresses: the HUD's live
        /// readout, so the number tracks how fast the player is typing RIGHT NOW instead of averaging the
        /// whole run flat. Display only; <see cref="LiveWpm"/> and <see cref="ResultsSummary.Wpm"/> keep
        /// the whole-run figure.
        /// Falls back to <see cref="LiveWpm"/> until the window holds at least two presses spread over a
        /// non-zero span, so the readout is meaningful from the first seconds instead of flapping.
        /// The clock is active time, exactly like <see cref="LiveWpm"/>: count-ins, instrumental gaps and
        /// post-line-completion waits do not decay the value, they simply do not pass.
        /// </summary>
        public double LiveRollingWpm
        {
            get
            {
                if (rollingCount < 2)
                    return LiveWpm;

                // Once the ring is full, the next slot to write is also the oldest entry.
                double oldest = rollingSamples[rollingCount < rolling_wpm_window ? 0 : rollingNext];
                double newest = rollingSamples[(rollingNext + rolling_wpm_window - 1) % rolling_wpm_window];
                double spanMs = newest - oldest;

                // Every press in the window landed within a single frame, so active time never advanced
                // between them: there is no span to divide by, defer to the whole-run figure.
                if (spanMs <= 0)
                    return LiveWpm;

                // n presses bound n-1 inter-key gaps, so the span covers (n-1) chars' worth of typing,
                // not n. Using n would inflate the readout by n/(n-1) (3.4% at a 30-press window, and
                // badly more while the window is still filling).
                return ((rollingCount - 1) / 5.0) / (spanMs / 60000.0);
            }
        }

        /// <summary>Mean sync quality (x100) over cells resolved so far (judged correct + sealed); 100 before anything resolves.</summary>
        public double LiveSyncPercent
        {
            get
            {
                double sum = 0;
                int resolved = 0;

                for (int i = 0; i < lines.Count; i++)
                {
                    foreach (var cell in lines[i].Cells)
                    {
                        if (!cell.IsTypeable)
                            continue;

                        if (cell.State == CellState.Correct && cell.JudgedDelta is double d)
                        {
                            sum += windowsFor(cell).SyncQuality(d);
                            resolved++;
                        }
                        else if (lineSealed[i])
                        {
                            // Missed / still-Wrong at seal: q = 0.
                            resolved++;
                        }
                    }
                }

                return resolved == 0 ? 100 : 100 * sum / resolved;
            }
        }

        /// <summary>Current run of consecutive rejected wrong keys; any accepted char resets it to 0.</summary>
        public int ConsecutiveWrongKeys => consecutiveWrongKeys;

        /// <summary>Mashing mod (Relax): every keypress is judged as the caret cell's expected char.</summary>
        public bool MashingEnabled { get; set; }

        /// <summary>
        /// Literate mod: when true, input is matched against the target's EXACT case (no
        /// <see cref="Typeability.Fold"/>), so a right letter typed in the wrong case is judged
        /// wrong: rejected/miss, exactly like any other wrong char. Off by default: gameplay is
        /// case-insensitive. Requires the input path to actually produce upper-case chars for
        /// Shift-held keys (see <see cref="KeyCharMap"/>), else capitals would be untypeable.
        /// </summary>
        public bool CaseSensitive { get; set; }

        /// <summary>
        /// Legacy "allow wrong input" setting: wrong (non-space) characters are typed through and
        /// marked red instead of rejected, and can be backspaced. Off by default (strict rejection).
        /// </summary>
        public bool AllowWrongInput { get; set; }

        /// <summary>
        /// Fletcher mod ("Were you Rushing or were you Dragging?!"): decouples the player's caret
        /// from the song's playhead. Three behaviours, all confined to this flag so the default path
        /// stays byte-identical:
        /// <list type="bullet">
        /// <item>RUSH FREEDOM: finishing a line moves the caret straight on to the next one instead
        /// of waiting for its cue (<see cref="rollForwardIfFinishedEarly"/>). The finished line is
        /// left unsealed and seals on its own normal deadline with nothing missed.</item>
        /// <item>DRAG FREEDOM: a line the player is still typing is not force-sealed at its normal
        /// deadline; the seal is deferred by <see cref="FLETCHER_DRAG_GRACE_MS"/> so the caret is
        /// never yanked off a line mid-word (<see cref="sealPermitted"/>).</item>
        /// <item>CHARACTER-DISTANCE RUSH CAP: a press that puts the caret more than
        /// <see cref="FLETCHER_MAX_CHARS_AHEAD"/> countable chars ahead of the playhead lands and
        /// scores as normal but earns no combo.</item>
        /// </list>
        /// Per-char judgement windows are untouched: rushing reads as early deltas and dragging as
        /// late ones, so accuracy, sync% and the judgement counts report the drift honestly.
        /// </summary>
        public bool FletcherEnabled { get; set; }

        public event Action<CharJudgement>? CharJudged;
        public event Action<int>? LineActivated;
        public event Action<LineSealResult>? LineSealed;
        public event Action? ComboBroken;
        public event Action? Finished;

        /// <summary>
        /// A wrong key was REJECTED: nothing was input (no cell state change, no caret move,
        /// no <see cref="CharJudged"/>), but combo has been reset and the consecutive streak
        /// incremented. Carries the offending char for feedback visuals.
        /// </summary>
        public event Action<char>? WrongKeyRejected;

        private readonly List<TypingLine> lines;
        private readonly bool[] lineSealed;
        private readonly Dictionary<JudgementType, int> counts = new Dictionary<JudgementType, int>();
        private readonly List<SyncSample> syncTimeline = new List<SyncSample>();

        /// <summary>Ring buffer of the ACTIVE-TIME stamps of the last correct keypresses (see <see cref="LiveRollingWpm"/>).</summary>
        private readonly double[] rollingSamples = new double[rolling_wpm_window];

        private readonly int totalTypeableCells;

        // --- Countable-character stream (Fletcher). The whole map read as one run of COUNTABLE
        // cells (typeable and not a space), which is the currency the rush cap measures in.
        // countableTargets: every countable cell's target time, sorted ascending, so the playhead's
        // position is a binary search. countableBase[k] / countablePrefix[k][i]: where line k, cell i
        // sits in that stream, so the caret's position is a lookup. All immutable after construction.
        private readonly double[] countableTargets;
        private readonly int[] countableBase;
        private readonly int[][] countablePrefix;

        private int nextSealIndex; // first line not yet sealed; lines seal strictly in order
        private int activeLineIndex = -1;
        private int caretIndex;
        private bool isFinished;

        private long score;
        private int combo;
        private int maxCombo;
        private int totalKeypresses;
        private int correctKeypresses;
        private int errorCount;
        private int consecutiveWrongKeys;

        private double activeTimeMs;
        private double? lastUpdateTime;

        private int rollingCount; // entries held in rollingSamples, capped at rolling_wpm_window
        private int rollingNext;  // next slot to write; also the oldest entry once the ring is full

        public TypingEngine(LyricBeatmap beatmap)
        {
            Beatmap = beatmap ?? throw new ArgumentNullException(nameof(beatmap));
            Windows = SyncWindows.For(beatmap.Granularity);

            lines = new List<TypingLine>(beatmap.Lines.Count);

            foreach (var line in beatmap.Lines)
                lines.Add(TypingLine.FromLyricLine(line, beatmap.Granularity));

            lineSealed = new bool[lines.Count];

            foreach (var line in lines)
                totalTypeableCells += line.TypeableCount;

            foreach (JudgementType type in Enum.GetValues<JudgementType>())
                counts[type] = 0;

            countableBase = new int[lines.Count];
            countablePrefix = new int[lines.Count][];
            var targets = new List<double>();

            for (int k = 0; k < lines.Count; k++)
            {
                var cells = lines[k].Cells;
                countableBase[k] = targets.Count;
                var prefix = new int[cells.Count + 1];

                for (int i = 0; i < cells.Count; i++)
                {
                    prefix[i + 1] = prefix[i];

                    if (!cells[i].IsCountable)
                        continue;

                    prefix[i + 1]++;
                    targets.Add(cells[i].TargetTime);
                }

                countablePrefix[k] = prefix;
            }

            // Overlapping lines can interleave their targets across a boundary, so sort rather than
            // assume the per-line order carries: the playhead lookup only needs "how many countable
            // targets are at or before this time".
            targets.Sort();
            countableTargets = targets.ToArray();
        }

        /// <summary>
        /// How many COUNTABLE characters the song has reached by <paramref name="time"/>: the count
        /// of countable cells across the whole map whose target time is at or before it. The
        /// playhead's position in the countable stream, and the reference the Fletcher rush cap is
        /// measured against. Monotonic in time and a pure function of the beatmap, so a replay
        /// reproduces it exactly.
        /// </summary>
        public int PlayheadCountablePosition(double time)
        {
            int lo = 0;
            int hi = countableTargets.Length;

            while (lo < hi)
            {
                int mid = (lo + hi) / 2;

                if (countableTargets[mid] <= time)
                    lo = mid + 1;
                else
                    hi = mid;
            }

            return lo;
        }

        /// <summary>
        /// The player caret's position in the same countable stream: every countable cell in the
        /// lines before the active one, plus the countable cells behind the caret within it. 0 when
        /// no line is active.
        /// </summary>
        public int CaretCountablePosition
        {
            get
            {
                if (activeLineIndex == -1)
                    return 0;

                var prefix = countablePrefix[activeLineIndex];

                return countableBase[activeLineIndex] + prefix[Math.Clamp(caretIndex, 0, prefix.Length - 1)];
            }
        }

        /// <summary>
        /// Signed countable-character drift of the caret against the playhead at
        /// <paramref name="time"/>: positive = rushing ahead, negative = dragging behind. The
        /// quantity the Fletcher rush cap bounds (and the honest read-out of what the mod is about).
        /// </summary>
        public int CharsAheadOfPlayhead(double time) => CaretCountablePosition - PlayheadCountablePosition(time);

        /// <summary>
        /// Call once per frame BEFORE routing input for that frame.
        /// Drives activation, sealing, active-time accrual, Finished.
        /// Never throws on out-of-order times (dt is clamped >= 0; lines never unseal).
        /// </summary>
        public void Update(double time)
        {
            // (1) Accrue active time while a line is active AND incomplete AND not finished
            //     (state as of the previous frame).
            if (lastUpdateTime is double last)
            {
                double dt = Math.Max(0, time - last);

                if (activeLineIndex != -1 && !IsLineComplete && !isFinished && wpmClockRuns(last))
                    activeTimeMs += dt;
            }

            lastUpdateTime = time;

            // Fletcher: a drag cutoff inside the seal loop below moves the caret straight on to the
            // next line; the activation is announced once, after the loop, so a catch-up cascade
            // through several stale lines still relayouts the stage exactly once.
            bool pendingActivation = false;

            // (2) Seal, in order, every line whose deadline has passed. Normal lines seal AT
            //     EndTime; lines with a seal grace (vocals overrunning into the next line, or a
            //     boundary-pinned last target) stay typeable through the grace window and seal
            //     early the moment nothing is left to type, so the next line isn't held up.
            while (nextSealIndex < lines.Count && canSeal(lines[nextSealIndex], time) && sealPermitted(nextSealIndex, time))
            {
                int index = nextSealIndex;
                var line = lines[index];

                int missed = 0;

                foreach (var cell in line.Cells)
                {
                    if (cell.IsTypeable && cell.State == CellState.Untyped)
                    {
                        cell.State = CellState.Missed;
                        missed++;
                        counts[JudgementType.Miss]++;
                    }
                }

                bool broke = missed >= 1;

                if (broke)
                {
                    // AT MOST ONE combo break per sealed line, no matter how many cells were missed.
                    combo = 0;
                }

                lineSealed[index] = true;
                nextSealIndex++;

                if (activeLineIndex == index)
                {
                    if (FletcherEnabled && index + 1 < lines.Count)
                    {
                        // Drag cutoff: the player ran out of borrowed time mid-line. Land them on the
                        // next line immediately rather than in a dead zone. Setting the caret here,
                        // inside the loop, is also what stops one cutoff cascading into the line the
                        // player just landed on: the next sealPermitted call sees them on it and
                        // grants it its own drag grace. A cascade still happens when that grace has
                        // ALSO expired (an idle player), which is the intended catch-up to the song.
                        activeLineIndex = index + 1;
                        caretIndex = 0;
                        pendingActivation = true;
                    }
                    else
                    {
                        activeLineIndex = -1;
                        caretIndex = 0;
                    }
                }

                if (broke)
                    ComboBroken?.Invoke();

                LineSealed?.Invoke(new LineSealResult(index, missed, broke));
            }

            // (3) Activate strictly by time: the first unsealed line, while it is judgeable
            //     (ActivationTime <= time < EndTime + grace). ActivationTime is the constant cue
            //     before the first word (CUE_LEAD_MS), not the boundary; crossing a boundary
            //     scrolls the stack (the seal above), but typing opens relative to the vocals.
            //     Typing never unlocks the next line.
            if (nextSealIndex >= lines.Count)
            {
                if (!isFinished)
                {
                    isFinished = true;
                    activeLineIndex = -1;
                    caretIndex = 0;
                    Finished?.Invoke();
                }
            }
            else if (activeLineIndex == -1)
            {
                var candidate = lines[nextSealIndex];

                if (time >= candidate.ActivationTime && time < candidate.EndTime + candidate.SealGraceMs)
                {
                    activeLineIndex = nextSealIndex;
                    caretIndex = 0;
                    autoSkipForward();
                    LineActivated?.Invoke(activeLineIndex);
                }
            }
            else if (pendingActivation)
            {
                // Fletcher drag cutoff (see the seal loop): the caret already moved, announce it once.
                autoSkipForward();
                LineActivated?.Invoke(activeLineIndex);
            }
        }

        /// <summary>
        /// Whether the WPM/sync active-time clock runs for the frame ending at <paramref name="previousTime"/>.
        /// Always, by default. Under <see cref="FletcherEnabled"/> the caret can be parked at the head
        /// of a line the song has not reached yet (rush freedom rolls it forward the instant a line is
        /// finished), and a clock that ran through a 20-second instrumental would read the wait as
        /// typing time; so the clock runs only from the point the playhead reaches that line's
        /// ActivationTime, which is exactly when the line would have gone active without the mod.
        /// </summary>
        private bool wpmClockRuns(double previousTime)
            => !FletcherEnabled || activeLineIndex == -1 || previousTime >= lines[activeLineIndex].ActivationTime;

        /// <summary>
        /// Fletcher DRAG FREEDOM: a line the player is still typing must not be force-sealed out from
        /// under them at its normal deadline. The seal is deferred while the caret is on the line, up
        /// to <see cref="FLETCHER_DRAG_GRACE_MS"/> past its hard deadline; past that the line seals as
        /// usual (untyped cells become misses, one combo break) and the caret is moved on. Always true
        /// without the mod, and true under it for any line the player is not currently on, so a
        /// finished-early line still seals exactly on its own deadline.
        /// </summary>
        private bool sealPermitted(int index, double time)
        {
            if (!FletcherEnabled || activeLineIndex != index)
                return true;

            var line = lines[index];

            // Nothing left untyped means there is no drag to protect: the line seals on its normal
            // deadline, exactly as it would without the mod. (This is also what lets the FINAL line,
            // which has no next line to roll on to, finish the run on time once it is fully typed.)
            if (!hasUntypedTypeable(line))
                return true;

            return time >= line.EndTime + line.SealGraceMs + FLETCHER_DRAG_GRACE_MS;
        }

        /// <summary>
        /// A line may seal once its EndTime has passed AND either its grace window has elapsed
        /// or nothing typeable is left untyped (early seal so the next line isn't delayed).
        /// </summary>
        private static bool canSeal(TypingLine line, double time)
        {
            if (time < line.EndTime)
                return false;

            if (time >= line.EndTime + line.SealGraceMs)
                return true;

            return !hasUntypedTypeable(line);
        }

        /// <summary>Whether the line still holds a typeable cell nobody has put anything into.</summary>
        private static bool hasUntypedTypeable(TypingLine line)
        {
            foreach (var cell in line.Cells)
            {
                if (cell.IsTypeable && cell.State == CellState.Untyped)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Process a lowercased char from KeyCharMap at gameplay time <paramref name="time"/>.
        /// Returns false when inert (no active line / line complete / finished).
        /// A wrong char is REJECTED, never input, but still breaks combo, stays in the
        /// accuracy denominator forever, and grows <see cref="ConsecutiveWrongKeys"/>.
        /// </summary>
        public bool ProcessKey(char c, double time)
        {
            if (isFinished || activeLineIndex == -1)
                return false;

            var line = lines[activeLineIndex];

            // Hop auto-skip cells before matching (normally already done on advance/activation).
            autoSkipForward();

            if (caretIndex >= line.Cells.Count)
                return false; // line complete, wait for the song.

            var cell = line.Cells[caretIndex];

            // Mashing mod: any key is the right key; judge it as the caret cell's expected char.
            // A FREESTYLE cell is exempt: it already accepts any key, and rewriting c here would
            // stamp the authoring marker over the char the player actually pressed (the one thing
            // a freestyle cell must remember). No double effect, mashing simply has nothing to add.
            if (MashingEnabled && !cell.IsFreestyle)
                c = cell.Expected;

            double delta = time - cell.TargetTime;
            // FREESTYLE cell: every char matches, in any case, under every mod (so the Literate
            // mod's exact-case rule and the allow-wrong-input path are both bypassed for it). The
            // press is then judged exactly like a correct char: same windows, points, combo,
            // accuracy and completion, with the pressed char kept in TypedChar.
            // Literate mod folds nothing: the typed char must match the target's exact case.
            // Default gameplay is case-insensitive (both sides lower-cased through Fold).
            bool matched = cell.IsFreestyle
                           || (CaseSensitive ? c == cell.Expected : Typeability.Fold(c) == Typeability.Fold(cell.Expected));

            if (!matched)
            {
                // Legacy "allow wrong input" setting: a wrong LETTER is typed through, marked red,
                // backspaceable, instead of rejected. The space key stays strict (no wrong space,
                // and no wrong char consuming a word boundary), and this mode never feeds the
                // mash-fail streak (consecutiveWrongKeys is left at 0).
                if (AllowWrongInput && c != ' ' && cell.Expected != ' ')
                {
                    totalKeypresses++;
                    errorCount++;
                    combo = 0;
                    counts[JudgementType.WrongChar]++;

                    cell.State = CellState.Wrong;
                    cell.TypedChar = c;

                    int wrongCellIndex = caretIndex;
                    caretIndex++;
                    autoSkipForward();

                    ComboBroken?.Invoke();
                    // Miss result (see DrawableTypeBeatHitObject.toHitResult) + red/shake feedback;
                    // a later backspace + correct retype re-judges the cell Correct for completion.
                    CharJudged?.Invoke(new CharJudgement(activeLineIndex, wrongCellIndex, JudgementType.WrongChar, delta, 0, combo));
                    rollForwardIfFinishedEarly();
                    return true;
                }

                // Strict (default): wrong key REJECTED, no cell mutation, no caret advance, no
                // CharJudged. It still costs the accuracy denominator, an error, a combo break, and
                // the consecutive-wrong-key streak (the game fails the play when it hits 13).
                totalKeypresses++;
                errorCount++;
                consecutiveWrongKeys++;
                combo = 0;
                counts[JudgementType.WrongChar]++;
                ComboBroken?.Invoke();
                WrongKeyRejected?.Invoke(c);
                return true;
            }

            consecutiveWrongKeys = 0;

            // Correctly re-typing a cell that was EVER judged correct (reached again via backspace,
            // which resets State but not FirstCorrectDelta) is scoring-inert: no counters, no
            // points/combo, no timeline sample, and the first judgement stands; otherwise
            // backspace-retype farms score, combo and accuracy without bound.
            bool inertRetype = cell.FirstCorrectDelta is not null;

            JudgementType type;
            int points = 0;

            if (inertRetype)
            {
                delta = cell.FirstCorrectDelta!.Value;
                type = windowsFor(cell).Classify(delta);

                cell.State = CellState.Correct;
                cell.TypedChar = c;
                cell.JudgedDelta = delta;
            }
            else
            {
                // ALL scoring keypresses (correct + wrong) count in the accuracy denominator, forever.
                totalKeypresses++;

                // Correct char: always accepted; the clock decides the judgement.
                // Premature/Lagging still count as CORRECT keypresses (right char, wrong time).
                correctKeypresses++;

                type = windowsFor(cell).Classify(delta);
                int basePoints = SyncWindows.BasePoints(type);

                // Fletcher RUSH CAP, evaluated before the caret moves: does this press put the caret
                // more than FLETCHER_MAX_CHARS_AHEAD countable chars past the playhead?
                bool rushedPastCap = FletcherEnabled && rushesPastCap(cell, time);

                if (basePoints > 0)
                {
                    // Multiplier reads combo BEFORE the increment; capped at combo_cap => up to 2.0x.
                    points = (int)Math.Round(basePoints * (1 + Math.Min(combo, combo_cap) / (double)combo_cap));
                    score += points;

                    if (rushedPastCap)
                    {
                        // A combo penalty, not a block: the char lands and scores exactly as it would
                        // without the mod, but no combo may accumulate while the caret is out past the
                        // cap. ComboBroken therefore fires once, on the press that crosses the line,
                        // and re-arms the moment a press lands back inside it (combo starts building
                        // again, so the next excursion breaks it again).
                        bool hadCombo = combo > 0;
                        combo = 0;

                        if (hadCombo)
                            ComboBroken?.Invoke();
                    }
                    else
                    {
                        combo++;
                        maxCombo = Math.Max(maxCombo, combo);
                    }
                }
                else
                {
                    // Premature / Lagging: 0 points, combo break, char still accepted.
                    combo = 0;
                    ComboBroken?.Invoke();
                }

                cell.State = CellState.Correct;
                cell.TypedChar = c;
                cell.JudgedDelta = delta;
                cell.FirstCorrectDelta = delta; // the one awarded judgement; retypes are inert.

                // SyncTimeline records every AWARDED correct-char judgement, incl. Premature/Lagging.
                syncTimeline.Add(new SyncSample(time, delta));

                counts[type]++;
            }

            // Log the press for the HUD's rolling WPM. Both branches above land the cell Correct, so a
            // scoring-inert retype still counts here: this is a record of keystrokes, not of cell states.
            pushRollingSample();

            int judgedCellIndex = caretIndex;
            caretIndex++;
            autoSkipForward();

            CharJudged?.Invoke(new CharJudgement(activeLineIndex, judgedCellIndex, type, delta, points, combo));
            rollForwardIfFinishedEarly();
            return true;
        }

        /// <summary>
        /// Fletcher rush cap: would accepting <paramref name="cell"/> at <paramref name="time"/> leave
        /// the caret more than <see cref="FLETCHER_MAX_CHARS_AHEAD"/> countable chars past the
        /// playhead? Measured on the caret position AFTER the press, so with a cap of 5 the fifth
        /// char ahead is still fine and the sixth is not. A non-countable cell (a space) spends no
        /// budget, so pressing it can never push the caret over the line by itself.
        /// </summary>
        private bool rushesPastCap(TypingCell cell, double time)
        {
            int after = CaretCountablePosition + (cell.IsCountable ? 1 : 0);

            return after - PlayheadCountablePosition(time) > FLETCHER_MAX_CHARS_AHEAD;
        }

        /// <summary>
        /// Fletcher RUSH FREEDOM: the moment a press finishes a line, the caret moves straight on to
        /// the next one instead of waiting for its activation cue. The finished line is left UNSEALED
        /// and seals on its own normal deadline (with nothing missed, since it is fully typed), so
        /// nothing about the song's timeline moves; only the player's position does. No-op on the last
        /// line, which keeps the default "line complete, wait for the song" behaviour that lets the
        /// key handler pass Space through to the skip overlay.
        /// </summary>
        private void rollForwardIfFinishedEarly()
        {
            if (!FletcherEnabled || isFinished || activeLineIndex == -1)
                return;

            if (caretIndex < lines[activeLineIndex].Cells.Count)
                return;

            if (activeLineIndex + 1 >= lines.Count)
                return;

            // Lines seal in order and the player never leaves a line except by finishing it or by a
            // drag cutoff (which advances nextSealIndex with them), so the next line is always unsealed.
            activeLineIndex++;
            caretIndex = 0;
            autoSkipForward();
            LineActivated?.Invoke(activeLineIndex);
        }

        /// <summary>Windows for a cell's judgement tier (Line for estimated/low-confidence timing).</summary>
        private static SyncWindows windowsFor(TypingCell cell) => SyncWindows.For(cell.JudgeGranularity);

        /// <summary>
        /// Erase the most recent typed cell within the active line, stepping back transparently
        /// over AutoSkipped punctuation (which is un-skipped so retyping re-marks it).
        /// Returns false if nothing to erase. The erased keypress stays in the accuracy counts.
        /// </summary>
        public bool ProcessBackspace()
        {
            if (isFinished || activeLineIndex == -1)
                return false;

            var cells = lines[activeLineIndex].Cells;

            // Find the nearest non-auto-skipped cell behind the caret (scan first, mutate after).
            int target = caretIndex - 1;

            while (target >= 0 && cells[target].State == CellState.AutoSkipped)
                target--;

            if (target < 0)
                return false;

            // Un-skip the punctuation cells we stepped back over.
            for (int i = target + 1; i < caretIndex; i++)
            {
                if (cells[i].State == CellState.AutoSkipped)
                    cells[i].State = CellState.Untyped;
            }

            var cell = cells[target];
            cell.State = CellState.Untyped;
            cell.TypedChar = null;
            cell.JudgedDelta = null;

            caretIndex = target;
            return true;
        }

        /// <summary>
        /// Signed lead/lag of the caret cell: time - Cells[CaretIndex].TargetTime;
        /// null when no judgeable caret cell (no active line / line complete / finished).
        /// </summary>
        public double? CurrentLeadLag(double time)
        {
            if (isFinished || activeLineIndex == -1)
                return null;

            var cells = lines[activeLineIndex].Cells;

            if (caretIndex >= cells.Count)
                return null;

            var cell = cells[caretIndex];

            if (!cell.IsTypeable)
                return null; // defensive: caret normally rests on a typeable cell.

            return time - cell.TargetTime;
        }

        public ResultsSummary BuildResults()
        {
            // SyncPercent over ALL typeable cells: finally-Correct cells contribute
            // SyncQuality(final correct delta); everything else (Missed / Wrong / unresolved) is 0.
            double qualitySum = 0;

            foreach (var line in lines)
            {
                foreach (var cell in line.Cells)
                {
                    if (cell.IsTypeable && cell.State == CellState.Correct && cell.JudgedDelta is double d)
                        qualitySum += windowsFor(cell).SyncQuality(d);
                }
            }

            double syncPercent = totalTypeableCells == 0 ? 100 : 100 * qualitySum / totalTypeableCells;

            double wpm = activeTimeMs <= 0 ? 0 : (countCorrectCells() / 5.0) / (activeTimeMs / 60000.0);

            return new ResultsSummary
            {
                Score = score,
                Accuracy = LiveAccuracy,
                Wpm = wpm,
                SyncPercent = syncPercent,
                MaxCombo = maxCombo,
                Counts = new Dictionary<JudgementType, int>(counts),
                SyncTimeline = syncTimeline.ToArray(),
                Artist = Beatmap.Metadata.Artist,
                Title = Beatmap.Metadata.Title,
            };
        }

        /// <summary>Correct cells (including spaces) across all lines: the WPM numerator source.</summary>
        private int countCorrectCells()
        {
            int count = 0;

            foreach (var line in lines)
            {
                foreach (var cell in line.Cells)
                {
                    if (cell.State == CellState.Correct)
                        count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Record one correct keypress at the current active time for <see cref="LiveRollingWpm"/>.
        /// Nothing reads this back into judgement, so it can never move a score. <see cref="ProcessBackspace"/>
        /// deliberately does NOT pop: the buffer is what the player typed, not what the cells currently
        /// hold, so backspacing and retyping simply logs another (later) press.
        /// </summary>
        private void pushRollingSample()
        {
            rollingSamples[rollingNext] = activeTimeMs;
            rollingNext = (rollingNext + 1) % rolling_wpm_window;

            if (rollingCount < rolling_wpm_window)
                rollingCount++;
        }

        /// <summary>Hop the caret forward over non-typeable cells, marking them AutoSkipped.</summary>
        private void autoSkipForward()
        {
            if (activeLineIndex == -1)
                return;

            var cells = lines[activeLineIndex].Cells;

            while (caretIndex < cells.Count && !cells[caretIndex].IsTypeable)
            {
                cells[caretIndex].State = CellState.AutoSkipped;
                caretIndex++;
            }
        }
    }
}
