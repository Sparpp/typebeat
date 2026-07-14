// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Threading;
using typebeat.Game.Rulesets.Objects;
using typebeat.Game.Rulesets.Objects.Types;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Judgements;

namespace typebeat.Game.Rulesets.TypeBeat.Objects
{
    /// <summary>
    /// One hit object per lyric line. Carries the full <see cref="LyricLine"/> payload; nested
    /// <see cref="TypeBeatCharObject"/>s (one per typeable cell, flattened by the regression-anchored
    /// <see cref="TypingLine"/> logic) provide osu scoring granularity. The line object itself is
    /// scoring-inert (<see cref="TypeBeatLineJudgement"/>), resolved when the engine seals the line.
    /// </summary>
    public class TypeBeatHitObject : HitObject, IHasDuration
    {
        /// <summary>Typing order position of this line in the beatmap (lines seal strictly in this order).</summary>
        public int LineIndex { get; set; }

        /// <summary>The line payload; times are absolute milliseconds (StartTime == Line.StartTime).</summary>
        public required LyricLine Line { get; set; }

        /// <summary>Beatmap-wide timing granularity, replicated per object so the beatmap round-trips it.</summary>
        public TimingGranularity Granularity { get; set; }

        /// <summary>The line remains typeable until EndTime + SealGraceMs; osu sees that as the object's end.</summary>
        public double EndTime => Line.EndTime + Line.SealGraceMs;

        public double Duration
        {
            get => EndTime - StartTime;
            set { } // fixed by the lyric data; required by IHasDuration.
        }

        public override Rulesets.Judgements.Judgement CreateJudgement() => new TypeBeatLineJudgement();

        protected override HitWindows CreateHitWindows() => HitWindows.Empty;

        protected override void CreateNestedHitObjects(CancellationToken cancellationToken)
        {
            base.CreateNestedHitObjects(cancellationToken);

            // The engine's flattening (TypingLine.FromLyricLine) is the single source of truth for
            // per-cell target times and judge tiers; the nested objects mirror its typeable cells.
            var typingLine = TypingLine.FromLyricLine(Line, Granularity);

            for (int i = 0; i < typingLine.Cells.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var cell = typingLine.Cells[i];

                if (!cell.IsTypeable)
                    continue;

                AddNested(new TypeBeatCharObject
                {
                    StartTime = cell.TargetTime,
                    LineIndex = LineIndex,
                    CellIndex = i,
                    Expected = cell.Expected,
                    JudgeGranularity = cell.JudgeGranularity,
                });
            }
        }
    }
}
