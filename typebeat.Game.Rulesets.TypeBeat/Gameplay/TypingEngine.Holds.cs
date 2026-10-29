// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    public sealed partial class TypingEngine
    {
        /// <summary>The hold currently down: its cell, authored span and the time its head was pressed.</summary>
        public readonly record struct ActiveHoldInfo(int LineIndex, int CellIndex, double StartTime, double EndTime, double PressTime);

        /// <summary>The hold the player is in, set by a correct press of a held cell and cleared when its tail is judged.</summary>
        public ActiveHoldInfo? ActiveHold { get; private set; }

        public event Action<HoldTailJudgement>? HoldTailJudged;

        public event Action<HoldTickJudgement>? HoldTickJudged;

        /// <summary>How many of each held cell's ticks are judged so far (ticks are judged in order).</summary>
        private readonly Dictionary<(int LineIndex, int CellIndex), int> ticksJudged = new Dictionary<(int, int), int>();

        /// <summary>Tails already judged, so a retyped head cannot open its hold twice.</summary>
        private readonly HashSet<(int LineIndex, int CellIndex)> judgedTails = new HashSet<(int, int)>();

        /// <summary>The held key went up: judges the active hold's tail. False when no hold is down.</summary>
        public bool ProcessRelease(double time)
        {
            if (ActiveHold is not ActiveHoldInfo hold)
                return false;

            judgeHoldTail(hold, time, HoldTailRule.Grade(hold.StartTime, hold.EndTime, hold.PressTime, time), atSeal: false);
            return true;
        }

        /// <summary>
        /// After a correct press: opens the cell's hold if it has one still to judge, first releasing any
        /// other open hold at the press. A head pressed at or after the hold's end misses its tail on the spot.
        /// </summary>
        private void openHoldIfHeld(int lineIndex, int cellIndex, double time)
        {
            var cell = lines[lineIndex].Cells[cellIndex];

            if (cell.HoldStart is not double start || cell.HoldEnd is not double end || judgedTails.Contains((lineIndex, cellIndex)))
                return;

            // A retype of the open hold's own head leaves it as it is.
            if (ActiveHold is ActiveHoldInfo open && open.LineIndex == lineIndex && open.CellIndex == cellIndex)
                return;

            var hold = new ActiveHoldInfo(lineIndex, cellIndex, start, end, time);

            releaseActiveHoldAt(time);

            if (time >= end)
            {
                judgeHoldTail(hold, time, JudgementType.Miss, atSeal: false);
                return;
            }

            ActiveHold = hold;

            // The head may be pressed after some of its ticks: those count as held.
            creditTicksDueBy(time);
        }

        /// <summary>Whether a press of <paramref name="cellIndex"/> is a hand-off inside its hold's release slack (judged on time).</summary>
        private static bool inHandoffWindow(TypingLine line, int cellIndex, double time)
            => line.Cells[cellIndex].HandsOffFrom is int held
               && line.Cells[held] is { HoldStart: double start, HoldEnd: double end }
               && HoldTailRule.InHandoffWindow(start, end, time);

        /// <summary>
        /// A hand-off pressed with its hold still open (no release frame before it) ends the hold at the
        /// press before the press is scored, exactly as the live key-down release does.
        /// </summary>
        private void closeHoldHandedOff(int lineIndex, int cellIndex, double time)
        {
            if (ActiveHold is ActiveHoldInfo hold && hold.LineIndex == lineIndex && lines[lineIndex].Cells[cellIndex].HandsOffFrom == hold.CellIndex)
                releaseActiveHoldAt(time);
        }

        /// <summary>Whether line <paramref name="index"/> has a hold down, which keeps it from sealing early.</summary>
        private bool holdOpenOn(int index) => ActiveHold?.LineIndex == index;

        /// <summary>A hold still down at its end is Great there.</summary>
        private void completeHoldsDueBy(double time)
        {
            if (ActiveHold is ActiveHoldInfo hold && time >= hold.EndTime)
                judgeHoldTail(hold, hold.EndTime, JudgementType.Great, atSeal: false);
        }

        /// <summary>
        /// Grades a hold still open at a line's hard seal as released then, before the seal's break, so its
        /// credit lands first on every account.
        /// </summary>
        private void closeOpenHoldAtSeal(int lineIndex, double time)
        {
            if (ActiveHold is ActiveHoldInfo hold && hold.LineIndex == lineIndex)
                releaseActiveHoldAt(time);
        }

        /// <summary>Grades the open hold, if any, as released at <paramref name="time"/> (capped at its end).</summary>
        private void releaseActiveHoldAt(double time)
        {
            if (ActiveHold is not ActiveHoldInfo hold)
                return;

            double release = Math.Min(time, hold.EndTime);
            judgeHoldTail(hold, release, HoldTailRule.Grade(hold.StartTime, hold.EndTime, hold.PressTime, release), atSeal: false);
        }

        /// <summary>As line <paramref name="lineIndex"/> seals: every unjudged tail never opened, so it is a neutral Miss.</summary>
        private void sealHolds(int lineIndex)
        {
            var cells = lines[lineIndex].Cells;

            for (int i = 0; i < cells.Count; i++)
            {
                if (cells[i].HoldEnd is double end && !judgedTails.Contains((lineIndex, i)))
                {
                    settleTicks(lineIndex, i, heldUntil: null);

                    judgedTails.Add((lineIndex, i));
                    counts[JudgementType.Miss]++;
                    raise(HoldTailJudged, new HoldTailJudgement(lineIndex, i, JudgementType.Miss, end, combo, AtSeal: true));
                }
            }
        }

        private void judgeHoldTail(ActiveHoldInfo hold, double release, JudgementType type, bool atSeal)
        {
            bool wasOpen = ActiveHold == hold;

            if (wasOpen)
                ActiveHold = null;

            // Ticks first, so their credit or break precedes the tail's.
            settleTicks(hold.LineIndex, hold.CellIndex, wasOpen ? release : null);

            judgedTails.Add((hold.LineIndex, hold.CellIndex));

            // A Great, Ok or Meh tail is a hit and credits combo; a Miss breaks it.
            if (type is JudgementType.Great or JudgementType.Ok or JudgementType.Meh)
            {
                creditCombo(hold.LineIndex, hold.CellIndex);
            }
            else
            {
                breakRun();
                discardRestorableStreak();
                raise(ComboBroken);
            }

            counts[type]++;
            raise(HoldTailJudged, new HoldTailJudgement(hold.LineIndex, hold.CellIndex, type, release, combo, atSeal));
        }

        /// <summary>Every tick of the open hold due by <paramref name="time"/> is a hit.</summary>
        private void creditTicksDueBy(double time)
        {
            if (ActiveHold is not ActiveHoldInfo hold || lines[hold.LineIndex].Cells[hold.CellIndex].HoldTickTimes is not { } ticks)
                return;

            for (int next = ticksJudged.GetValueOrDefault((hold.LineIndex, hold.CellIndex)); next < ticks.Count && ticks[next] <= time; next++)
                judgeTick(hold.LineIndex, hold.CellIndex, next, ticks[next], hit: true, neutral: false, breaks: false);
        }

        /// <summary>
        /// Settles a hold's remaining ticks: hits up to <paramref name="heldUntil"/>, then misses (only the first
        /// breaks combo). With null (the hold never opened) every tick is a neutral miss.
        /// </summary>
        private void settleTicks(int lineIndex, int cellIndex, double? heldUntil)
        {
            if (lines[lineIndex].Cells[cellIndex].HoldTickTimes is not { } ticks)
                return;

            bool broke = false;

            for (int next = ticksJudged.GetValueOrDefault((lineIndex, cellIndex)); next < ticks.Count; next++)
            {
                if (heldUntil is not double held)
                    judgeTick(lineIndex, cellIndex, next, ticks[next], hit: false, neutral: true, breaks: false);
                else if (ticks[next] <= held)
                    judgeTick(lineIndex, cellIndex, next, ticks[next], hit: true, neutral: false, breaks: false);
                else
                {
                    judgeTick(lineIndex, cellIndex, next, ticks[next], hit: false, neutral: false, breaks: !broke);
                    broke = true;
                }
            }
        }

        private void judgeTick(int lineIndex, int cellIndex, int tickIndex, double time, bool hit, bool neutral, bool breaks)
        {
            ticksJudged[(lineIndex, cellIndex)] = tickIndex + 1;

            if (hit)
            {
                creditCombo(lineIndex, cellIndex);
            }
            else if (breaks)
            {
                breakRun();
                discardRestorableStreak();
                raise(ComboBroken);
            }

            raise(HoldTickJudged, new HoldTickJudgement(lineIndex, cellIndex, tickIndex, hit, time, combo, neutral));
        }

        private void resetHolds()
        {
            ActiveHold = null;
            judgedTails.Clear();
            ticksJudged.Clear();
        }
    }
}
