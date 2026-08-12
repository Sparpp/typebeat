// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Ported verbatim from type!beat TypeBeat.Game/Gameplay/Judgement.cs (regression-anchored).
// This file is the SINGLE tuning point for all judgement window constants.
// Pure C#: no osu.Framework dependencies. All TIMES are double milliseconds; judgement OFFSETS
// are in whatever SyncMeasure the play uses (character distances by default).
// Renames on entry: public constants restyled to ALL_UPPER per fork naming rules.
// No type here collides with typebeat.Game.Rulesets.Judgements.Judgement (no type named "Judgement").

using System;
using System.Collections.Generic;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    /// <summary>
    /// What one keypress resolved its cell as.
    ///
    /// <para>The four QUALITY tiers are named for the osu <see cref="Rulesets.Scoring.HitResult"/> they map
    /// to, and <see cref="TypeBeat.Scoring.TypeBeatResultMapping.CellResult"/> is the identity on them
    /// (backlog 133). Before that the two vocabularies disagreed (engine Perfect meant osu Great,
    /// engine Good meant osu Ok, engine Ok meant osu Meh), so the word "Perfect" meant two different
    /// things depending on which side of the mapping you were reading, and a fourth tier would have
    /// made that worse rather than better. Nothing persists this enum, by name or by ordinal, so the
    /// realignment could be made rather than only wished for: it exists in memory during a play, and
    /// a stored score carries osu results, while a replay carries KEYSTROKES and is re-judged from
    /// scratch (see <see cref="TypeBeat.Scoring.TypeBeatReplayScorer"/>).</para>
    /// </summary>
    public enum JudgementType
    {
        /// <summary>Inside the tightest window. osu <see cref="Rulesets.Scoring.HitResult.Perfect"/>.</summary>
        Perfect,

        /// <summary>osu <see cref="Rulesets.Scoring.HitResult.Great"/>.</summary>
        Great,

        /// <summary>osu <see cref="Rulesets.Scoring.HitResult.Ok"/>.</summary>
        Ok,

        /// <summary>The widest window a correct keypress can still score in. osu <see cref="Rulesets.Scoring.HitResult.Meh"/>.</summary>
        Meh,

        /// <summary>Right character, too far AHEAD of the playhead to score.</summary>
        Premature,

        /// <summary>Right character, too far BEHIND the playhead to score.</summary>
        Lagging,

        WrongChar,
        Miss
    }

    /// <summary>
    /// What a keypress's offset from its cell is MEASURED IN, and therefore what unit
    /// <see cref="SyncWindows"/>' windows are expressed in.
    /// </summary>
    public enum SyncMeasure
    {
        /// <summary>
        /// The live rule (backlog 133): how many CHARACTERS the keypress is from the character the
        /// playhead is on, fractionally (see <see cref="TypingLine.CharacterDistanceAt"/>). Negative
        /// is ahead of the playhead.
        /// </summary>
        CharacterDistance,

        /// <summary>
        /// The rule up to backlog 133: milliseconds between the keypress and the cell's
        /// <see cref="TypingCell.TargetTime"/>. Negative is early. Nothing selects this yet; it is
        /// kept live for the Rhythmic mod (backlog 135), which turns it back on by setting
        /// <see cref="TypingEngine.Measure"/>.
        /// </summary>
        Milliseconds,
    }

    public sealed class SyncWindows
    {
        public const double LEAD_IN_MS = 2000;

        /// <summary>
        /// Aligner word confidence below this judges the word's cells at Line-granularity
        /// windows: the least reliable timing gets the widest tolerance, never the tightest.
        /// </summary>
        public const double LOW_CONFIDENCE_SCORE = 0.15;

        // Base (Line-granularity) window constants: the one tuning point. TWO ladders, one per
        // SyncMeasure, because a window is a distance in whatever the offset is measured in and the
        // two measures do not share a unit.
        //
        // CHARACTER DISTANCE (the live ladder, backlog 133). A geometric ladder: every tier is
        // exactly 1.6x late-biased, which is what today's 250/400 millisecond pair was, and exactly
        // double the tier inside it. Wherever cell spacing is locally uniform a character distance
        // reduces to the millisecond delta divided by that spacing, which is the whole point: per-
        // character targets are already interpolated (TypingLine.FromLyricLine), so "characters
        // behind the playhead" and "milliseconds off target" were always the same axis, and this
        // rescales it to the map's own pace rather than measuring something new.
        private const double base_perfect_early = 1.25;
        private const double base_perfect_late = 2.00;
        private const double base_great_early = 2.50;
        private const double base_great_late = 4.00;
        private const double base_ok_early = 5.00;
        private const double base_ok_late = 8.00;
        private const double base_meh_early = 10.00;
        private const double base_meh_late = 16.00;

        // MILLISECONDS (backlog 135's Rhythmic mod). The Great/Ok/Meh rows are EXACTLY the windows
        // this game judged in up to backlog 133 (they were then called Perfect/Good/Ok and mapped
        // onto those same three osu results), so selecting this measure reproduces the old game
        // rather than approximating it. The fourth tier is new, and it subdivides the TOP of the
        // ladder at the same halving the character ladder uses, so nothing that used to be a Great
        // becomes anything worse.
        private const double ms_perfect_early = 125;
        private const double ms_perfect_late = 200;
        private const double ms_great_early = 250;
        private const double ms_great_late = 400;
        private const double ms_ok_early = 600;
        private const double ms_ok_late = 1000;
        private const double ms_meh_early = 1200;
        private const double ms_meh_late = 2000;

        // Granularity scales: unreliable timing gets the widest tolerance, never the tightest.
        private const double line_scale = 1.0;
        private const double word_scale = 0.6;
        private const double syllable_scale = 0.45;

        private static readonly SyncWindows character_line = new SyncWindows(SyncMeasure.CharacterDistance, line_scale);
        private static readonly SyncWindows character_word = new SyncWindows(SyncMeasure.CharacterDistance, word_scale);
        private static readonly SyncWindows character_syllable = new SyncWindows(SyncMeasure.CharacterDistance, syllable_scale);

        private static readonly SyncWindows millisecond_line = new SyncWindows(SyncMeasure.Milliseconds, line_scale);
        private static readonly SyncWindows millisecond_word = new SyncWindows(SyncMeasure.Milliseconds, word_scale);
        private static readonly SyncWindows millisecond_syllable = new SyncWindows(SyncMeasure.Milliseconds, syllable_scale);

        public static SyncWindows For(TimingGranularity granularity, SyncMeasure measure = SyncMeasure.CharacterDistance)
        {
            bool milliseconds = measure == SyncMeasure.Milliseconds;

            switch (granularity)
            {
                case TimingGranularity.Word:
                    return milliseconds ? millisecond_word : character_word;

                case TimingGranularity.Syllable:
                    return milliseconds ? millisecond_syllable : character_syllable;

                default:
                    return milliseconds ? millisecond_line : character_line;
            }
        }

        public SyncMeasure Measure { get; }

        public double Scale { get; }

        public double PerfectEarly { get; }
        public double PerfectLate { get; }
        public double GreatEarly { get; }
        public double GreatLate { get; }
        public double OkEarly { get; }
        public double OkLate { get; }
        public double MehEarly { get; }
        public double MehLate { get; }

        private SyncWindows(SyncMeasure measure, double scale)
        {
            Measure = measure;
            Scale = scale;

            bool milliseconds = measure == SyncMeasure.Milliseconds;

            PerfectEarly = (milliseconds ? ms_perfect_early : base_perfect_early) * scale;
            PerfectLate = (milliseconds ? ms_perfect_late : base_perfect_late) * scale;
            GreatEarly = (milliseconds ? ms_great_early : base_great_early) * scale;
            GreatLate = (milliseconds ? ms_great_late : base_great_late) * scale;
            OkEarly = (milliseconds ? ms_ok_early : base_ok_early) * scale;
            OkLate = (milliseconds ? ms_ok_late : base_ok_late) * scale;
            MehEarly = (milliseconds ? ms_meh_early : base_meh_early) * scale;
            MehLate = (milliseconds ? ms_meh_late : base_meh_late) * scale;
        }

        /// <summary>
        /// Classify a correct keypress's offset in this window set's <see cref="Measure"/>: how many
        /// characters the press is from the character the playhead is on (negative = ahead of it),
        /// or, under <see cref="SyncMeasure.Milliseconds"/>, keypress time minus cell target time.
        /// Nested asymmetric ranges, tested Perfect -&gt; Great -&gt; Ok -&gt; Meh; outside Meh the
        /// sign decides Premature (too far ahead) vs Lagging (too far behind).
        /// </summary>
        public JudgementType Classify(double offset)
        {
            if (offset >= -PerfectEarly && offset <= PerfectLate)
                return JudgementType.Perfect;

            if (offset >= -GreatEarly && offset <= GreatLate)
                return JudgementType.Great;

            if (offset >= -OkEarly && offset <= OkLate)
                return JudgementType.Ok;

            if (offset >= -MehEarly && offset <= MehLate)
                return JudgementType.Meh;

            return offset < -MehEarly ? JudgementType.Premature : JudgementType.Lagging;
        }

        /// <summary>
        /// Asymmetric sync quality in [0, 1] over the WIDEST scoring window:
        /// q = clamp(1 - (offset &lt; 0 ? -offset/MehEarly : offset/MehLate), 0, 1). Exactly 1 dead on
        /// the playhead and exactly 0 at the edges of the Meh window, so every offset a correct
        /// keypress can still score at maps somewhere inside the ramp and everything beyond it
        /// (Premature / Lagging) sits on the floor.
        /// </summary>
        public double SyncQuality(double offset)
            => Math.Clamp(1 - (offset < 0 ? -offset / MehEarly : offset / MehLate), 0, 1);

        /// <summary>
        /// The engine's own per-character points, matching the osu base score each tier's result
        /// carries (see <see cref="TypeBeat.Scoring.TypeBeatScoreProcessor.GetBaseScoreForResult"/>), so the
        /// engine's running score and the submitted one grade a keypress the same way.
        /// </summary>
        public static int BasePoints(JudgementType type)
        {
            switch (type)
            {
                case JudgementType.Perfect:
                    return 300;

                case JudgementType.Great:
                    return 200;

                case JudgementType.Ok:
                    return 100;

                case JudgementType.Meh:
                    return 50;

                default:
                    return 0;
            }
        }
    }

    /// <summary>
    /// One resolved keypress. <paramref name="Delta"/> is the signed lead/lag in MILLISECONDS
    /// (keypress time minus the cell's target), and stays milliseconds whatever
    /// <see cref="SyncMeasure"/> the play is judged under: it is the honest read-out of when the
    /// press happened, which is what a timing display wants. Since backlog 133 it is NOT what
    /// <paramref name="Type"/> was derived from under the default measure; that is the character
    /// distance, and it is kept on the cell (<see cref="TypingCell.JudgedOffset"/>).
    /// </summary>
    public readonly record struct CharJudgement(int LineIndex, int CellIndex, JudgementType Type, double Delta, int PointsAwarded, int ComboAfter);

    public readonly record struct LineSealResult(int LineIndex, int MissedCells, bool ComboBroken);

    public readonly record struct SyncSample(double Time, double Delta);

    public sealed class ResultsSummary
    {
        public required long Score { get; init; }

        /// <summary>0..1.</summary>
        public required double Accuracy { get; init; }

        public required double Wpm { get; init; }

        /// <summary>0..100.</summary>
        public required double SyncPercent { get; init; }

        public required int MaxCombo { get; init; }

        /// <summary>All 8 <see cref="JudgementType"/> keys always present.</summary>
        public required IReadOnlyDictionary<JudgementType, int> Counts { get; init; }

        public required IReadOnlyList<SyncSample> SyncTimeline { get; init; }

        public required string Artist { get; init; }

        public required string Title { get; init; }

        /// <summary>Both thresholds required: S >=95 sync &amp;&amp; >=0.95 acc; A 90/0.90; B 80/0.80; C 65/0.65; else D.</summary>
        public string Grade
        {
            get
            {
                if (SyncPercent >= 95 && Accuracy >= 0.95) return "S";
                if (SyncPercent >= 90 && Accuracy >= 0.90) return "A";
                if (SyncPercent >= 80 && Accuracy >= 0.80) return "B";
                if (SyncPercent >= 65 && Accuracy >= 0.65) return "C";

                return "D";
            }
        }
    }
}
