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

        /// <summary>
        /// Literate mod: flatten this line exactly as authored (punctuation + case) instead of into
        /// the default stripped stream, so the nested per-cell scoring objects line up with the
        /// engine's cells INDEX FOR INDEX (a <see cref="Gameplay.CharJudgement"/> is routed by cell
        /// index; a mismatch would silently drop every judgement). Stamped by
        /// <see cref="Mods.TypeBeatModLiterate"/> after conversion and before ApplyDefaults, the one
        /// window in which nested objects have not been built yet. Play-time only, never persisted.
        /// </summary>
        public bool Literate { get; set; }

        /// <summary>
        /// Polyglot mod (backlog 331): flatten this line in its ORIGINAL script instead of its romanised
        /// text (see <see cref="Gameplay.PolyglotLine"/>), for the same index-for-index reason as
        /// <see cref="Literate"/>. Stamped by <see cref="Mods.TypeBeatModPolyglot"/> in the same
        /// window. Play-time only, never persisted.
        /// </summary>
        public bool Polyglot { get; set; }

        /// <summary>
        /// The language the map's originals romanise under, stamped with <see cref="Polyglot"/> so
        /// the nested objects carry the romanised syllable cuts back exactly as the engine does.
        /// </summary>
        public string? PolyglotLanguage { get; set; }

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
            // per-cell target times; the nested objects mirror its typeable cells.
            var typingLine = TypingLine.ForMods(Line, Literate, Polyglot, PolyglotLanguage);

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
                });
            }
        }
    }
}
