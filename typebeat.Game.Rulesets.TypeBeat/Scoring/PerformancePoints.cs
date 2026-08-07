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
    /// pp = 4.0 · SR_eff^2.70
    ///          · (1 − miss/notes)^8.5                         cleanliness
    ///          · (1 − mistypes/(notes+mistypes))^3.5          mistyping
    ///          · max(0.1, 1 + 0.70·log10(notes/100))          length, floored
    ///          · acc^1.30                                     timing quality
    ///          · (maxcombo/notes)^0.55                        combo
    ///          · modMult
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
    /// exponent 8.5. MISTYPING (wrong keypresses,
    /// <see cref="TypeBeatScoreProcessor.MISTYPE_RESULT"/>, i.e. the <c>combo_break</c> statistics
    /// key) is its own factor at 3.5. Between backlog 72 and 89 the two rode inside one fraction,
    /// which quietly made each penalty depend on the other: a mistype pulled the miss ratio towards
    /// its own value, so a player with a heavy mistype count was charged LESS per dropped cell than
    /// a clean one. Split, a play's misses cost the same whatever its keypresses did, and vice
    /// versa.
    /// </para>
    ///
    /// <para>
    /// Why the mistype term keeps mistypes on BOTH sides of its fraction while the miss term does
    /// not: misses are bounded by <c>notes</c> (a play cannot drop more cells than the map has), but
    /// keypresses are UNBOUNDED, so <c>1 − mistypes/notes</c> would run negative and a fractional
    /// exponent on a negative base is not merely wrong but non-real. Putting the count in the
    /// denominator too bounds the base to [0, 1] for any mistype count, however absurd, and it
    /// decays towards 0 rather than exploding. Do not "simplify" that denominator away.
    /// </para>
    ///
    /// <para>
    /// Mistypes deliberately do NOT enter <c>notes</c>, which stays <c>great + ok + meh + miss</c>,
    /// the map's cell count. Letting keypresses inflate it would hand a masher a bigger LENGTH bonus
    /// and a smaller COMBO denominator, paying for the mashing twice over. At zero mistypes the
    /// mistyping term is exactly 1.0, so such a play is priced by <c>(1 − miss/notes)^8.5</c> alone.
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
        /// </list>
        /// </summary>
        public const int VERSION = 2;

        // ---- formula constants (docs/pp.md) ----

        private const double scale = 4.0;              // C: global scale, does not affect ranking order
        private const double sr_exponent = 2.70;
        private const double miss_exponent = 8.5;
        private const double mistype_exponent = 3.5;
        private const double length_weight = 0.70;
        private const double length_floor = 0.1;
        private const double accuracy_exponent = 1.30;
        private const double combo_exponent = 0.55;
        private const double reference_notes = 100.0;  // the log bonus' pivot: 100 notes is the 1.0 point

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
        /// <c>["great", "ok", "meh", "miss"]</c> statistics keys.
        /// <see cref="HitResult.IgnoreHit"/> is deliberately absent: the line containers are
        /// ignore_hit judgements and counting them would inflate <c>notes</c> and dilute every
        /// single factor (cleanliness, length, combo).
        /// </summary>
        public static readonly IReadOnlyList<HitResult> NOTE_RESULTS = new[]
        {
            HitResult.Great,
            HitResult.Ok,
            HitResult.Meh,
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

                    // SD / MU are explicitly 1.0, matching their score multipliers. Anything else
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
        /// </summary>
        public static double Compute(
            double starRating,
            int notes,
            int misses,
            double accuracy,
            int maxCombo,
            IReadOnlyList<Mod>? mods,
            int mistypes = 0)
        {
            // No notes describes no play; a zero or non-finite rating prices nothing.
            if (notes <= 0 || !double.IsFinite(starRating) || starRating <= 0)
                return 0;

            misses = Math.Clamp(misses, 0, notes);
            maxCombo = Math.Clamp(maxCombo, 0, notes);
            mistypes = Math.Max(mistypes, 0);
            accuracy = double.IsFinite(accuracy) ? Math.Clamp(accuracy, 0, 1) : 0;

            double difficulty = Math.Pow(starRating, sr_exponent);

            // Dropped cells, and nothing else: misses <= notes after the clamp above, so the base
            // sits in [0, 1] and the term with it. What a miss costs does not depend on the
            // keypresses.
            double cleanliness = Math.Pow(1.0 - (double)misses / notes, miss_exponent);

            // Wrong keypresses, and nothing else. The count is UNBOUNDED, so it sits on both sides
            // of the fraction: that is what keeps the base in [0, 1] and decaying towards 0 rather
            // than running negative under a fractional exponent. The sum is taken in DOUBLE, because
            // notes + mistypes as ints would overflow on a tamper-shaped count and flip the sign of
            // the ratio, turning the penalty into a bonus. At zero mistypes this is exactly 1.0.
            // notes is untouched by design (see the class docs): only this term prices mistypes.
            double mistyping = Math.Pow(1.0 - mistypes / ((double)notes + mistypes), mistype_exponent);

            double length = LengthBonus(notes);
            double timing = Math.Pow(accuracy, accuracy_exponent);
            double combo = Math.Pow((double)maxCombo / notes, combo_exponent);

            double pp = scale * difficulty * cleanliness * mistyping * length * timing * combo * ModMultiplier(mods, notes);

            return double.IsFinite(pp) && pp > 0 ? pp : 0;
        }

        /// <summary>
        /// pp for a play whose counts have already been read: the whole per-play pipeline in one
        /// call, so the live HUD counter and any end-of-play consumer can never disagree about the
        /// argument order. Mirrors the server's <c>ForScore</c> minus its storage concerns (the
        /// ranked flag, the settled/pending distinction), which have no client-side meaning.
        /// </summary>
        public static double ForPlay(double starRating, NoteCounts counts, double accuracy, int maxCombo, IReadOnlyList<Mod>? mods)
            => Compute(starRating, counts.Notes, counts.Misses, accuracy, maxCombo, mods, counts.Mistypes);
    }
}
