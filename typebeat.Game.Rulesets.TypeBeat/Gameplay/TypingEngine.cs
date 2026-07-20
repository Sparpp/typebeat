// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Ported verbatim from type!beat TypeBeat.Game/Gameplay/TypingEngine.cs (regression-anchored).
// type!beat gameplay-core: the headless gameplay/judgement heart.
// Time-driven line activation/sealing, keypress judgement, backspace, auto-skip,
// score/combo/accuracy/active-time-WPM/sync accumulation, SyncTimeline capture.
// Pure C# — zero osu.Framework dependencies. Driven entirely by explicit
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

        private const int combo_cap = 50;

        public LyricBeatmap Beatmap { get; }

        public IReadOnlyList<TypingLine> Lines => lines;

        public SyncWindows Windows { get; }

        /// <summary>-1 before the first line and after finish.</summary>
        public int ActiveLineIndex => activeLineIndex;

        /// <summary>
        /// The first line that has not sealed yet; -1 once every line has sealed. While no line is
        /// active (pre-roll, or the dead zone between a seal and the next line's cue) this is the
        /// UPCOMING line — the one the stage should focus, dimmed, after the boundary scroll.
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
        private readonly int totalTypeableCells;

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
        }

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

                if (activeLineIndex != -1 && !IsLineComplete && !isFinished)
                    activeTimeMs += dt;
            }

            lastUpdateTime = time;

            // (2) Seal, in order, every line whose deadline has passed. Normal lines seal AT
            //     EndTime; lines with a seal grace (vocals overrunning into the next line, or a
            //     boundary-pinned last target) stay typeable through the grace window and seal
            //     early the moment nothing is left to type, so the next line isn't held up.
            while (nextSealIndex < lines.Count && canSeal(lines[nextSealIndex], time))
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
                    activeLineIndex = -1;
                    caretIndex = 0;
                }

                if (broke)
                    ComboBroken?.Invoke();

                LineSealed?.Invoke(new LineSealResult(index, missed, broke));
            }

            // (3) Activate strictly by time: the first unsealed line, while it is judgeable
            //     (ActivationTime <= time < EndTime + grace). ActivationTime is the constant cue
            //     before the first word (CUE_LEAD_MS), not the boundary — crossing a boundary
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

            foreach (var cell in line.Cells)
            {
                if (cell.IsTypeable && cell.State == CellState.Untyped)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Process a lowercased char from KeyCharMap at gameplay time <paramref name="time"/>.
        /// Returns false when inert (no active line / line complete / finished).
        /// A wrong char is REJECTED — never input — but still breaks combo, stays in the
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
                return false; // line complete — wait for the song.

            var cell = line.Cells[caretIndex];
            double delta = time - cell.TargetTime;
            bool matched = Typeability.Fold(c) == Typeability.Fold(cell.Expected);

            if (!matched)
            {
                // Wrong key — REJECTED: no cell mutation, no caret advance, no CharJudged.
                // It still costs: the accuracy denominator, an error, a combo break, and the
                // consecutive-wrong-key streak (the game fails the play when it hits 13).
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
            // points/combo, no timeline sample, and the first judgement stands — otherwise
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

                // Correct char — always accepted; the clock decides the judgement.
                // Premature/Lagging still count as CORRECT keypresses (right char, wrong time).
                correctKeypresses++;

                type = windowsFor(cell).Classify(delta);
                int basePoints = SyncWindows.BasePoints(type);

                if (basePoints > 0)
                {
                    // Multiplier reads combo BEFORE the increment; capped at combo_cap => up to 2.0x.
                    points = (int)Math.Round(basePoints * (1 + Math.Min(combo, combo_cap) / (double)combo_cap));
                    score += points;
                    combo++;
                    maxCombo = Math.Max(maxCombo, combo);
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

            int judgedCellIndex = caretIndex;
            caretIndex++;
            autoSkipForward();

            CharJudged?.Invoke(new CharJudgement(activeLineIndex, judgedCellIndex, type, delta, points, combo));
            return true;
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

        /// <summary>Correct cells (including spaces) across all lines — the WPM numerator source.</summary>
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
