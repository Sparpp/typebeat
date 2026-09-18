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
        Miss,

        /// <summary>
        /// A cell a word skip ABANDONED (backlog 167): announced so the stage repaints the character
        /// and so the break the skip took is visible at the moment it is taken, and resolving
        /// NOTHING, exactly as <see cref="WrongChar"/> resolves nothing
        /// (<see cref="TypeBeat.Scoring.TypeBeatResultMapping.CellResult"/> answers null for both).
        /// The cell is re-typeable until its line seals, and applying a result now is precisely what
        /// would make re-earning it impossible: a cell takes only its FIRST result.
        ///
        /// <para>Its <c>Counts</c> entry therefore stays at zero for the whole of a run. The miss a
        /// never-reclaimed skip finally costs is counted at the SEAL, as an ordinary
        /// <see cref="Miss"/>, for the same reason the result is: until the line runs out of time
        /// nobody can say whether the character was given up or merely deferred.</para>
        /// </summary>
        Abandoned
    }

    /// <summary>
    /// The typeable cells one word skip left in <see cref="CellState.Abandoned"/>, or the ones a
    /// single event has just taken back out of it (see <see cref="TypingEngine.WordAbandoned"/>,
    /// <see cref="TypingEngine.AbandonReclaimed"/> and <see cref="TypingEngine.AbandonSealed"/>).
    /// Carried as the cell INDICES rather than a bare count because the seal's consumer has to
    /// address the cells one by one, and as one payload because every consumer of all three events
    /// prices the whole group at once.
    /// </summary>
    public readonly record struct AbandonedCells(int LineIndex, IReadOnlyList<int> CellIndices)
    {
        public int Count => CellIndices.Count;
    }

    public sealed class SyncWindows
    {
        public const double LEAD_IN_MS = 2000;

        // ONE LADDER FOR EVERY CELL: the one tuning point. MILLISECONDS between the keypress and the
        // cell's TargetTime, SYMMETRIC around the target.
        //
        // This replaced a three-tier ladder (Line 250/400, Word 150/240, Syllable 112.5/180, all
        // late-biased 1.6x) that scaled the same six numbers by 1.0 / 0.6 / 0.45 and chose a tier
        // per cell from the beatmap's granularity, falling back to the widest tier for estimated
        // lines and low-confidence words. That is gone: the timing data a map carries still varies
        // (a line may have whole-word spans or author subdivisions), but the JUDGEMENT no longer
        // does, so unreliable timing buys no extra tolerance and a well-subdivided map is not
        // judged more tightly than a coarse one. The windows are now symmetric, which retires the
        // 0.625 early:late ratio the ladder used to share across its tiers.
        //
        // A retune of these three numbers is NOT an era: no CONFIG bit records which ladder a run
        // was graded on, so a stored replay re-derives on the ladder that ships today, exactly as a
        // scoring change to any other tuning point would. The era bits around it (bit 13's halving,
        // bit 8's FirstCharTiming) are rules that survive a retune and multiply or narrow whatever
        // ladder is here.
        private const double great_window_ms = 150;
        private const double ok_window_ms = 300;
        private const double meh_window_ms = 600;

        /// <summary>
        /// The ladder, and the only one: every cell of every map is judged on it, whatever timing
        /// granularity the map was authored at. A mod that widens or tightens the windows does NOT
        /// get a cache of its own here (see <see cref="Scaled"/>).
        /// </summary>
        public static SyncWindows Default { get; } = new SyncWindows(1.0);

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
            GreatEarly = great_window_ms * scale;
            GreatLate = great_window_ms * scale;
            OkEarly = ok_window_ms * scale;
            OkLate = ok_window_ms * scale;
            MehEarly = meh_window_ms * scale;
            MehLate = meh_window_ms * scale;
        }

        /// <summary>
        /// This ladder with every bound multiplied by <paramref name="factor"/>. A GENERAL
        /// multiplicative window scale, deliberately not named after any one mod: the Easy mod
        /// doubles the windows through it, and anything else that widens or tightens them (a rate
        /// mod scaling them by the clock rate, say) multiplies its own factor in on top, so two of
        /// them compose by multiplication instead of one overwriting the other.
        ///
        /// <para>Every bound is <c>window * Scale</c>, so scaling the ladder is exactly constructing
        /// it at <c>Scale * factor</c>. That is why there is no second cache: <see cref="Default"/>
        /// is the LADDER, and a scale is a number the ENGINE holds (<c>TypingEngine.WindowScale</c>),
        /// applied once when it is set rather than per keypress.</para>
        ///
        /// <para>A factor of exactly 1 returns this same instance, so the unmodded path allocates
        /// nothing and keeps grading against the very objects it graded against before the scale
        /// existed.</para>
        /// </summary>
        public SyncWindows Scaled(double factor)
        {
            if (!double.IsFinite(factor) || factor <= 0)
                throw new ArgumentOutOfRangeException(nameof(factor), factor, "A judgement window scale must be finite and positive.");

            return factor == 1 ? this : new SyncWindows(Scale * factor);
        }

        /// <summary>
        /// Classify a correct keypress's delta (keypress time - cell target time; negative = early).
        /// Nested ranges, tested Great -&gt; Ok -&gt; Meh; outside Meh the sign decides
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
        /// Sync quality in [0, 1]: q = clamp(1 - (delta &lt; 0 ? -delta/MehEarly : delta/MehLate), 0, 1).
        /// Symmetric with the windows themselves, so the two edges read the same for anyone who
        /// measures the readout by hand.
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

    /// <summary>
    /// One sealed line. <paramref name="ComboBroken"/> says the line ran out of time on at least one
    /// cell nobody typed, which is the seal's one combo break;
    /// <paramref name="SurvivingCombo"/> is the run the engine is left holding AFTER that break, i.e.
    /// the increments earned strictly past the line's LAST such cell, which survive it
    /// (<see cref="TypingEngine.BackDatedSealBreak"/>). It is 0 under the classic era, where the
    /// break wipes the whole run, and 0 whenever nothing survived, so a consumer reads it only
    /// alongside <paramref name="ComboBroken"/>. It exists on the event because the score processor
    /// has to mirror that break by hand, exactly as it mirrors a word skip's.
    /// </summary>
    public readonly record struct LineSealResult(int LineIndex, int MissedCells, bool ComboBroken, int SurvivingCombo = 0);

    public readonly record struct SyncSample(double Time, double Delta);

    public sealed class ResultsSummary
    {
        public required long Score { get; init; }

        /// <summary>0..1.</summary>
        public required double Accuracy { get; init; }

        public required double Wpm { get; init; }

        /// <summary>
        /// 0..100. DISPLAY ONLY since backlog 251: nothing derives from it, <see cref="Grade"/>
        /// included, and it is only rendered where the player has asked for the sync metric
        /// (<c>TypeBeatRulesetSetting.ShowSyncMetric</c>, off by default). Kept on the type, and
        /// kept computed, precisely so that toggle has something to show.
        /// </summary>
        public required double SyncPercent { get; init; }

        public required int MaxCombo { get; init; }

        /// <summary>All 8 <see cref="JudgementType"/> keys always present.</summary>
        public required IReadOnlyDictionary<JudgementType, int> Counts { get; init; }

        public required IReadOnlyList<SyncSample> SyncTimeline { get; init; }

        public required string Artist { get; init; }

        public required string Title { get; init; }

        /// <summary>
        /// ACCURACY alone: S >=0.95; A >=0.90; B >=0.80; C >=0.65; else D.
        ///
        /// <para>It used to require a matching <see cref="SyncPercent"/> floor at every tier (95 / 90
        /// / 80 / 65) as well. That second gate is gone since backlog 251, and the tier floors on
        /// accuracy are the ones the pair already used, so a play's grade can only ever come out the
        /// same or higher than it did. Sync is now a DISPLAY figure and nothing else: it is still
        /// computed and still shown to anyone who turns
        /// <c>TypeBeatRulesetSetting.ShowSyncMetric</c> on, but with the metric off by default a
        /// hidden number silently demoting a clean run was the whole objection, and no grade, score,
        /// judgement or submission reads it any more.</para>
        /// </summary>
        public string Grade
        {
            get
            {
                if (Accuracy >= 0.95) return "S";
                if (Accuracy >= 0.90) return "A";
                if (Accuracy >= 0.80) return "B";
                if (Accuracy >= 0.65) return "C";

                return "D";
            }
        }
    }
}
