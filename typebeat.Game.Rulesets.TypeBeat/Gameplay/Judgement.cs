// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Ported verbatim from type!beat TypeBeat.Game/Gameplay/Judgement.cs (regression-anchored).
// This file is the SINGLE tuning point for all judgement window constants.
// Pure C#: no osu.Framework dependencies. All times are double milliseconds.
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
    /// <para>The three QUALITY tiers are named for the osu <see cref="Rulesets.Scoring.HitResult"/>
    /// they map to, and <see cref="TypeBeat.Scoring.TypeBeatResultMapping.CellResult"/> is the
    /// identity on them. That naming is the one thing backlog 147 kept from the character-distance
    /// arc it otherwise reverted: before backlog 133 the two vocabularies disagreed (engine Perfect
    /// meant osu Great, engine Good meant osu Ok, engine Ok meant osu Meh), so the word "Perfect"
    /// meant two different things depending on which side of the mapping you were reading. Nothing
    /// persists this enum, by name or by ordinal, so the alignment costs nothing: it exists in
    /// memory during a play, a stored score carries osu results, and a replay carries KEYSTROKES
    /// and is re-judged from scratch (see
    /// <see cref="TypeBeat.Scoring.TypeBeatReplayScorer"/>).</para>
    /// </summary>
    public enum JudgementType
    {
        /// <summary>Inside the tightest window. osu <see cref="Rulesets.Scoring.HitResult.Great"/>.</summary>
        Great,

        /// <summary>osu <see cref="Rulesets.Scoring.HitResult.Ok"/>.</summary>
        Ok,

        /// <summary>The widest window a correct keypress can still score in. osu <see cref="Rulesets.Scoring.HitResult.Meh"/>.</summary>
        Meh,

        /// <summary>Right character, pressed too EARLY to score.</summary>
        Premature,

        /// <summary>Right character, pressed too LATE to score.</summary>
        Lagging,

        WrongChar,
        Miss
    }

    public sealed class SyncWindows
    {
        public const double LEAD_IN_MS = 2000;

        /// <summary>
        /// Aligner word confidence below this judges the word's cells at Line-granularity
        /// windows: the least reliable timing gets the widest tolerance, never the tightest.
        /// </summary>
        public const double LOW_CONFIDENCE_SCORE = 0.15;

        // Base (Line-granularity) window constants: the one tuning point. MILLISECONDS between the
        // keypress and the cell's TargetTime, late-biased 1.6x on every tier.
        //
        // Backlog 133 replaced this ladder with a CHARACTER-DISTANCE one in four tiers, and backlog
        // 147 put it back exactly as it was, three tiers on these six constants. The character axis
        // measured how far the press was from the character the playhead was on, which capped how
        // far AHEAD a player could press at a fixed number of characters however slow the map, and
        // it valued the top tier at 200 where this ladder values it at 300. Both had to go together:
        // a four-tier millisecond ladder would still have valued every stored row's top tier at 200
        // where it was submitted at 300.
        private const double base_great_early = 250;
        private const double base_great_late = 400;
        private const double base_ok_early = 600;
        private const double base_ok_late = 1000;
        private const double base_meh_early = 1200;
        private const double base_meh_late = 2000;

        private static readonly SyncWindows line_windows = new SyncWindows(1.0);
        private static readonly SyncWindows word_windows = new SyncWindows(0.6);
        private static readonly SyncWindows syllable_windows = new SyncWindows(0.45);

        public static SyncWindows For(TimingGranularity granularity)
        {
            switch (granularity)
            {
                case TimingGranularity.Word:
                    return word_windows;

                case TimingGranularity.Syllable:
                    return syllable_windows;

                default:
                    return line_windows;
            }
        }

        public double Scale { get; }

        public double GreatEarly { get; }
        public double GreatLate { get; }
        public double OkEarly { get; }
        public double OkLate { get; }
        public double MehEarly { get; }
        public double MehLate { get; }

        private SyncWindows(double scale)
        {
            Scale = scale;
            GreatEarly = base_great_early * scale;
            GreatLate = base_great_late * scale;
            OkEarly = base_ok_early * scale;
            OkLate = base_ok_late * scale;
            MehEarly = base_meh_early * scale;
            MehLate = base_meh_late * scale;
        }

        /// <summary>
        /// Classify a correct keypress's delta (keypress time - cell target time; negative = early).
        /// Nested asymmetric ranges, tested Great -&gt; Ok -&gt; Meh; outside Meh the sign decides
        /// Premature (too early) vs Lagging (too late).
        ///
        /// <para>A SPACE never arrives here with a real delta: backlog 148 took the spacebar out of
        /// the timing challenge, so <see cref="TypingEngine.ProcessKey"/> zeroes the delta of a space
        /// typed on a space cell before it is classified (and before it is stored for the sync
        /// readouts). This ladder therefore only ever grades lyric characters, and the exemption is
        /// the engine's rule rather than a window constant, so it is not tuned from here.</para>
        /// </summary>
        public JudgementType Classify(double delta)
        {
            if (delta >= -GreatEarly && delta <= GreatLate)
                return JudgementType.Great;

            if (delta >= -OkEarly && delta <= OkLate)
                return JudgementType.Ok;

            if (delta >= -MehEarly && delta <= MehLate)
                return JudgementType.Meh;

            return delta < -MehEarly ? JudgementType.Premature : JudgementType.Lagging;
        }

        /// <summary>
        /// Asymmetric sync quality in [0, 1]: q = clamp(1 - (delta &lt; 0 ? -delta/MehEarly : delta/MehLate), 0, 1).
        /// </summary>
        public double SyncQuality(double delta)
            => Math.Clamp(1 - (delta < 0 ? -delta / MehEarly : delta / MehLate), 0, 1);

        public static int BasePoints(JudgementType type)
        {
            switch (type)
            {
                case JudgementType.Great:
                    return 300;

                case JudgementType.Ok:
                    return 150;

                case JudgementType.Meh:
                    return 50;

                default:
                    return 0;
            }
        }
    }

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

        /// <summary>All 7 <see cref="JudgementType"/> keys always present.</summary>
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
