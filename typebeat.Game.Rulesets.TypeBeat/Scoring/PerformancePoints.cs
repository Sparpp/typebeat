// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Scoring
{
    /// <summary>
    /// Performance points (pp) for a single play, priced LOCALLY. The canonical spec is the
    /// website's <c>docs/pp.md</c>; every constant below is pinned there and must not drift.
    ///
    /// <code>
    /// pp = 12.5 · SR_eff^2.00
    ///      · max(0, 1 − miss^1.6/notes)^10                   cleanliness
    ///      · max(0, 1 − mistypes^1.6/(notes+mistypes))^4     mistyping
    ///      · max(0.1, 1 + 0.50·log10(notes/100))             length, floored
    ///      · acc^1.80                                        timing quality
    ///      · (ln(1 + 9.0·maxcombo/notes)/ln(1 + 9.0))^2.50   combo
    ///      · modMult
    /// </code>
    ///
    /// <para>
    /// Kept byte-for-byte in step with the server's original
    /// (typebeat-web: <c>Typebeat.Web.Scoring.PerformancePoints</c>, which is where a play's stored
    /// pp actually comes from). Any change here must be mirrored there, and the shared
    /// <see cref="VERSION"/> bumped so the server reprices stored rows. The pairing is pinned by
    /// <c>typebeat-web/tests/Typebeat.WireCompat/PerformancePointsParityTest.cs</c>, the one project
    /// that compiles both halves, which drives the two implementations over the same plays and
    /// asserts they agree to the bit.
    /// </para>
    ///
    /// <para>
    /// MISSES and MISTYPES are priced by SEPARATE terms (backlog 89), and neither appears in the
    /// other's. CLEANLINESS is dropped cells alone, over the plain note count, at the steeper
    /// exponent 10. MISTYPING (wrong keypresses,
    /// <see cref="TypeBeatScoreProcessor.MISTYPE_RESULT"/>, i.e. the <c>combo_break</c> statistics
    /// key) is its own factor at 6. Between backlog 72 and 89 the two rode inside one fraction,
    /// which quietly made each penalty depend on the other: a mistype pulled the miss ratio towards
    /// its own value, so a player with a heavy mistype count was charged LESS per dropped cell than
    /// a clean one. Split, a play's misses cost the same whatever its keypresses did, and vice
    /// versa.
    /// </para>
    ///
    /// <para>
    /// A mistype is still the cheaper of the two failures (6 against 10): a stumble you recover from
    /// is not the same thing as never typing the cell at all. Backlog 95 raised both exponents
    /// (8.5 to 10, 3.5 to 6), so the gap between them is narrower than it was.
    /// </para>
    ///
    /// <para>
    /// BOTH PENALTIES RAISE THE RAW COUNT TO A POWER, NOT THE RATIO (backlog 97), and that power is
    /// <see cref="count_power"/>, a tunable rather than part of the shape (backlog 101). Cleanliness
    /// is <c>max(0, 1 − miss^1.2/notes)</c> and mistyping
    /// <c>max(0, 1 − mistypes^1.2/(notes + mistypes))</c>. Backlog 96 squared the RATIO, which runs
    /// the opposite way (a value already in [0, 1] gets SMALLER when squared, so <c>1 − r²</c> is
    /// LARGER than <c>1 − r</c>) and was a misreading of the intent; backlog 97 corrected it at a
    /// power of 2, which was far too extreme, and 101 settled the power at 1.2.
    /// </para>
    ///
    /// <para>
    /// THE <c>Math.Max</c> CLAMP IS LOAD-BEARING, NOT DEFENSIVE. A powered COUNT over an unpowered
    /// denominator is not bounded by [0, 1] at all: the base crosses zero at
    /// <c>miss = notes^(1/1.2)</c> (178 misses on a 500-note map) and runs NEGATIVE past it, and a
    /// fractional exponent on a negative base is not merely wrong but non-real. Misses really can
    /// equal <c>notes</c> and mistypes have no bound whatever, so this is the ordinary case and not a
    /// hostile-input guard. Clamped, the term is a well-defined 0 beyond that point: a CLIFF, chosen
    /// knowingly. WHERE it falls is exactly what <see cref="count_power"/> sets, which is why backlog
    /// 101 pulled that lever rather than the exponents; see the backlog-101 amendment in
    /// <c>docs/pp.md</c>, which states the figures.
    /// </para>
    ///
    /// <para>
    /// BOTH NUMERATORS GO THROUGH <c>Math.Pow</c>, WHICH CONVERTS TO DOUBLE FIRST (never
    /// <c>x * x</c> in <c>int</c>). Mistypes are unbounded, so an <c>int</c> square overflows
    /// catastrophically (at <c>int.MaxValue</c> the true square is about 4.6e18), and a tamper-shaped
    /// note count could do the same to the misses. In double, <c>Math.Pow(int.MaxValue, 1.2)</c> is
    /// about 1.6e11 and the ratio about 74, so the base clamps to a well-defined zero rather than
    /// wrapping to a NaN or, worse, a bonus. THE COUNTS ARE CLAMPED NON-NEGATIVE BEFORE THEY REACH
    /// THE POWER, and that ordering is load-bearing now the power is FRACTIONAL:
    /// <c>Math.Pow(-1, 1.2)</c> is NaN, not merely a wrong sign. <c>Math.Pow(0, 1.2)</c> is exactly
    /// 0, so both bases are still exactly 1.0 at a count of zero and a spotless play is priced
    /// bit-identically across any retune of the power.
    /// </para>
    ///
    /// <para>
    /// Why the mistype term keeps mistypes on BOTH sides of its fraction while the miss term does
    /// not: misses are bounded by <c>notes</c> (a play cannot drop more cells than the map has), but
    /// keypresses are UNBOUNDED, so a plain <c>mistypes^1.2/notes</c> would grow without limit and
    /// make the clamp the only thing standing between a masher and a non-real result at any count at
    /// all. Keeping the count in the denominator too moves the zero out to the positive root of
    /// <c>m^1.2 − m − notes = 0</c> (about 248.4, i.e. 249 mistypes, on a 500-note map) and keeps the
    /// sum itself in <c>double</c>, since <c>notes + mistypes</c> as <c>int</c> overflows as readily
    /// as the power does. Do not "simplify" that denominator away.
    /// </para>
    ///
    /// <para>
    /// Mistypes deliberately do NOT enter <c>notes</c>, which stays the map's CELL count
    /// (<c>great + ok + meh + good + miss</c>, one entry per cell, where <c>good</c> is an
    /// uncorrected typo). Letting keypresses inflate it would hand a masher a bigger LENGTH bonus
    /// and a smaller COMBO denominator, paying for the mashing twice over. At zero mistypes the
    /// mistyping term is exactly 1.0, so such a play is priced by
    /// <c>max(0, 1 − miss^1.2/notes)^10</c> alone.
    /// </para>
    ///
    /// <para>
    /// SR_eff is the map's star rating AT THE PLAY'S CLOCK RATE, never the base rating with a flat
    /// DT/HT bonus bolted on: the rate is priced exclusively through the recomputed star rating, so
    /// nothing double-counts. Only the BASE rates are pp-eligible (DT/NC 1.50x, HT 0.75x); a custom
    /// rate prices to nothing at all (<see cref="EligibleRate"/>). The play still ranks on the score
    /// leaderboards at every rate exactly as before, it just earns no pp.
    /// </para>
    ///
    /// <para>
    /// HALF TIME carries ONE extra term on top of that, and it is the only place in this file where
    /// a rate is priced by anything but the rating: <see cref="HalfTimeMultiplier"/>, the reciprocal
    /// of whatever Double Time is worth on the SAME map. Slowing a map down already lowers SR_eff,
    /// but on most maps it lowers it by far less than speeding it up raises it, so HT was the cheap
    /// way to keep a hard map's difficulty term while typing at a comfortable pace. Making the
    /// down-rate factor exactly 1/(up-rate factor) prices the two symmetrically, per map, rather
    /// than by a flat guess. It is applied by <see cref="Compute"/>'s <c>rateMultiplier</c>, NOT by
    /// <see cref="ModMultiplier"/>, which still carries no rate term at all (it sees only the mods
    /// and a note count, and could not compute this if it wanted to).
    /// </para>
    ///
    /// <para>
    /// WHERE THE CLIENT'S STAR RATING COMES FROM, and why it is the same number the server stores:
    /// <see cref="StarsFor"/> runs <see cref="LyricDifficulty.Compute"/> over the map's lyric lines
    /// at the eligible rate. That is the identical computation behind
    /// <see cref="TypeBeatDifficultyCalculator"/> and, through the server's mirrored copy of
    /// <c>LyricDifficulty</c>, behind <c>beatmaps.difficulty_rating</c> (rate 1.00),
    /// <c>beatmaps.sr_dt</c> (1.50) and <c>beatmaps.sr_ht</c> (0.75). Nothing is fetched from the
    /// server to price a play.
    /// </para>
    ///
    /// <para>
    /// ELIGIBILITY that is NOT about the rate (an unranked mod, an unsubmitted map, a failed run) is
    /// deliberately not re-derived here, exactly as the server does not re-derive it: the server
    /// prices only scores stored <c>ranked = true</c> on a set in status <c>'ranked'</c>. A caller
    /// that wants "what will this actually be worth" must apply those gates itself; see
    /// <see cref="UI.TypeBeatHudOverlay"/>, which is the one in-game consumer today.
    /// </para>
    ///
    /// Pure functions over primitives: no I/O, no throwing on hostile input. A degenerate play (zero
    /// notes, zero stars, NaN accuracy, a combo above the note count) yields 0, never NaN, Infinity
    /// or a negative value.
    /// </summary>
    public static class PerformancePoints
    {
        /// <summary>
        /// The formula's shape version, shared with the server's <c>PerformancePoints.VERSION</c>
        /// (which stamps <c>scores.pp_version</c> and drives its startup reprice). It lives here
        /// too so the parity test can assert the two halves are the same generation of the formula.
        ///
        /// <list type="bullet">
        /// <item>v1 = the initial formula (docs/pp.md), including the backlog-72 mistype term,
        /// which did not bump because no stored row could carry a mistype count at all.</item>
        /// <item>v2 = the backlog-89 rebalance: the miss exponent rises 7.5 to 8.5, and mistypes
        /// leave the cleanliness fraction for a term of their own at exponent 3.5. This one HAD to
        /// bump: the steeper miss exponent reprices every stored row carrying even ONE miss.</item>
        /// <item>v3 = the backlog-90 Half Time penalty: a base-rate HT play is multiplied by
        /// <see cref="HalfTimeMultiplier"/> on top of its 0.75x rating, which makes the down-rate
        /// factor the reciprocal of the up-rate one on the same map (or a flat 0.70 cut where that
        /// reciprocal would be a BUFF). Reprices every stored HT row and nothing else.</item>
        /// <item>v4 = the backlog-95 penalty rebalance: the miss exponent rises 8.5 to 10 and the
        /// mistype exponent 3.5 to 6. Both terms are exactly 1.0 at a count of zero whatever the
        /// exponent, so a spotless play is priced bit-identically; every stored row carrying even
        /// ONE miss or ONE mistype is repriced, which is what forces the bump.</item>
        /// <item>v5 = the backlog-96 squaring of both penalty RATIOS: cleanliness becomes (1 -
        /// (miss/notes)^2)^10 and mistyping (1 - (mistypes/(notes+mistypes))^2)^6, with the
        /// exponents 10 and 6 unchanged. Squaring a ratio that already sits in [0, 1] makes it
        /// SMALLER, so 1 - r^2 is LARGER than 1 - r and both terms soften a long way; this runs
        /// deliberately opposite to backlog 89 and 95, and is intended. Both bases are still
        /// exactly 1.0 at a count of zero, so a spotless play is priced bit-identically, while
        /// every stored row carrying even ONE miss or ONE mistype is repriced, upwards this time,
        /// which is what forces the bump.</item>
        /// <item>v6 = the backlog-97 squaring of both penalty COUNTS: cleanliness becomes max(0, 1 -
        /// miss^2/notes)^10 and mistyping max(0, 1 - mistypes^2/(notes + mistypes))^6, with the
        /// exponents 10 and 6 unchanged. Backlog 96 squared the RATIO, which softened both terms;
        /// squaring the raw COUNT hardens them instead, which is what was meant. The clamp is
        /// required rather than decorative: the base runs NEGATIVE once the squared count passes
        /// the denominator, and a fractional exponent on a negative base is non-real, so both terms
        /// end in a CLIFF at sqrt(notes) misses and at the positive root of m^2 - m - notes = 0
        /// mistypes. Both bases are still exactly 1.0 at a count of zero, so a spotless play is
        /// priced bit-identically, while every stored row carrying even ONE miss or ONE mistype is
        /// repriced downwards, most of them to zero, which is what forces the bump.</item>
        /// <item>v7 = the backlog-101 retune of count_power, 2 to 1.2. The SHAPE is exactly the one
        /// backlog 97 introduced, max(0, 1 - count^count_power/denominator), and the exponents 10
        /// and 6 do not move; only the power the raw count is raised to does, and it is now a
        /// declared constant rather than a squaring written out longhand. Squaring was too extreme:
        /// the cleanliness base hit zero at 23 misses on a 500-note map, 4.6% of it, so essentially
        /// every real play priced to nothing. At 1.2 it hits zero at 178, i.e. 35% of the map,
        /// which reads as dropping a third of it. Both bases are still exactly 1.0 at a count of
        /// zero, so a spotless play is priced bit-identically, while every stored row carrying even
        /// ONE miss or ONE mistype is repriced, upwards this time and mostly away from zero, which
        /// is what forces the bump.</item>
        /// <item>v8 = (no summary given)</item>
        /// <item>v9 = the backlog-112 retune of three constants, with the SHAPE untouched: the global
        /// scale rises 3.0 to 5.5, sr_exponent 2.60 to 2.70, and mistype_exponent 8.0 to 4.0.
        /// count_power stays 1.6, miss_exponent stays 10, and the length, accuracy and combo terms
        /// and every mod multiplier are exactly as they were. scale and sr_exponent together are
        /// close to a pure rescale (they preserve ranking order among plays on the same map, and
        /// steepen it only mildly across difficulties), and roughly DOUBLE a clean mid-difficulty
        /// play. Halving the mistype exponent is the part that changes ORDER: a mistype-heavy play
        /// is repriced far more than double, because 8 was steep enough to price such plays at
        /// essentially nothing. Both penalty bases are still exactly 1.0 at a count of zero, so a
        /// spotless play moves only by the rescale, while every stored row carrying a mistype is
        /// repriced upwards, which is what forces the bump.</item>
        /// <item>v10 = v10 = accuracy exponent 1.30 to 1.75 and combo exponent 0.75 to 1.50. A spotless
        /// play is priced bit-identically (both bases are exactly 1.0 at a full combo and perfect
        /// accuracy, whatever the exponent), so this repositions everything BELOW an FC rather than
        /// rescaling the pool: a 97% play at 0.90 combo loses about 9%, a 90% play at 0.75 combo
        /// about 23%.</item>
        /// <item>v11 = v11 = scale 5.5 to 12.5, sr_exponent 2.70 to 2.00, accuracy_exponent 1.75 to 1.80,
        /// combo_exponent 1.50 to 2.50. v10 never shipped, so the meaningful comparison is against
        /// v9: the SR exponent drop flattens the difficulty curve so easy maps gain and hard maps
        /// lose, while the combo exponent sharpens what a broken combo costs.</item>
        /// <item>v12 = the combo term stops being a plain powered ratio. The base becomes ln(1 +
        /// 9.0·r)/ln(1 + 9.0) over r = maxcombo/notes, and only then is raised to the same
        /// combo_exponent of 2.50; nothing else in the formula moves. A FULL COMBO IS EXACTLY 1.0
        /// for any shape constant, so an FC is priced bit-identically and this repositions only
        /// what sits BELOW one, exactly as the v10 and v11 combo retunes did. The concave log base
        /// very nearly cancels the convex ^2.50 over the range real plays live in, so the term
        /// reads as roughly LINEAR in the combo ratio down to about 0.7 (0.90 gives 0.9007, 0.80
        /// gives 0.7983, 0.75 gives 0.7458): a broken combo now costs roughly its face value, where
        /// at ^2.50 alone losing 10% of a combo cost 23% of the term. Every stored row below a full
        /// combo is repriced upwards, which is what forces the bump.</item>
        /// </list>
        /// </summary>
        public const int VERSION = 12;

        // ---- formula constants (docs/pp.md) ----

        private const double scale = 12.5;              // C: global scale, does not affect ranking order
        private const double sr_exponent = 2.00;
        private const double miss_exponent = 10.0;
        private const double mistype_exponent = 4.0;

        /// <summary>
        /// The power the RAW COUNT is raised to inside both penalty bases, before its denominator
        /// divides it. A tunable, not a hard-wired square: backlog 97 introduced this shape with the
        /// power written out longhand as <c>(double)x * x</c>, which read as part of the shape and
        /// could only be retuned by hand in both mirrors; backlog 101 lifted it out here.
        ///
        /// <para>It is the constant that decides WHERE EACH TERM'S CLIFF FALLS, since the
        /// cleanliness base vanishes at <c>miss = notes^(1/count_power)</c>. At 2 that is 23 misses
        /// on a 500-note map, i.e. 4.6% of it, which zeroed essentially every real play; at 1.2 it
        /// is 178, i.e. 35%, which reads as "you dropped a third of the map". It also decides how
        /// the cliff scales WITH map size: as a fraction of the map it is
        /// <c>notes^(1/count_power - 1)</c>, so at 2 it swung from 10% of a 100-note map to 2.2% of
        /// a 2000-note map (long maps drastically harsher, for no reason anyone chose), while at 1.2
        /// it moves only 46% to 35% to 28% across 100, 500 and 2000 notes.</para>
        /// </summary>
        private const double count_power = 1.6;

        private const double length_weight = 0.50;
        private const double length_floor = 0.1;
        private const double accuracy_exponent = 1.80;
        private const double combo_exponent = 2.50;

        /// <summary>
        /// The CURVATURE of the combo base (backlog 131). The term is
        /// <c>(ln(1 + combo_log_shape·r)/ln(1 + combo_log_shape))^combo_exponent</c> over
        /// <c>r = maxcombo/notes</c>: the ratio is bent through a log BEFORE the exponent reaches
        /// it, where every generation through v11 raised the plain ratio.
        ///
        /// <para>A FULL COMBO IS EXACTLY 1.0 AT EVERY VALUE OF THIS CONSTANT, since
        /// <c>ln(1 + k)/ln(1 + k)</c> is 1 and 1 raised to anything is 1. So an FC is priced
        /// bit-identically across any retune of it and the constant repositions only what sits
        /// BELOW a full combo, exactly as the v10 and v11 combo retunes did.</para>
        ///
        /// <para>THE BEND RUNS OPPOSITE TO THE EXPONENT, which is why it is worth having: the log
        /// base is CONCAVE (it lifts every ratio under 1) where <c>^2.50</c> is convex. At 9 the two
        /// very nearly cancel over the range real plays live in, so the term reads as roughly LINEAR
        /// in the combo ratio down to about 0.7 (0.90 gives 0.9007, 0.80 gives 0.7983, 0.75 gives
        /// 0.7458). A broken combo therefore costs roughly its FACE VALUE, where under
        /// <c>^2.50</c> alone losing 10% of a combo cost 23% of the term.</para>
        /// </summary>
        private const double combo_log_shape = 9.0;

        private const double reference_notes = 100.0;  // the log bonus' pivot: 100 notes is the 1.0 point

        /// <summary>
        /// The flat cut a Half Time play takes when the mirror multiplier would be a BUFF, i.e. a
        /// 30% reduction. See <see cref="HalfTimeMultiplier"/> for when that happens and why the
        /// guard is not a <c>Math.Min</c>.
        /// </summary>
        private const double half_time_buff_clamp = 0.70;

        // ---- mod multipliers (docs/pp.md) ----

        private const double literate_multiplier = 1.06;
        private const double fletcher_multiplier = 0.90;
        private const double no_fail_multiplier = 0.90;
        private const double flashlight_offset = 0.02;
        private const double flashlight_weight = 0.06;
        private const double flashlight_floor = 1.0;

        /// <summary>
        /// Slack when testing a play's rate against a mod's base rate. The slider steps by 0.01 and
        /// 1.50 / 0.75 are exactly representable in binary, so this only guards accumulated bindable
        /// arithmetic; it is far tighter than one slider step, so no genuinely custom rate can slip
        /// through it. Mirrors the server's <c>rate_epsilon</c>.
        /// </summary>
        private const double rate_epsilon = 1e-9;

        /// <summary>
        /// The judgement results that count as a NOTE, i.e. the server's
        /// <c>["great", "ok", "meh", "good", "miss"]</c> statistics keys.
        /// <see cref="HitResult.IgnoreHit"/> is deliberately absent: the line containers are
        /// ignore_hit judgements and counting them would inflate <c>notes</c> and dilute every
        /// single factor (cleanliness, length, combo).
        ///
        /// <para><see cref="TypeBeatResultMapping.UNFIXED_TYPO"/> IS a note, and that is the pp half
        /// of backlog 124 and 126. It is one cell of the map, so leaving it out would shorten the
        /// map pp thinks the player played, inflating the length term and the combo ratio. It is
        /// deliberately NOT <see cref="MISS_RESULT"/>: a miss is a character the player was too slow
        /// to finish at all, a typo is one they finished wrongly, and the mistype term already
        /// prices the second. So the typo costs COMPLETION like a miss
        /// (<see cref="TypeBeatScoreProcessor.CountsAsTyped"/>) and pp like a typo, which is exactly
        /// the split those two backlog items exist to keep.</para>
        /// </summary>
        public static readonly IReadOnlyList<HitResult> NOTE_RESULTS = new[]
        {
            HitResult.Great,
            HitResult.Ok,
            HitResult.Meh,
            TypeBeatResultMapping.UNFIXED_TYPO,
            HitResult.Miss,
        };

        /// <summary>The one <see cref="NOTE_RESULTS"/> member that is also a MISS.</summary>
        public const HitResult MISS_RESULT = HitResult.Miss;

        /// <summary>
        /// The MISTYPE result: one per wrong KEYPRESS, persisted as the <c>combo_break</c>
        /// statistics key (see <see cref="TypeBeatScoreProcessor.MISTYPE_RESULT"/>, which this
        /// aliases rather than re-states). Not a note, not accuracy-affecting, and priced by its
        /// own term since backlog 89.
        /// </summary>
        public const HitResult MISTYPE_RESULT = TypeBeatScoreProcessor.MISTYPE_RESULT;

        /// <summary>The DT / NC / HT base rates: the ONLY rates that earn pp (docs/pp.md).</summary>
        public const double DOUBLE_TIME_BASE_RATE = 1.50;

        /// <summary>Half Time's base rate, the down-rate counterpart of <see cref="DOUBLE_TIME_BASE_RATE"/>.</summary>
        public const double HALF_TIME_BASE_RATE = 0.75;

        /// <summary>
        /// Notes, misses and mistypes for a play, as the formula defines them. <see cref="Mistypes"/>
        /// defaults to 0 so a play carrying no mistype count prices exactly as it always did.
        /// </summary>
        public readonly record struct NoteCounts(int Notes, int Misses, int Mistypes = 0);

        /// <summary>
        /// Notes, misses and mistypes from a play's judgement counts: the live
        /// <see cref="ScoreProcessor.Statistics"/> mid-play, or a finished score's
        /// <see cref="ScoreInfo.Statistics"/>. Negative counts (which only a rewind race could
        /// produce) contribute nothing rather than subtracting, and an absent
        /// <see cref="MISTYPE_RESULT"/> reads as 0 mistypes.
        ///
        /// <para>MID-PLAY THIS IS WHAT MAKES THE LIVE COUNTER CONVERGE: <c>notes</c> is the count of
        /// JUDGED notes, so it grows cell by cell and, on the last judgement of a passed play, is
        /// exactly the count the submitted score carries.</para>
        /// </summary>
        public static NoteCounts CountNotes(IReadOnlyDictionary<HitResult, int>? statistics)
        {
            if (statistics == null)
                return default;

            int notes = 0, misses = 0;

            for (int i = 0; i < NOTE_RESULTS.Count; i++)
            {
                var key = NOTE_RESULTS[i];

                if (!statistics.TryGetValue(key, out int count) || count <= 0)
                    continue;

                notes += count;

                if (key == MISS_RESULT)
                    misses += count;
            }

            int mistypes = statistics.TryGetValue(MISTYPE_RESULT, out int mistypeCount) && mistypeCount > 0 ? mistypeCount : 0;

            return new NoteCounts(notes, misses, mistypes);
        }

        /// <summary>The same counts for a finished score.</summary>
        public static NoteCounts CountNotes(ScoreInfo score) => CountNotes(score.Statistics);

        /// <summary>
        /// The clock rate this play is PRICED at, or null when its rate makes it pp-ineligible.
        /// Mirrors the server's <c>StarsFor</c>, minus its third state: the server can be missing a
        /// stored rate rating and has to defer, whereas the client computes the rating itself and
        /// never can.
        ///
        /// <list type="bullet">
        /// <item>No rate mod: 1.00.</item>
        /// <item>Exactly one rate mod sitting at its slider default: 1.50 (DT / NC) or 0.75 (HT).</item>
        /// <item>A custom rate: null. Permanently pp-ineligible; the play still ranks on the score
        /// leaderboards exactly as before.</item>
        /// <item>More than one rate mod: null. Tamper-shaped by construction (the client makes
        /// DT / NC / HT mutually exclusive), so it is refused rather than guessed at.</item>
        /// </list>
        /// </summary>
        public static double? EligibleRate(IReadOnlyList<Mod>? mods)
        {
            Mod? rateMod = null;
            int rateMods = 0;

            if (mods != null)
            {
                for (int i = 0; i < mods.Count; i++)
                {
                    if (!TryGetBaseRate(mods[i].Acronym, out _))
                        continue;

                    rateMod = mods[i];
                    rateMods++;
                }
            }

            if (rateMods == 0)
                return 1.0;

            if (rateMods > 1 || rateMod == null || !TryGetBaseRate(rateMod.Acronym, out double baseRate))
                return null;

            // A rate mod that carries no adjustable speed at all can only be sitting at its default,
            // which is exactly how the server reads a row with no stored speed_change.
            double rate = rateMod is ModRateAdjust adjust ? adjust.SpeedChange.Value : baseRate;

            return Math.Abs(rate - baseRate) > rate_epsilon ? (double?)null : baseRate;
        }

        /// <summary>
        /// The base (pp-eligible) rate for a rate mod's acronym, or false for anything that is not
        /// a rate mod. Keyed on the acronym rather than the mod type for the same reason the server
        /// is: the acronym is what travels on the wire and what the two implementations share.
        /// </summary>
        public static bool TryGetBaseRate(string? acronym, out double baseRate)
        {
            switch (acronym?.Trim().ToUpperInvariant())
            {
                case "DT":
                case "NC":
                    baseRate = DOUBLE_TIME_BASE_RATE;
                    return true;

                case "HT":
                    baseRate = HALF_TIME_BASE_RATE;
                    return true;

                default:
                    baseRate = 0;
                    return false;
            }
        }

        /// <summary>
        /// The star rating this play is priced at, or null when its rate makes it pp-ineligible:
        /// <see cref="LyricDifficulty.Compute"/> over the map's lyric lines at
        /// <see cref="EligibleRate"/>. This is the client-side stand-in for the server's three
        /// stored ratings, and produces the same numbers because both sides run the same mirrored
        /// difficulty code (see the class docs).
        /// </summary>
        public static double? StarsFor(IEnumerable<LyricLine> lines, IReadOnlyList<Mod>? mods)
            => EligibleRate(mods) is double rate ? LyricDifficulty.Compute(lines, rate) : (double?)null;

        /// <summary>
        /// The play's RATE multiplier, the second half of what <see cref="StarsFor"/> starts:
        /// exactly 1.0 for a no-mod play, a Double Time / Nightcore play and a rate-ineligible one,
        /// and <see cref="HalfTimeMultiplier"/> for a base-rate Half Time play.
        ///
        /// <para>Only the Half Time branch pays for the extra <see cref="LyricDifficulty"/> passes,
        /// and it needs two of them (the map at 1.00x and at 1.50x) on top of the 0.75x rating
        /// <see cref="StarsFor"/> already computes. Those are the same three numbers the server
        /// stores as <c>difficulty_rating</c> / <c>sr_dt</c> / <c>sr_ht</c>, which is why the two
        /// halves reach the same multiplier without the client fetching anything.</para>
        /// </summary>
        public static double RateMultiplier(IEnumerable<LyricLine> lines, IReadOnlyList<Mod>? mods)
        {
            // EligibleRate returns the base-rate CONSTANT itself for an eligible rate mod, so this
            // is an exact comparison against the same double, not a tolerance question.
            if (EligibleRate(mods) != HALF_TIME_BASE_RATE)
                return 1;

            return HalfTimeMultiplier(
                LyricDifficulty.Compute(lines),
                LyricDifficulty.Compute(lines, DOUBLE_TIME_BASE_RATE),
                LyricDifficulty.Compute(lines, HALF_TIME_BASE_RATE));
        }

        /// <summary>
        /// The extra multiplier a base-rate HALF TIME play is priced by, on top of its 0.75x
        /// rating. 1.0 is NOT a possible answer here; every other rate's multiplier is 1.0 and never
        /// reaches this function.
        ///
        /// <para>Write <c>D = (sr_dt/sr_base)^2.70</c> and <c>H = (sr_ht/sr_base)^2.70</c>. Those
        /// are what the two base rates are ALREADY worth on this map, purely through
        /// <c>SR^2.70</c>, with no term of their own anywhere: D is Double Time's emergent bonus
        /// and H is Half Time's emergent discount. The mirror multiplier is <c>1/(D·H)</c>, which
        /// makes Half Time's TOTAL rate factor <c>H · 1/(D·H) = 1/D</c>, exactly the reciprocal of
        /// Double Time's, per map. Speeding a map up and slowing it down are then equal and
        /// opposite by construction rather than by a flat guess, which is the whole point: HT used
        /// to be the cheap way to keep a hard map's difficulty term while typing at a comfortable
        /// pace, because slowing down costs far less than speeding up pays.</para>
        ///
        /// <para>THE GUARD IS LOAD-BEARING, NOT DEFENSIVE. The mirror is a BUFF exactly when
        /// <c>1/D &gt; H</c>, i.e. <c>D·H &lt; 1</c>, i.e. <c>sr_dt · sr_ht &lt; sr_base²</c>: a map
        /// whose SR curve is concave in log-rate, so slowing it down helps far more than speeding
        /// it up hurts. That is precisely the map an unguarded mirror would REWARD for using Half
        /// Time. Worked example: base 4.2, dt 4.5, ht 2.0 gives D = 1.205 and H = 0.135, so the
        /// mirror would make HT's total factor 0.830 against today's 0.135, a six-fold buff.
        /// Clamped, it is <c>0.70 · 0.135 = 0.094</c>, still a nerf.</para>
        ///
        /// <para>IT IS NOT A <c>Math.Min</c>. A mirror multiplier of, say, 0.90 is a mild nerf and
        /// must be used AS IS. <c>Math.Min(mirror, 0.70)</c> would deepen every mild nerf into a
        /// flat 30% cut and quietly throw away the per-map symmetry this term exists for. The clamp
        /// applies only on the wrong side of 1.0.</para>
        ///
        /// <para>Hostile input yields 0, in keeping with the rest of this file: a non-finite or
        /// non-positive rating describes no map, and returning 0 makes the play price to 0 rather
        /// than to NaN. <see cref="Compute"/> would already return 0 for a non-positive
        /// <c>starRating</c>, but this is reached down a different path and a NaN here would
        /// survive that guard and poison the product.</para>
        /// </summary>
        /// <param name="baseStars">The map's rate-1.0 rating (the server's <c>difficulty_rating</c>).</param>
        /// <param name="starsDoubleTime">Its rating at 1.50x (the server's <c>sr_dt</c>).</param>
        /// <param name="starsHalfTime">Its rating at 0.75x (the server's <c>sr_ht</c>).</param>
        public static double HalfTimeMultiplier(double baseStars, double starsDoubleTime, double starsHalfTime)
        {
            if (!isRateableRating(baseStars) || !isRateableRating(starsDoubleTime) || !isRateableRating(starsHalfTime))
                return 0;

            double doubleTimeFactor = Math.Pow(starsDoubleTime / baseStars, sr_exponent);
            double halfTimeFactor = Math.Pow(starsHalfTime / baseStars, sr_exponent);

            double mirror = 1.0 / (doubleTimeFactor * halfTimeFactor);

            if (!double.IsFinite(mirror) || mirror <= 0)
                return 0;

            // Strictly above 1.0 the mirror would PAY for playing slower; that, and only that,
            // takes the flat cut. Anything at or below 1.0 is already a nerf and is used exactly as
            // computed.
            return mirror > 1 ? half_time_buff_clamp : mirror;
        }

        /// <summary>A star rating that can be divided by or raised to a power without producing nonsense.</summary>
        private static bool isRateableRating(double stars) => double.IsFinite(stars) && stars > 0;

        /// <summary>
        /// The mod multiplier for a play. There is NO rate term here on purpose: DT / HT are priced
        /// entirely through the star rating (<see cref="StarsFor"/>).
        ///
        /// <para>Flashlight's bonus grows with song length, so it pays off on long maps and barely
        /// moves on short ones. Its floor clamp is load-bearing: unclamped, the raw term dips BELOW
        /// 1.0 under ~46 notes, which would turn a bonus mod into a penalty on short maps.</para>
        ///
        /// <para>No Fail is priced at 0.90 (osu's value) rather than left free: it converts a
        /// would-be fail, which earns nothing at all, into a completed play. Its 0.5x SCORE
        /// multiplier stays score-side; mirroring that here would double-punish on top of the miss
        /// term.</para>
        /// </summary>
        public static double ModMultiplier(IReadOnlyList<Mod>? mods, int notes)
        {
            if (mods == null || mods.Count == 0)
                return 1;

            double multiplier = 1;

            // A stack cannot really hold the same mod twice (a mod stack is keyed by type), so a
            // duplicated acronym is tamper-shaped and is applied once.
            var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < mods.Count; i++)
            {
                string acronym = mods[i].Acronym;

                if (string.IsNullOrWhiteSpace(acronym) || !applied.Add(acronym))
                    continue;

                switch (acronym.ToUpperInvariant())
                {
                    case "LT":
                        multiplier *= literate_multiplier;
                        break;

                    case "FL":
                        multiplier *= FlashlightMultiplier(notes);
                        break;

                    case "FT":
                        multiplier *= fletcher_multiplier;
                        break;

                    case "NF":
                        multiplier *= no_fail_multiplier;
                        break;

                    // SD / GK / MU are explicitly 1.0, matching their score multipliers. Anything else
                    // (including a mod this table has not learned yet) is neutral: an unknown mod
                    // must never silently inflate or deflate a ranking.
                }
            }

            return multiplier;
        }

        /// <summary>Flashlight's length-scaled bonus, floored at 1.0 (see <see cref="ModMultiplier"/>).</summary>
        public static double FlashlightMultiplier(int notes)
            => notes <= 0
                ? flashlight_floor
                : Math.Max(flashlight_floor, 1 + flashlight_offset + flashlight_weight * Math.Log10(notes / reference_notes));

        /// <summary>The length bonus, floored (see <see cref="length_floor"/>).</summary>
        /// <remarks>
        /// The raw term crosses zero at about 4 notes and would go negative below that, so the floor
        /// is required: no play may ever compute to zero or negative pp from its LENGTH alone. It
        /// bites under roughly 5 notes, i.e. only on data that describes no real map.
        /// </remarks>
        public static double LengthBonus(int notes)
            => notes <= 0
                ? length_floor
                : Math.Max(length_floor, 1 + length_weight * Math.Log10(notes / reference_notes));

        /// <summary>
        /// pp for one play. <paramref name="starRating"/> is the play's EFFECTIVE rating
        /// (<see cref="StarsFor"/>), <paramref name="accuracy"/> the standardised hit accuracy and
        /// <paramref name="maxCombo"/> the highest combo reached.
        ///
        /// <para>Inputs are clamped rather than trusted: misses and combo into <c>[0, notes]</c>
        /// (the theoretical max combo of a typing map IS its note count), mistypes to non-negative
        /// (they have no upper bound: a player can press as many wrong keys as they like) and
        /// accuracy into <c>[0, 1]</c>. The result is guaranteed finite and non-negative.</para>
        ///
        /// <para><paramref name="rateMultiplier"/> is the play's RATE multiplier, which is 1.0 for
        /// every play except a base-rate Half Time one; <see cref="ForPlay"/> takes it from
        /// <see cref="RateMultiplier"/>. It is a parameter rather than something computed here
        /// because it takes all three of the map's star ratings and this function is handed only
        /// the one it prices with. A caller that omits it prices the play WITHOUT the Half Time
        /// penalty, so every path that can see an HT play must pass it; the WireCompat parity test
        /// is what pins that. Non-finite or negative values fall out as 0 through the guard at the
        /// end, exactly like every other hostile input.</para>
        /// </summary>
        public static double Compute(
            double starRating,
            int notes,
            int misses,
            double accuracy,
            int maxCombo,
            IReadOnlyList<Mod>? mods,
            int mistypes = 0,
            double rateMultiplier = 1)
        {
            // No notes describes no play; a zero or non-finite rating prices nothing.
            if (notes <= 0 || !double.IsFinite(starRating) || starRating <= 0)
                return 0;

            misses = Math.Clamp(misses, 0, notes);
            maxCombo = Math.Clamp(maxCombo, 0, notes);
            mistypes = Math.Max(mistypes, 0);
            accuracy = double.IsFinite(accuracy) ? Math.Clamp(accuracy, 0, 1) : 0;

            double difficulty = Math.Pow(starRating, sr_exponent);

            // Dropped cells, and nothing else. The RAW COUNT carries the power (count_power), not
            // the ratio, so this base falls off far faster than misses/notes ever did: it reaches 0
            // at misses = notes^(1/count_power), i.e. 178 misses on a 500-note map, and would run
            // NEGATIVE past that. Math.Max is what makes it a well-defined cliff instead, and it is
            // load-bearing: misses can equal notes after the clamp above, so the unclamped base
            // really does go negative, and a fractional exponent on a negative base is non-real.
            // THE Math.Clamp ABOVE HAS TO COME FIRST for the same reason from the other direction:
            // Math.Pow of a negative count under a fractional power is NaN, not a wrong number.
            // Math.Pow also converts to double, which is what stops a tamper-shaped note count
            // overflowing an int square and flipping the sign of the whole penalty. What a miss
            // costs does not depend on the keypresses. At zero misses Math.Pow(0, count_power) is
            // exactly 0, so the base is exactly 1.0 and the term with it.
            double missBase = Math.Max(0.0, 1.0 - Math.Pow(misses, count_power) / notes);
            double cleanliness = Math.Pow(missBase, miss_exponent);

            // Wrong keypresses, and nothing else, under the same power. The count is UNBOUNDED, so
            // it still sits on BOTH sides of the fraction: that is what keeps the denominator
            // growing with the count, putting the zero at the positive root of
            // m^count_power - m - notes = 0 (about 248.4, i.e. 249 mistypes, on a 500-note map)
            // rather than at notes^(1/count_power). The numerator goes through Math.Pow and the sum
            // is taken in DOUBLE, independently and for the same reason: an int square overflows
            // catastrophically (the true square at int.MaxValue is about 4.6e18) and notes +
            // mistypes as ints overflows too. In double, int.MaxValue mistypes give a ratio of about
            // 74, so the base clamps to a well-defined 0 rather than wrapping into a NaN or a bonus.
            // At zero mistypes this is exactly 1.0. notes is untouched by design (see the class
            // docs): only this term prices mistypes.
            double mistypeBase = Math.Max(0.0, 1.0 - Math.Pow(mistypes, count_power) / ((double)notes + mistypes));
            double mistyping = Math.Pow(mistypeBase, mistype_exponent);

            double length = LengthBonus(notes);
            double timing = Math.Pow(accuracy, accuracy_exponent);
            // The longest run as a fraction of the map, bent through a log before the exponent
            // reaches it (see combo_log_shape). NO CLAMP IS NEEDED HERE and none would bite:
            // maxCombo is already clamped into [0, notes] above, so comboRatio is in [0, 1], the
            // log's argument in [1, 1 + combo_log_shape] and the base in [0, 1]. A FULL COMBO IS
            // EXACTLY 1.0, since the numerator and denominator are then the same Math.Log call on
            // the same value, so an FC is priced bit-identically across any retune of the shape.
            double comboRatio = (double)maxCombo / notes;
            double comboBase = Math.Log(1.0 + combo_log_shape * comboRatio) / Math.Log(1.0 + combo_log_shape);
            double combo = Math.Pow(comboBase, combo_exponent);

            double pp = scale * difficulty * cleanliness * mistyping * length * timing * combo * ModMultiplier(mods, notes) * rateMultiplier;

            return double.IsFinite(pp) && pp > 0 ? pp : 0;
        }

        /// <summary>
        /// pp for a play whose counts have already been read: the whole per-play pipeline in one
        /// call, so the live HUD counter and any end-of-play consumer can never disagree about the
        /// argument order. Mirrors the server's <c>ForScore</c> minus its storage concerns (the
        /// ranked flag, the settled/pending distinction), which have no client-side meaning.
        ///
        /// <para><paramref name="rateMultiplier"/> defaults to 1.0, which is correct for every play
        /// but a base-rate Half Time one. A caller that can see an HT play must pass
        /// <see cref="RateMultiplier"/>; <see cref="PerformancePointsDisplay"/> is where the client
        /// surfaces get both halves from one place.</para>
        /// </summary>
        public static double ForPlay(double starRating, NoteCounts counts, double accuracy, int maxCombo, IReadOnlyList<Mod>? mods, double rateMultiplier = 1)
            => Compute(starRating, counts.Notes, counts.Misses, accuracy, maxCombo, mods, counts.Mistypes, rateMultiplier);
    }
}
