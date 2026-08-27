// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Replays;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;

namespace typebeat.Game.Rulesets.TypeBeat.Replays
{
    /// <summary>
    /// Generates a perfect play: every typeable cell's expected character pressed at the moment
    /// that cell judges delta 0 under the era the play is graded in, using the same
    /// <see cref="TypingLine.FromLyricLine"/> flattening the engine itself is built from, so the
    /// frames line up with the engine's cells by construction. The literate flag must match the
    /// play's mods, because Literate changes which cells exist at all (punctuation becomes typed)
    /// and the case they are typed in; without it case is emitted as authored anyway and simply
    /// folded away.
    ///
    /// Times are clamped into each line's typeable window (never before its activation, never past
    /// its seal deadline) and kept monotonic, then rounded to integral milliseconds like recorded
    /// input so the frames survive .osr encoding untouched. The rounding is re-clamped against the
    /// window afterwards, because rounding a fractional activation DOWN would put the press on the
    /// wrong side of a line boundary (see <see cref="GenerateFrames"/>).
    /// </summary>
    public class TypeBeatAutoGenerator : AutoGenerator<TypeBeatReplayFrame>
    {
        /// <summary>Safety margin kept before a line's force-seal deadline for clamped presses.</summary>
        private const double seal_margin_ms = 10;

        private readonly bool literate;
        private readonly bool syllableTiming;
        private readonly bool charTimedStretch;

        /// <param name="beatmap">The map to perfect.</param>
        /// <param name="literate">
        /// Whether the play is under the Literate mod, which changes the cell list itself.
        /// </param>
        /// <param name="syllableTiming">
        /// Which judgement ERA the generated presses must be perfect under, era-styled exactly like
        /// <see cref="Gameplay.TypingEngine.SyllableTiming"/> and defaulting to the same CLASSIC
        /// era, so a bare construction keeps the pre-backlog-181 frames (every press on its cell's
        /// own point target). Set it to match the engine that will grade the replay: live play is
        /// span-judged for every mod stack but Hard Rock
        /// (<c>DrawableTypeBeatRuleset.createEngine</c>), and
        /// <see cref="Mods.TypeBeatModAutoplay.CreateReplayData"/> mirrors that condition.
        /// </param>
        /// <param name="charTimedStretch">
        /// Whether the grading engine narrows that span rule for STRETCH cells (backlog 209, see
        /// <see cref="Gameplay.TypingEngine.CharTimedStretch"/>), era-styled the same way and
        /// defaulting to the same OLD era. Inert unless <paramref name="syllableTiming"/> is set,
        /// because a classic engine already presses and judges every cell on its point target.
        /// </param>
        public TypeBeatAutoGenerator(IBeatmap beatmap, bool literate = false, bool syllableTiming = false, bool charTimedStretch = false)
            : base(beatmap)
        {
            this.literate = literate;
            this.syllableTiming = syllableTiming;
            this.charTimedStretch = charTimedStretch;
        }

        protected override void GenerateFrames()
        {
            // Mirror DrawableTypeBeatRuleset.createEngine: engine line order is LineIndex order.
            var lineObjects = Beatmap.HitObjects.OfType<TypeBeatHitObject>().OrderBy(h => h.LineIndex).ToList();

            if (lineObjects.Count == 0)
                return;

            TimingGranularity granularity = lineObjects[0].Granularity;

            double lastTime = double.NegativeInfinity;

            foreach (var lineObject in lineObjects)
            {
                var line = TypingLine.FromLyricLine(lineObject.Line, granularity, literate);

                // The line is typeable in [ActivationTime, EndTime + SealGraceMs); keep a margin
                // before the deadline so a boundary-pinned target is still pressed while typeable.
                double windowStart = line.ActivationTime;
                double windowEnd = Math.Max(windowStart, line.EndTime + line.SealGraceMs - seal_margin_ms);

                // Integral millisecond at which the line is genuinely open. Real maps carry
                // fractional times, and a line's activation is routinely its exact StartTime (which
                // IS the previous line's EndTime, the decoder makes the windows contiguous). Rounding
                // such a target DOWN, even by a fraction of a millisecond, moves the press to the
                // wrong side of that boundary: the previous line has not sealed yet, so it is still
                // the active one, it is already fully typed, and TypingEngine.ProcessKey answers a
                // complete line by doing nothing at all. The press vanishes with no judgement and no
                // rejection, and every remaining press on the new line lands one cell early. On an
                // all-freestyle line the drift is invisible (any key matches) right up to the first
                // space cell, where autoplay's space hits a freestyle cell (which rejects space) and
                // its letters then hit the space cell, rejected over and over until the mash guard
                // fails the play. So round UP to the open, never past it.
                double openMs = Math.Ceiling(windowStart);

                for (int i = 0; i < line.Cells.Count; i++)
                {
                    var cell = line.Cells[i];

                    if (!cell.IsTypeable)
                        continue;

                    double time = Math.Clamp(perfectTimeFor(line, i), windowStart, windowEnd);

                    // Monotonic, integral (matches live capture; lossless in .osr).
                    time = Math.Max(Math.Round(Math.Max(time, lastTime)), openMs);
                    lastTime = time;

                    // A freestyle cell accepts any key but space, so the perfect play presses a
                    // fixed letter rather than the authoring marker (which would be rejected, as a
                    // space would); the frame stays inside the a-z surface the .osr mapping is
                    // pinned on, and the marker never reaches the display as a "typed" char.
                    Frames.Add(new TypeBeatReplayFrame(time, cell.IsFreestyle ? Typeability.FREESTYLE_AUTO_CHAR : cell.Expected));
                }
            }
        }

        /// <summary>
        /// The instant cell <paramref name="cellIndex"/> is judged delta 0 at, before the window,
        /// monotonic and rounding machinery in <see cref="GenerateFrames"/> gets to it.
        ///
        /// <para>Classic era: the cell's own point target, which is what
        /// <c>TypingEngine.judgedDeltaFor</c> measures against. Under
        /// <see cref="syllableTiming"/> a cell inside a syllable group is measured against that
        /// group's sung SPAN instead, so the perfect instant is the target CLAMPED into
        /// [<see cref="SyllableGroup.StartTime"/>, <see cref="SyllableGroup.EndTime"/>]: inside the
        /// span the target is already perfect and nothing moves, outside it the nearer edge is the
        /// only choice that judges 0. The clamp is not cosmetic. Under mapper subtimings a cell's
        /// flat-ramp target routinely sits OUTSIDE its own syllable's span, because the target
        /// spread walks the characters evenly BY INDEX across the mapper's segments while the
        /// groups are cut where the <see cref="Gameplay.Syllabifier"/> says the syllable breaks,
        /// and the two disagree (see <c>TypingLine.buildSyllables</c>). Backlog 179 assumed that
        /// gap was always small enough to stay Great; on a real map it reached the Ok window and
        /// autoplay scored 99.23%.</para>
        ///
        /// <para>A cell in NO group keeps its point target under both eras, because that is exactly
        /// what the engine keeps judging it on: space cells, lines with no groups, and every cell
        /// of a stylised token the syllabifier refuses (backlog 178). Under
        /// <see cref="charTimedStretch"/> a STRETCH cell (a freestyle slot, or a cell of a run of
        /// three or more identical characters inside one syllable) keeps it too, for the same
        /// reason: the engine reverts exactly those to their character targets, so clamping them
        /// into their span would press the edge of a window that is no longer being measured and
        /// hand autoplay the very off-target press the narrowing exists to price.</para>
        ///
        /// <para>The integral rounding applied afterwards can still step off a FRACTIONAL span edge
        /// by up to half a millisecond, which is left alone deliberately: rounding into the span
        /// instead is not always possible (a span shorter than a millisecond, or one whose edges
        /// round to the same integer, has no integral instant inside it at all), and half a
        /// millisecond against the tightest Great window in the game (112.5 ms, Syllable
        /// granularity) is not a judgement anyone can lose.</para>
        /// </summary>
        private double perfectTimeFor(TypingLine line, int cellIndex)
        {
            double target = line.Cells[cellIndex].TargetTime;

            if (!syllableTiming)
                return target;

            int syllable = line.SyllableIndexOf(cellIndex);

            if (syllable < 0)
                return target;

            if (charTimedStretch && line.IsCharTimedStretch(cellIndex))
                return target;

            var group = line.Syllables[syllable];

            return Math.Clamp(target, group.StartTime, group.EndTime);
        }
    }
}
