// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Scoring
{
    /// <summary>
    /// Performance points (pp) for a single play, priced LOCALLY. The canonical spec is the
    /// website's <c>docs/pp.md</c>; every constant below is pinned there and must not drift.
    ///
    /// <code>
    /// pp = 9 · SR_eff^2.30
    ///      · max(0, 1 − (miss/difficult)^1.2)^13.5134        cleanliness, over DIFFICULT characters
    ///      · accuracyShare(acc) · knee(acc)                  timing quality, DEPARTURE 5
    ///      · modMult
    ///      · (1 + comboBonus)                              combo bonus, a FRACTION of the price
    ///
    /// accuracyShare(acc) = expCurve((acc − acc_floor) / (1 − acc_floor))
    /// expCurve(t)        = (e^(k·t) − 1) / (e^k − 1), or t itself when k is 0
    ///
    /// comboBonus = min(10, cells/200 · 1) / 100 · maxCombo/cells · (1.5 on a spotless full combo)
    /// </code>
    ///
    /// <para>VERSION 22 IS A FORK OF THE PRICING SHAPE, tuned in the PP Sandbox
    /// (<c>tools/pp-sandbox/</c>), whose module is the same arithmetic with every constant lifted
    /// into a dial. Four departures from v21, each marked where it acts:</para>
    ///
    /// <list type="number">
    /// <item><description>THE TYPO TERM IS GONE. A wrong keypress the player recovered from costs
    /// nothing: the price is the rating, cleanliness, timing, mods and the combo bonus alone. The
    /// typo COUNT is still derived by <see cref="CountNotes"/> for the surfaces that display it,
    /// but nothing here reads it.</description></item>
    /// <item><description>THE MISS PENALTY IS JUDGED AGAINST THE MAP'S DIFFICULT CHARACTERS, NOT ITS
    /// CELL COUNT. <paramref name="difficultCharacters"/> is
    /// <see cref="LyricDifficulty.ModelResult.DifficultCharacters"/> at the played rate on the played
    /// stream: every cell weighted by how close its own bin sits to the map's peak, to the envelope
    /// power. Dropping a cell therefore costs more on a map whose difficulty is concentrated in a few
    /// passages, and the count is a property of the map rather than of the play.</description></item>
    /// <item><description>THE LOSS CURVE IS CALIBRATED IN FRACTIONS. The power sits on the missed
    /// FRACTION of those difficult characters, so the same miss RATE costs the same share of the
    /// core price on every map. <c>miss_exponent</c> 13.5134 is <c>ln(2)/ln(1/0.95)</c>: at
    /// <c>count_power</c> 1, missing 5% of the difficult characters keeps exactly half the core. The
    /// base reaches zero only when every difficult character was missed, and the clamp turns anything
    /// past that into a well-defined zero. A map with no difficult characters cannot absorb a miss at
    /// all: any dropped cell zeroes the term rather than producing 0/0.</description></item>
    /// <item><description>THE COMBO BONUS MULTIPLIES THE PRICE INSTEAD OF ADDING TO IT, at a ceiling
    /// that scales with the map: +1% at 200 cells on a straight line through the origin, 10% from 2000
    /// cells up, times the share of the map the longest run held, and times a 1.5 kicker on a
    /// spotless full combo (so a 2000-cell map pays just under +10% for 1999/2000 and +15% for
    /// 2000/2000). Because it is a percentage rather than an amount, a price zeroed by misses stays
    /// zero.</description></item>
    /// </list>
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
    /// WHAT COUNTS AS A MISS AND WHAT COUNTS AS A TYPO (backlog 213). A miss is a cell the play did
    /// not type right: <c>miss + good</c>, i.e. a cell nobody finished PLUS one finished with the
    /// wrong character and never corrected. A typo is a wrong keypress the play recovered from:
    /// <c>max(0, combo_break - good)</c>, the mistype count with the uncorrected ones taken back
    /// out. So ONE FLUB IS PRICED BY EXACTLY ONE TERM: fix it and it stays a typo event, leave it
    /// and it becomes a miss. Both derivations are in <c>CountNotes</c> and both reduce to
    /// the pre-213 ones at <c>good = 0</c>. <c>notes</c> is untouched by the fold, and <c>good</c>
    /// stays in <see cref="NOTE_RESULTS"/>: the cell is one cell of the map however it was typed.
    /// </para>
    ///
    /// <para>
    /// MISSES and TYPOS are priced by SEPARATE terms (backlog 89), and neither appears in the
    /// other's. CLEANLINESS is dropped cells alone, over the plain note count, at the steeper
    /// exponent 10. The TYPO term (wrong keypresses,
    /// <see cref="TypeBeatScoreProcessor.MISTYPE_RESULT"/>, i.e. the <c>combo_break</c> statistics
    /// key) is its own factor at 4. Between backlog 72 and 89 the two rode inside one fraction,
    /// which quietly made each penalty depend on the other: a typo pulled the miss ratio towards
    /// its own value, so a player with a heavy typo count was charged LESS per dropped cell than
    /// a clean one. Split, a play's misses cost the same whatever its keypresses did, and vice
    /// versa.
    /// </para>
    ///
    /// <para>
    /// A typo is still the cheaper of the two failures (4 against 10): a stumble you recover from
    /// is not the same thing as never typing the cell at all. Backlog 95 raised both exponents
    /// (8.5 to 10, 3.5 to 6); the typo one has moved twice since, to 8 in v8 and back to 4 in v9.
    /// </para>
    ///
    /// <para>
    /// BOTH PENALTIES RAISE THE RAW COUNT TO A POWER, NOT THE RATIO (backlog 97), and that power is
    /// <see cref="count_power"/>, a tunable rather than part of the shape (backlog 101). Cleanliness
    /// is <c>max(0, 1 − miss^1.6/notes)</c> and the typo term
    /// <c>max(0, 1 − typos^1.6/(notes + typos))</c>. Backlog 96 squared the RATIO, which runs
    /// the opposite way (a value already in [0, 1] gets SMALLER when squared, so <c>1 − r²</c> is
    /// LARGER than <c>1 − r</c>) and was a misreading of the intent; backlog 97 corrected it at a
    /// power of 2, which was far too extreme, and 101 settled the power at 1.2. v8 then retuned it
    /// to the 1.6 in force.
    /// </para>
    ///
    /// <para>
    /// THE <c>Math.Max</c> CLAMP IS LOAD-BEARING, NOT DEFENSIVE. A powered COUNT over an unpowered
    /// denominator is not bounded by [0, 1] at all: the base crosses zero at
    /// <c>miss = notes^(1/1.6)</c> (49 misses on a 500-note map) and runs NEGATIVE past it, and a
    /// fractional exponent on a negative base is not merely wrong but non-real. Misses really can
    /// equal <c>notes</c> and typos have no bound whatever, so this is the ordinary case and not a
    /// hostile-input guard. Clamped, the term is a well-defined 0 beyond that point: a CLIFF, chosen
    /// knowingly. WHERE it falls is exactly what <see cref="count_power"/> sets, which is why backlog
    /// 101 pulled that lever rather than the exponents; see the backlog-101 amendment in
    /// <c>docs/pp.md</c>, which states the figures.
    /// </para>
    ///
    /// <para>
    /// BOTH NUMERATORS GO THROUGH <c>Math.Pow</c>, WHICH CONVERTS TO DOUBLE FIRST (never
    /// <c>x * x</c> in <c>int</c>). Typos are unbounded, so an <c>int</c> square overflows
    /// catastrophically (at <c>int.MaxValue</c> the true square is about 4.6e18), and a tamper-shaped
    /// note count could do the same to the misses. In double, <c>Math.Pow(int.MaxValue, 1.6)</c> is
    /// about 8.5e14 and the ratio about 4.0e5, so the base clamps to a well-defined zero rather than
    /// wrapping to a NaN or, worse, a bonus. THE COUNTS ARE CLAMPED NON-NEGATIVE BEFORE THEY REACH
    /// THE POWER, and that ordering is load-bearing now the power is FRACTIONAL:
    /// <c>Math.Pow(-1, 1.6)</c> is NaN, not merely a wrong sign. <c>Math.Pow(0, 1.6)</c> is exactly
    /// 0, so both bases are still exactly 1.0 at a count of zero and a spotless play is priced
    /// bit-identically across any retune of the power.
    /// </para>
    ///
    /// <para>
    /// Why the typo term keeps typos on BOTH sides of its fraction while the miss term does
    /// not: misses are bounded by <c>notes</c> (a play cannot drop more cells than the map has), but
    /// keypresses are UNBOUNDED, so a plain <c>typos^1.6/notes</c> would grow without limit and
    /// make the clamp the only thing standing between a masher and a non-real result at any count at
    /// all. Keeping the count in the denominator too moves the zero out to the positive root of
    /// <c>m^1.6 − m − notes = 0</c> (about 51.71, i.e. 52 typos, on a 500-note map) and keeps the
    /// sum itself in <c>double</c>, since <c>notes + typos</c> as <c>int</c> overflows as readily
    /// as the power does. Do not "simplify" that denominator away.
    /// </para>
    ///
    /// <para>
    /// Typos deliberately do NOT enter <c>notes</c>, which stays the map's CELL count
    /// (<c>great + ok + meh + good + miss</c>, one entry per cell, where <c>good</c> is an
    /// uncorrected typo and, since backlog 213, one of the cells the miss term prices). Letting
    /// keypresses inflate it would hand a masher a smaller COMBO
    /// denominator and a bigger Flashlight bonus, paying for the mashing twice over. At zero typos
    /// the typo term is exactly 1.0, so such a play is priced by
    /// <c>max(0, 1 − miss^1.6/notes)^10</c> alone.
    /// </para>
    ///
    /// <para>
    /// THERE IS NO LENGTH FACTOR HERE, AND ADDING ONE BACK WOULD DOUBLE COUNT (backlog 152).
    /// Through v15 this file carried <c>max(0.1, 1 + 0.50·log10(notes/100))</c>, worth up to 1.70x,
    /// while the star rating priced length barely at all. Length now lives entirely in
    /// <see cref="Beatmaps.LyricDifficulty"/>, which since backlog 273 has no separate length term
    /// of its own: length counts only through the characters it adds to the envelope's difficulty
    /// sum, so pp still sees a long map, but only a few percent through SR_eff rather than up to
    /// 70. That is the intended reordering: length stops buying pp it no longer earns.
    /// <c>notes</c> itself stays, and is still load-bearing for both penalty
    /// terms, the combo ratio and <see cref="FlashlightMultiplier"/>.
    /// </para>
    ///
    /// <para>
    /// COMBO IS AN ADDITIVE BONUS AND NOT A FACTOR OF THE PRODUCT (backlog 270). Every generation
    /// from v1 to v20 multiplied the whole play by a combo term, so a broken run scaled the core
    /// pp DOWN; from v12 that term was a log-bent ratio raised to 2.50, tuned so the loss read as
    /// roughly the combo's face value. It is now <c>maxcombo/notes · max(0, combo_bonus_slope ·
    /// (SR_eff − combo_bonus_zero))</c>, ADDED to the finished product (see
    /// <see cref="combo_bonus_slope"/>): a play keeps its core pp whatever its longest run was,
    /// and a full combo collects the whole of a bonus that is worth 25 pp at 3 stars, 50 at 5 and
    /// 75 at 7.
    /// </para>
    ///
    /// <para>
    /// SR_eff is the map's star rating AT THE PLAY'S CLOCK RATE AND ON THE MAP ITS CONVERSION MODS
    /// PRODUCED, never a base rating with a flat bonus bolted on: anything that moves the difficulty
    /// is priced exclusively through the recomputed star rating, so nothing double-counts. Only the
    /// BASE rates are pp-eligible (DT/NC 1.50x, HT 0.75x); a custom rate prices to nothing at all
    /// (<see cref="EligibleRate"/>). The play still ranks on the score leaderboards at every rate
    /// exactly as before, it just earns no pp.
    /// </para>
    ///
    /// <para>
    /// LITERATE IS THE OTHER HALF OF THAT RULE (backlog 144). It is
    /// <see cref="IApplicableAfterBeatmapConversion"/> and makes every punctuation mark a typed
    /// cell, so it is priced through the rating of the CONVERTED map and carries no flat multiplier
    /// (see <see cref="ModMultiplier"/>). It is ORTHOGONAL to the rate, so the two compose into six
    /// ratings per difficulty, which the server stores as
    /// <c>difficulty_rating</c> / <c>sr_dt</c> / <c>sr_ht</c> and
    /// <c>sr_literate</c> / <c>sr_literate_dt</c> / <c>sr_literate_ht</c>. This client stores none
    /// of them: <see cref="StarsFor"/> computes whichever one it needs on the spot.
    /// </para>
    ///
    /// <para>
    /// HALF TIME IS PRICED BY ITS RATING AND NOTHING ELSE, exactly as Double Time is (backlog 265).
    /// From v3 to v19 it carried one extra term, a MIRROR multiplier that made the down-rate factor
    /// the reciprocal of the up-rate one on the same map, on the reading that slowing a map down
    /// lowers SR_eff by far less than speeding it up raises it. That term is gone. It was the only
    /// place in this file where a rate was priced by anything but the rating, it made one rate a
    /// function of all three of a map's ratings (so the server could not price an HT play until
    /// <c>sr_dt</c> was stored), and a degenerate <c>sr_dt</c> zeroed an otherwise honest play. If
    /// Half Time ever reads as underpriced again the fix belongs in the SR model behind the
    /// 0.75x rating, never in a second multiplier here. So the claim docs/pp.md has made since task
    /// 61, that a rate is priced EXCLUSIVELY through SR_eff, is now literally true of both rates.
    /// </para>
    ///
    /// <para>
    /// WHERE THE CLIENT'S STAR RATING COMES FROM, and why it is the same number the server stores:
    /// <see cref="StarsFor"/> runs <see cref="LyricDifficulty.Compute"/> over the map's lyric lines
    /// at the eligible rate and on the eligible stream. That is the identical computation behind
    /// <see cref="TypeBeatDifficultyCalculator"/> and, through the server's mirrored copy of
    /// <c>LyricDifficulty</c>, behind all six stored ratings. Nothing is fetched from the server to
    /// price a play, which is exactly why the client has no "not stored yet" state where the server
    /// has one.
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
        /// <item>v3 = the backlog-90 Half Time penalty: a base-rate HT play is multiplied by a
        /// MIRROR multiplier on top of its 0.75x rating, which makes the down-rate factor the
        /// reciprocal of the up-rate one on the same map (or a flat 0.70 cut where that reciprocal
        /// would be a BUFF). Reprices every stored HT row and nothing else. Removed again at
        /// v20.</item>
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
        /// <item>v8 = a retune of six constants at once (game commit 51f1dc5, 2026-08-08), with the
        /// SHAPE untouched: the global scale drops 4.0 to 3.0, sr_exponent 2.70 to 2.60,
        /// mistype_exponent 6.0 to 8.0, count_power 1.2 to 1.6, length_weight 0.70 to 0.50 and
        /// combo_exponent 0.55 to 0.75. miss_exponent and accuracy_exponent do not move, and neither
        /// does any mod multiplier. It is the ONLY version that shipped without an amendment in
        /// docs/pp.md, so the record of it is its commit message and nothing more: "New pp
        /// coefficients: scale 3, SR 2.6, count power 1.6, mistype exponent 8, length weight 0.5,
        /// combo 0.75". Both penalty bases are still exactly 1.0 at a count of zero, but scale,
        /// sr_exponent, length_weight and combo_exponent price a spotless play too, so this reprices
        /// every stored row rather than only the ones carrying a miss or a mistype. count_power is
        /// the part that moves both CLIFFS, from 178 misses on a 500-note map to 49 and from 249
        /// mistypes to 52.</item>
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
        /// <item>v13 = v13 = count_power 1.6 back to 1.2, restoring the value backlog 101 chose with a
        /// written argument and that v8 silently replaced. The SHAPE is untouched and so is every
        /// other constant. At 1.6 the cleanliness base hit zero at 49 misses on a 500-note map,
        /// 9.7% of it, within a factor of two of the 4.6% that backlog 101 rejected as pricing
        /// essentially every real play to nothing; at 1.2 it is 178, i.e. 35%. It also restores the
        /// mistyping term's separate cliff, which exists because its count sits in its own
        /// denominator: the two cliffs were 52 and 49 at 1.6, three counts apart, and are 249 and
        /// 178 at 1.2. Both bases are still exactly 1.0 at a count of zero, so a spotless play is
        /// priced bit-identically, while every stored row carrying a miss or a mistype is repriced
        /// upwards, many of them away from exactly zero, which is what forces the bump.</item>
        /// <item>v14 = v14 = the global scale drops 12.5 to 9.6, exported from the pp sandbox as the only
        /// change. scale is the one constant that provably cannot move ranking order, within a map
        /// or across maps, since it multiplies every play equally; it rescales absolute pp by 0.768
        /// and nothing else. Every stored row is repriced, which is what forces the bump, but no
        /// leaderboard reorders. Applied on top of v13 rather than the v12 the sandbox export names
        /// as its baseline, because backlog 137 landed count_power 1.6 to 1.2 first.</item>
        /// <item>v15 = the flat Literate multiplier of 1.06 leaves modMult, and Literate is priced
        /// through the star rating of the map it CONVERTS instead. The mod is
        /// IApplicableAfterBeatmapConversion: it makes every supported punctuation mark a typed
        /// cell of its own, so it genuinely changes the map's cell count, its pace and its
        /// difficulty. docs/pp.md has always said a rate is priced EXCLUSIVELY through SR_eff so
        /// that nothing double-counts, and once Literate moves the rating too, a flat multiplier on
        /// top is precisely that double count. The SHAPE of the formula is untouched and no
        /// constant moves; every stored Literate row is repriced and nothing else is, which is what
        /// forces the bump.</item>
        /// <item>v16 = the backlog-152 length migration: the length factor max(0.1, 1 +
        /// 0.50*log10(notes/100)) is DELETED from this file and length is priced by LyricDifficulty
        /// instead, as an additive 0.12*max(0, log10(cells/100)) star bonus. Two length terms would
        /// double count, so pp keeps none: it now sees a long map only as ((SR +
        /// bonus)/SR)^sr_exponent, a few percent where the old term paid up to 1.70x. Long-map
        /// plays therefore deflate hardest (roughly 18% at 340 cells, 28% at 800, 38% at 2300),
        /// which is the intended reordering; the uniform part of that deflation is to be taken out
        /// by re-anchoring scale separately. notes stays, for both penalty terms, the combo ratio
        /// and Flashlight.</item>
        /// <item>v17 = Global scale 9.6 to 12.4, a uniform rescale that holds the median ranked player's
        /// total pp flat across backlog 152.</item>
        /// <item>v18 = the backlog-213 fold of the UNCORRECTED TYPO into the miss. No constant moves
        /// and neither does the SHAPE: what moves is the DERIVATION of two of the formula's three
        /// counts from a play's statistics (<c>CountNotes</c>). <c>misses</c> becomes
        /// <c>miss + good</c> and <c>typos</c> becomes <c>max(0, combo_break - good)</c>, so a flub
        /// the player never fixed is priced by the cleanliness term at exponent 10 instead of the
        /// typo term at 4, and is priced ONCE rather than by both. <c>notes</c> is untouched:
        /// <c>good</c> stays in <see cref="NOTE_RESULTS"/>, because the cell is still one cell of
        /// the map. Every stored row carrying a <c>good</c> is repriced, downwards, which is what
        /// forces the bump; a row with no uncorrected typo is priced bit-identically, since both
        /// derivations reduce to the old ones at <c>good = 0</c>.</item>
        /// <item>v19 = the backlog-227 accuracy SOFT KNEE. The timing term becomes acc^1.80
        /// multiplied by 1/(1 + exp(-(acc - acc_knee)/acc_knee_width)), a logistic whose two new
        /// constants say WHERE the accuracy cliff falls (acc_knee = 0.80) and how sharply
        /// (acc_knee_width = 0.025), each independently of the other and of the exponent. Raising
        /// accuracy_exponent could not do this: an exponent steep enough to price 80% out taxes the
        /// top of the range too (a 95% play keeps 0.912 of the term at 1.80 and only 0.774 at 5),
        /// where the knee costs that same play 0.25%, and 1.8% at 90%, 11% at 85%, HALF at 80%,
        /// while multiplying 75% by 0.12 and 70% by 0.02. Three properties hold at every value of
        /// the two constants: the knee is EXACTLY 0.5 at acc == acc_knee (the argument to exp is 0
        /// there), it is STRICTLY INCREASING so it can never reorder two plays and only respreads
        /// them, and it is finite and smooth over the whole of [0, 1] with no clamp needed, since
        /// accuracy is clamped into that interval first and the argument to exp then stays inside
        /// [-8, +32]. A width of 0 or less MEANS no knee, a real branch in both mirrors that prices
        /// exactly as v18 did. Every stored row under a full accuracy is repriced, downwards and
        /// hardest at the bottom, which is what forces the bump; nothing outside pp moves.</item>
        /// <item>v20 = the backlog-265 removal of the Half Time MIRROR multiplier. v3's extra term
        /// and its 0.70 buff clamp are deleted, so Half Time is priced through its 0.75x rating
        /// alone, exactly as Double Time is priced through its 1.50x one. No constant moves and
        /// neither does the SHAPE: what goes is a whole factor of the product, so every stored
        /// base-rate HT row is repriced UPWARDS by exactly the reciprocal of the multiplier it used
        /// to carry (up to 1/0.70, i.e. +43%, on a row that was taking the clamp), and no other row
        /// moves at all. On the server it also RELAXES a data dependency: an HT play needed both
        /// <c>sr_ht</c> and <c>sr_dt</c> and now needs only <c>sr_ht</c>. A Literate HT play
        /// follows without a branch of its own.</item>
        /// <item>v21 = the backlog-270 combo rewrite and mod-table edit. COMBO STOPS BEING A FACTOR: the
        /// log-bent multiplier (combo_log_shape 9.0, combo_exponent 2.50) is deleted and a BONUS of
        /// maxcombo/notes * max(0, 12.5 * (SR_eff - 1.0)) is ADDED to the finished product, outside
        /// every factor including modMult. A non-FC play therefore keeps its core pp instead of
        /// being crushed by a term that had already been charged for by the miss and typo terms,
        /// and a full combo earns 25 pp at 3 stars, 50 at 5 and 75 at 7. The bonus is deliberately
        /// NOT gated by accuracy: combo does not break on an off-time press, so a full-combo run at
        /// 69 percent collects all of it, and gating it would move top-20 totals by only 1 to 4
        /// percent. MOD TABLE: the dead RH entry (rhythmic_multiplier 1.10) is deleted, which
        /// reprices the one stored Rhythmic row 10 percent down; recite_multiplier 1.07 (RE) and
        /// fletcher_strict_multiplier 1.02 (FC) are added, both mods whose gameplay change no star
        /// rating can see. Every stored row except a zero-combo one is repriced, which is what
        /// forces the bump.</item>
        /// </list>
        /// </summary>
        /// <summary>
        /// v22 is the fork described on the class: the typo term deleted, the miss penalty judged
        /// against the map's difficult characters, the loss curve calibrated in missed fractions, the
        /// combo bonus turned into a map-scaled percentage of the price, and the shape retuned to
        /// the PP Sandbox's dials as they stood then (scale 8, rating exponent 2.30, accuracy
        /// exponent 3, Hard Rock 1.15). EVERY stored row reprices, which is what the bump is for.
        /// </summary>
        /// <summary>
        /// v24 is the PP Sandbox's LIVE dials, re-read from the lab after the owner retuned it. The
        /// Easy multiplier drops 0.9 to 0.85, and the knee position is written as the 0 the lab's
        /// panel holds - inert either way, since the width is 0 and the knee is therefore exactly
        /// 1.0 - so the only live move is Easy's, and it reprices Easy rows alone. Every other dial
        /// already agreed with the lab: scale 9, sr_exponent 2.30, count_power 1.2, acc_steepness
        /// 1.75, acc_floor 0.5, knee off, miss_exponent 13.5134, the combo cap and kicker,
        /// reference_notes 100, Recite 2.0, Hard Rock neutral, Fletcher and No Fail 0.9.
        /// </summary>
        public const int VERSION = 24;

        // ---- formula constants (the PP Sandbox's active dials) ----

        private const double scale = 9.0;               // C: global scale, does not affect ranking order
        private const double sr_exponent = 2.30;

        /// <summary>
        /// <c>ln(2) / ln(1 / 0.95)</c>, rounded: the exponent that puts HALF the core price at a 5%
        /// miss rate with <see cref="count_power"/> 1, on maps of every size.
        /// </summary>
        private const double miss_exponent = 13.5134;

        /// <summary>
        /// The power the MISSED FRACTION carries. 1 is linear in the fraction, which is what makes the
        /// calibrated half-point a miss RATE; higher gives a grace region at low miss rates and a
        /// steeper fall near 100%. The base reaches zero only when every difficult character was
        /// missed, whatever this is set to.
        /// </summary>
        private const double count_power = 1.2;

        // DEPARTURE 5 (v23). The accuracy SHAPE, replacing the shipped acc^accuracy_exponent.
        // Accuracy is rescaled onto [acc_floor, 1] and run through a normalised exponential,
        // which pins both ends for every steepness: the floor is exactly 0 and a perfect play
        // exactly 1. `acc_steepness` is the shape dial - 0 is the straight line from the floor,
        // and higher values hold the price low through the middle of the range before climbing
        // hard over the last few points. These are the PP Sandbox's active dials, which is where
        // the fork is tuned; see tools/pp-sandbox/pp.mjs.
        private const double acc_steepness = 1.75;
        private const double acc_floor = 0.5;

        /// <summary>
        /// WHERE THE ACCURACY CLIFF SITS (backlog 227). The timing term is
        /// <c>acc^accuracy_exponent · 1/(1 + exp(-(acc - acc_knee)/acc_knee_width))</c>: a SOFT KNEE
        /// multiplying the gentle exponent, with this constant setting the accuracy the knee is
        /// centred on and <see cref="acc_knee_width"/> setting how sharply it falls. The two are
        /// independent of each other and of the exponent, which is the whole reason the knee is a
        /// second factor rather than a bigger exponent.
        ///
        /// <para>A BIGGER EXPONENT WOULD TAX THE TOP TOO. Real accuracies here live at 55 to 93 (see
        /// <c>docs/pp.md</c>) and solid plays at 85 to 98, so an exponent steep enough to price 80% out
        /// takes the plays it is not aimed at with it: a 95% play keeps 0.912 of the term at 1.80 and
        /// only 0.774 at 5. The knee costs that same play 0.25%, and 1.8% at 90%, 11% at 85%, HALF at
        /// 80%, while multiplying 75% by 0.12 and 70% by 0.02.</para>
        ///
        /// <para>THE KNEE IS EXACTLY 0.5 AT <c>acc == acc_knee</c> AT EVERY WIDTH, since the argument
        /// to the exponential is then exactly 0 and <c>1/(1 + exp(0))</c> is <c>1/2</c>. So a play
        /// sitting on the knee is priced identically across any retune of the width, and the width
        /// repositions only what sits either side of it, exactly as
        /// <see cref="combo_bonus_slope"/> leaves a play with no combo at all alone.</para>
        ///
        /// <para>IT CANNOT REORDER TWO PLAYS. The logistic is strictly increasing in accuracy and so
        /// is <c>acc^accuracy_exponent</c>, so their product is too: the knee RESPREADS the accuracy
        /// axis and never permutes it. It is also finite and smooth over the whole of <c>[0, 1]</c>
        /// with no clamp needed, since accuracy is clamped into that interval before it gets here: at
        /// a width of 0.025 the argument to <c>Math.Exp</c> runs between -8 and +32, nowhere near the
        /// ~709 at which it overflows to infinity.</para>
        /// </summary>
        // 0 at the PP Sandbox's live dials, which is the position its own panel holds. Inert either
        // way: the width below is 0, this file's declared-absence sentinel, so the whole knee is
        // exactly 1.0. Written as the lab writes it so the two sides do not disagree about a dial
        // they both ignore.
        private const double acc_knee = 0.0;

        /// <summary>
        /// How sharply the knee at <see cref="acc_knee"/> falls: the accuracy interval over which the
        /// factor moves from about 0.27 to about 0.73 (one width either side of the knee). Smaller is
        /// a harder edge.
        ///
        /// <para>A WIDTH OF ZERO OR LESS MEANS THERE IS NO KNEE, and the factor is then exactly 1.0,
        /// i.e. the pre-227 timing term. That is the DECLARED-ABSENCE sentinel: a mirror one
        /// generation behind declares neither constant and prices exactly that, which is what
        /// <c>tools/pp.py</c> reads an absent declaration as. It is a real branch in
        /// <see cref="AccuracyKnee"/> rather than a limit of the formula, which is where it differs
        /// from <see cref="combo_bonus_slope"/>'s 0: a slope of 0 really does make the bonus
        /// exactly 0 for every play, where a logistic has no width that returns 1.0 (a width
        /// tending to 0 gives a STEP, and 0/0 at the knee itself is NaN), so only the branch makes
        /// the sentinel true of the arithmetic as well as of the tool.</para>
        /// </summary>
        // OFF at the sandbox's active dials: the exponential above already does the shaping the
        // knee was added for, and a width of 0 is this file's declared-absence sentinel.
        private const double acc_knee_width = 0.0;

        /// <summary>
        /// WHAT A FULL COMBO IS WORTH, PER STAR (backlog 270). The combo bonus is
        /// <c>maxcombo/notes · max(0, combo_bonus_slope · (SR_eff − combo_bonus_zero))</c>, ADDED
        /// to the finished product rather than multiplied into it, so this constant is a number of
        /// pp per star rather than a fraction of anything: at 12.5 a full combo is worth 25 pp at
        /// 3 stars, 50 at 5 and 75 at 7, and a play with half the map's combo collects half of
        /// that.
        ///
        /// <para>WHY ADDITIVE. Through v20 combo was a FACTOR, so a broken run scaled the whole
        /// play down and stacked on top of the two penalty terms that had already charged for the
        /// flubs that broke it. Non-FC plays were crushed: a real 69% run that never held a long
        /// streak priced at almost nothing however hard the map was. As a bonus, the core pp of a
        /// play is whatever its difficulty, cleanliness, typos and accuracy say it is, and a long
        /// run adds to it.</para>
        ///
        /// <para>THE BONUS IS DELIBERATELY NOT GATED BY ACCURACY, and the consequence was measured
        /// rather than overlooked: combo does not break on an off-time press, so a full-combo run
        /// at 69% accuracy collects the whole bonus. Gating it by
        /// <c>acc^accuracy_exponent · AccuracyKnee(acc)</c> was tried in the sandbox and moves
        /// top-20 totals by 1 to 4%, which is not worth making the bonus a second accuracy term;
        /// the spec is a naive fraction of a naive bonus.</para>
        ///
        /// <para>A SLOPE OF ZERO MEANS THERE IS NO BONUS, exactly, for every play and every
        /// rating, which is what <c>tools/pp.py</c> reads an absent declaration of either constant
        /// as. That needs no branch here: <c>0 · (SR_eff − z)</c> is 0 and <c>max(0, 0)</c> is 0,
        /// so the sentinel is a value of the arithmetic and not merely a reading of it.</para>
        /// </summary>
        private const double combo_bonus_slope = 12.5;

        /// <summary>
        /// THE RATING BELOW WHICH A FULL COMBO IS WORTH NOTHING (backlog 270): the bonus is
        /// <c>max(0, combo_bonus_slope · (SR_eff − combo_bonus_zero))</c>, so it opens at
        /// <c>SR_eff = combo_bonus_zero</c> and grows linearly above it. The <c>Math.Max</c> is
        /// load-bearing and not defensive: a real map can rate below 1.0 (a map with no window long
        /// enough to fit the smallest scheduled duration rates EXACTLY 0 under the envelope model,
        /// there being no length term left to give it anything else), and without the clamp
        /// such a play would be handed a NEGATIVE bonus that a long run made worse.
        /// </summary>
        private const double combo_bonus_zero = 1.0;

        // ---- v22 combo bonus, which REPLACED the two constants above ----
        //
        // They are kept (and referenced by nothing) because they are the record of what v21 priced:
        // the fork's bonus is a PERCENTAGE of the price rather than a number of pp added beside it,
        // and its ceiling grows with the map instead of being a flat pp amount a short map and a long
        // one collect alike.

        /// <summary>
        /// The percentage a FULL COMBO is worth on a 200-cell map. The ceiling is a straight line
        /// through the origin, so this one number also sets the slope: 1% here is 2% at 400 cells, 5%
        /// at 1000 and 10% at 2000, where <see cref="combo_bonus_cap"/> takes over. 0 removes the
        /// bonus exactly.
        /// </summary>
        private const double combo_bonus_at_200_cells = 1.0;

        /// <summary>
        /// The most the combo bonus can ever be worth, as a percentage of the price, on any map.
        /// </summary>
        private const double combo_bonus_cap = 10.0;

        /// <summary>
        /// What a SPOTLESS full combo (every cell typed, none dropped) multiplies its own ceiling by:
        /// a 2000-cell map pays +15% for 2000/2000 and just under +10% for 1999/2000. 1 removes the
        /// kicker and leaves the line unbroken.
        /// </summary>
        private const double combo_bonus_perfect = 1.5;

        /// <summary>
        /// The pivot of <see cref="FlashlightMultiplier"/>'s log bonus: 100 notes is where it is
        /// worth exactly <c>1 + flashlight_offset</c>. It was shared with the length bonus until
        /// backlog 152 deleted that; Flashlight owns it alone now, and it stays here rather than
        /// moving into the mod block because it is a property of the note count, not of the mod.
        /// </summary>
        private const double reference_notes = 100.0;

        // ---- mod multipliers (docs/pp.md) ----

        // THE NUMBERS IN THIS BLOCK ARE CHOSEN, NOT DERIVED FROM THE SCORE MULTIPLIERS. Two of
        // them (recite_multiplier and fletcher_strict_multiplier) happen to equal the mod's score
        // multiplier because the user picked the same number twice, and that is a coincidence
        // rather than a rule: Easy is 0.75 here against 0.5x score, Hard Rock 1.25 against 1.10x,
        // No Fail 0.90 against 0.5x and Flashlight a length-scaled bonus against a flat 1.12x.
        // Never read one table off the other.
        //
        // Rhythmic (RH) had an entry here from backlog 135 until backlog 270, at 1.10. The mod
        // itself went in backlog 147, so no client can send the acronym and exactly one stored row
        // still carries it; that row reprices 10% down at v21, which is a VERSION bump doing what
        // a VERSION bump is for. The XMLDoc that argued against this deletion cited
        // ModMultiplier.TotalScoreCeiling, which is a DIFFERENT FILE (the server's
        // Scoring/ModMultiplier.cs, the score-side table): that table still prices "RH" at 1.10
        // and is untouched here, so the row's stored total stays under its ceiling and stays
        // ranked.

        /// <summary>
        /// Recite (backlog 236): the lyric text is hidden until the line is sung, so the play is
        /// typed from listening rather than from reading ahead. Nothing about the map changes, so
        /// there is no converted rating to price it through and it takes a flat term, exactly as
        /// Easy and Hard Rock do.
        /// </summary>
        private const double recite_multiplier = 2.0;

        /// <summary>
        /// Fletcher (backlog 208): the caret is PINNED back to the line the song is on, which is
        /// the reverse of the mod's original meaning and the harder half of it, since the unpinned
        /// caret became the default for every play. The cells, their target times and the map's
        /// pace are identical, so like Easy and Hard Rock it is priced flat here.
        ///
        /// <para>DISTINCT FROM <see cref="fletcher_multiplier"/>, which is the retired <c>FT</c>
        /// acronym at 0.90: <c>FT</c> is a <see cref="ModType.System"/> mod nobody can select,
        /// kept resolvable so its stored rows keep their price, and it means the OPPOSITE thing
        /// (an unpinned caret, back when that was the mod rather than the default).</para>
        /// </summary>
        private const double fletcher_strict_multiplier = 1.02;

        /// <summary>
        /// Easy (backlog 149): the play was judged on DOUBLED windows, so every character was twice
        /// as forgiving to land. Priced as the difficulty reduction it is, at the PP Sandbox's LIVE
        /// dial (0.85; the lab's own panel is where the value is chosen). Flat rather than routed
        /// through the star rating, unlike Literate: the mod converts nothing (the cells, their
        /// target times and the map's pace are identical), it only widens the tolerance around each
        /// target, which no rating input can see.
        /// </summary>
        private const double easy_multiplier = 0.85;

        /// <summary>
        /// Hard Rock (backlog 150): the play was judged on HALVED windows, the exact mirror of Easy,
        /// so every character was half as forgiving to land. Priced flat for the same reason Easy is
        /// (the mod converts nothing, so no rating input can see it), at a value the user chose on
        /// 2026-08-13. Deliberately NOT the reciprocal of <see cref="easy_multiplier"/> (1.333...):
        /// the window scales mirror each other, the prices need not, and a tighter window costs a
        /// player less than a wider one gives them. Separate from the mod's 1.10x SCORE multiplier,
        /// exactly as Easy's flat term is separate from its 0.5x.
        /// </summary>
        // NEUTRAL at the PP Sandbox's active dials: Hard Rock's judgement windows are what the
        // rhythm arm reads, so the sandbox leaves the flat term at 1.0 rather than paying for the
        // same change twice.
        private const double hard_rock_multiplier = 1.0;

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
        /// <c>["perfect", "great", "ok", "meh", "good", "miss"]</c> statistics keys.
        /// <see cref="HitResult.IgnoreHit"/> is deliberately absent: the line containers are
        /// ignore_hit judgements and counting them would inflate <c>notes</c> and dilute every
        /// factor it appears in (cleanliness, typos, combo, Flashlight).
        ///
        /// <para><see cref="HitResult.Perfect"/> is kept AFTER backlog 147 took the fourth quality
        /// tier back out, and it is not dead weight: the four-tier judgement shipped, so rows
        /// submitted in that window carry a <c>perfect</c> key, and dropping it here would read
        /// those rows as maps with almost no notes, hardening both penalty terms and inflating the
        /// combo ratio.
        /// No client can produce one any more, so for every play made under the current rules the
        /// entry contributes exactly nothing.</para>
        ///
        /// <para><see cref="UNFIXED_TYPO_RESULT"/> IS a note, and that is the pp half of backlog 124
        /// and 126. It is one cell of the map, so leaving it out would shorten the map pp thinks the
        /// player played, hardening both penalty terms and inflating the combo ratio. Backlog 213
        /// does NOT change that: the key stays on this list, and what changed is only which of the
        /// two penalty terms it feeds. Through v17 it was deliberately not a
        /// <see cref="MISS_RESULT"/>, on the reading that a cell finished wrongly is not a cell
        /// nobody finished; since v18 it is counted as one, because the two say the same thing about
        /// the play (the character is not there) and pricing them apart charged one flub through the
        /// typo term while completion had already charged it as a miss.</para>
        /// </summary>
        public static readonly IReadOnlyList<HitResult> NOTE_RESULTS = new[]
        {
            HitResult.Perfect,
            HitResult.Great,
            HitResult.Ok,
            HitResult.Meh,
            TypeBeatResultMapping.UNFIXED_TYPO,
            HitResult.Miss,
        };

        /// <summary>The <see cref="NOTE_RESULTS"/> member that is a DROPPED cell: nobody typed it.</summary>
        public const HitResult MISS_RESULT = HitResult.Miss;

        /// <summary>
        /// The <see cref="NOTE_RESULTS"/> member that is an UNCORRECTED TYPO: the player finished
        /// the cell with the wrong character and never went back for it (the <c>good</c> statistics
        /// key, see <see cref="TypeBeatResultMapping.UNFIXED_TYPO"/>, which this aliases rather than
        /// re-states).
        ///
        /// <para>Since backlog 213 it is priced as a MISS and not as a typo: see
        /// <c>CountNotes</c> for the derivation and for why the wrong keypress that produced
        /// it is taken back out of the typo count.</para>
        /// </summary>
        public const HitResult UNFIXED_TYPO_RESULT = TypeBeatResultMapping.UNFIXED_TYPO;

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
        /// Notes, misses and typos for a play, as the formula defines them. <see cref="Typos"/>
        /// defaults to 0 so a play carrying no typo count prices exactly as it always did.
        /// </summary>
        public readonly record struct NoteCounts(int Notes, int Misses, int Typos = 0)
        {
            /// <summary>
            /// THE DIFFICULT CHARACTERS of the map the play was set on, at the played rate and on
            /// the played stream: what the miss penalty is judged against (see the class docs). It is
            /// a property of the MAP rather than of the play, so a caller that cannot see the map's
            /// lyric lines (a stored score with no local beatmap) leaves it at 0, which prices any
            /// miss as a zeroed cleanliness term rather than silently falling back to cells.
            /// </summary>
            public double DifficultCharacters { get; init; }
        }

        /// <summary>
        /// Notes, misses and typos from a play's judgement counts: the live
        /// <see cref="ScoreProcessor.Statistics"/> mid-play, or a finished score's
        /// <see cref="ScoreInfo.Statistics"/>. Negative counts (which only a rewind race could
        /// produce) contribute nothing rather than subtracting, and an absent
        /// <see cref="MISTYPE_RESULT"/> reads as 0 typos.
        ///
        /// <para>Since backlog 213 <c>misses</c> and <c>typos</c> are DERIVED rather than read
        /// straight off two keys: <c>misses = miss + good</c> and
        /// <c>typos = max(0, combo_break - good)</c>. The body says why.</para>
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

                // Backlog 213: an UNCORRECTED TYPO is a MISS. It keeps its own statistics key (the
                // wire does not move, so old rows stay comparable and the distinction survives in
                // the data), and this is the pp half of every consumer reclassifying instead.
                if (key == MISS_RESULT || key == UNFIXED_TYPO_RESULT)
                    misses += count;
            }

            int unfixedTypos = statistics.TryGetValue(UNFIXED_TYPO_RESULT, out int unfixedCount) && unfixedCount > 0 ? unfixedCount : 0;
            int typos = statistics.TryGetValue(MISTYPE_RESULT, out int typoCount) && typoCount > 0 ? typoCount : 0;

            // NO DOUBLE JEOPARDY, which is the other half of the fold. Every uncorrected typo cell
            // implied a wrong KEYPRESS, and that keypress is already in combo_break, so charging the
            // miss term for the cell AND the typo term for the keypress would price one flub twice.
            // A corrected typo stays a typo event (its cell resolved as an ordinary hit, so nothing
            // subtracts it); an uncorrected one becomes a miss and leaves the typo term.
            //
            // The clamp is load-bearing rather than defensive: the two counts arrive off the wire
            // independently, so good can exceed combo_break on a row stored before the mistype stat
            // existed at all (no combo_break key, backlog 72) and on any tamper-shaped dictionary. A
            // negative typo count would raise Math.Pow(typos, count_power) to a NaN under the
            // fractional power, or price the play ABOVE a clean one.
            return new NoteCounts(notes, misses, Math.Max(0, typos - unfixedTypos));
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
        /// The acronym of the LITERATE mod. Keyed on the acronym rather than on
        /// <see cref="Mods.TypeBeatModLiterate"/> for the same reason
        /// <see cref="TryGetBaseRate"/> is: the acronym is what travels on the wire and what this
        /// implementation and the server's share, where the mod TYPE exists only here.
        /// </summary>
        public const string LITERATE_ACRONYM = "LT";

        /// <summary>Whether this stack carries Literate, i.e. whether the play is on the CONVERTED map.</summary>
        /// <summary>
        /// Which judgement arm a play is rated in, from its mods: the two mods that change the
        /// engine's own windows rather than the map (Easy and Hard Rock, which are mutually
        /// exclusive), read the way <see cref="IsLiterate"/> reads the typed stream. Everything else
        /// rates in the live span rule.
        ///
        /// <para>THE ARM IS A RATING INPUT, not a filter over the finished price: the star rating
        /// prices the intervals a press may land in, so a wider window or a point target is a
        /// different rating, exactly as it is in the sandbox's <c>complexityMod</c> dial. It is
        /// deliberately NOT a multiplier here - that would charge the same change twice, once in the
        /// rating and once in <see cref="ModMultiplier"/>.</para>
        /// </summary>
        public static LyricDifficulty.JudgementArm JudgementArmFor(IReadOnlyList<Mod>? mods)
        {
            if (mods == null)
                return LyricDifficulty.JudgementArm.None;

            for (int i = 0; i < mods.Count; i++)
            {
                if (mods[i] is TypeBeatModEasy)
                    return LyricDifficulty.JudgementArm.Easy;

                if (mods[i] is TypeBeatModHardRock)
                    return LyricDifficulty.JudgementArm.HardRock;
            }

            return LyricDifficulty.JudgementArm.None;
        }

        public static bool IsLiterate(IReadOnlyList<Mod>? mods)
        {
            if (mods == null)
                return false;

            for (int i = 0; i < mods.Count; i++)
            {
                if (string.Equals(mods[i].Acronym?.Trim(), LITERATE_ACRONYM, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// The star rating this play is priced at, or null when its rate makes it pp-ineligible:
        /// <see cref="LyricDifficulty.Compute"/> over the map's lyric lines at
        /// <see cref="EligibleRate"/>, and on the stream <see cref="IsLiterate"/> selects. This is
        /// the client-side stand-in for the server's SIX stored ratings, and produces the same
        /// numbers because both sides run the same mirrored difficulty code (see the class docs).
        ///
        /// <para>LITERATE IS APPLIED HERE, NOT IN <see cref="ModMultiplier"/> (backlog 144). The mod
        /// converts the beatmap, adding a typed cell per punctuation mark, so the map it is played
        /// on is a different map and is rated as one. It composes with the rate rather than
        /// replacing it: a Literate Double Time play is the CONVERTED map's rating at 1.50x, which
        /// is the server's <c>sr_literate_dt</c> and is a genuinely separate number from either
        /// <c>sr_literate</c> or <c>sr_dt</c>.</para>
        /// </summary>
        /// <summary>
        /// THE DIFFICULT CHARACTERS of the map those lines describe, at the played rate and on the
        /// played stream: the envelope model's own N, which is what the miss penalty is judged
        /// against. 0 for a rate the formula cannot price, which prices any miss on such a play as a
        /// zeroed cleanliness term rather than falling back to the map's cell count.
        /// </summary>
        public static double DifficultCharactersFor(IEnumerable<LyricLine> lines, IReadOnlyList<Mod>? mods)
            => EligibleRate(mods) is double rate
                ? LyricDifficulty.ComputeDetail(lines, rate, IsLiterate(mods), LyricDifficulty.Live, JudgementArmFor(mods)).DifficultCharacters
                : 0;

        public static double? StarsFor(IEnumerable<LyricLine> lines, IReadOnlyList<Mod>? mods)
            => EligibleRate(mods) is double rate ? LyricDifficulty.Compute(lines, rate, IsLiterate(mods), LyricDifficulty.Live, JudgementArmFor(mods)) : (double?)null;

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
        ///
        /// <para>Easy IS a flat term here (<see cref="easy_multiplier"/>), and so is Hard Rock
        /// (<see cref="hard_rock_multiplier"/>), and that is not in tension with the Literate rule
        /// below: scaling the judgement windows changes nothing the star rating is computed from, so
        /// there is no converted map to price either of them through. The RATE mods scale the same
        /// windows since backlog 150 and still carry no term here, because a rate does move the
        /// rating and is priced exclusively through it.</para>
        ///
        /// <para>THERE IS NO LITERATE TERM HERE EITHER, and for exactly the reason there is no rate
        /// one (backlog 144). <see cref="Mods.TypeBeatModLiterate"/> is
        /// <see cref="IApplicableAfterBeatmapConversion"/>: it turns every punctuation mark into a
        /// typed cell, so it changes the map's cell count, its pace and its difficulty, and it is
        /// priced through <see cref="StarsFor"/> like a rate. It used to carry a flat 1.06 ON TOP of
        /// the unconverted map's rating, which was the only place in this file where a mod that
        /// moves the rating was also paid a multiplier; keeping both once the rating moves would be
        /// precisely the double count docs/pp.md exists to forbid. The flat number was also a poor
        /// description of the mod: measured over the five reference maps the honest rate-1.0 rating
        /// moves between -0.8% and +6.3%, i.e. Literate makes two of them EASIER, where a flat 1.06
        /// paid every map the same 6%.</para>
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
                    case "FL":
                        multiplier *= FlashlightMultiplier(notes);
                        break;

                    case "EZ":
                        multiplier *= easy_multiplier;
                        break;

                    case "HR":
                        multiplier *= hard_rock_multiplier;
                        break;

                    case "RE":
                        multiplier *= ReciteMultiplierFor(notes);
                        break;

                    case "FC":
                        multiplier *= fletcher_strict_multiplier;
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

        /// <summary>
        /// DEPARTURE 6 (v23). Recite is a MULTIPLIED Flashlight bonus rather than a flat term:
        /// <c>1 + recite_multiplier * (FlashlightMultiplier(notes) - 1)</c>. The mod hides the
        /// lyric until the line is sung, which is what Flashlight charges for - the map is typed
        /// from memory rather than read ahead - and that cost grows with how much map there is to
        /// hold in the head. So <see cref="recite_multiplier"/> is the SCALE on that bonus, not a
        /// bonus of its own: 0 is free, 1 is exactly what Flashlight is worth, and the sandbox's
        /// active 2.0 pays twice Flashlight's bonus. The two mods still multiply when both are
        /// selected, exactly as every other pair in the table does.
        /// </summary>
        public static double ReciteMultiplierFor(int notes)
            => 1 + recite_multiplier * (FlashlightMultiplier(notes) - 1);

        /// <summary>
        /// DEPARTURE 5 (v23). The share of its core pp a play keeps from accuracy alone:
        /// <c>ExpCurve((accuracy - acc_floor) / (1 - acc_floor))</c>, so the floor is exactly
        /// where the price reaches zero and a perfect play is exactly 1. The normalisation is
        /// what makes the two dials independent - the steepness moves the shape without moving
        /// either end.
        /// </summary>
        private static double AccuracyShare(double accuracy)
        {
            if (!double.IsFinite(accuracy) || accuracy <= acc_floor)
                return 0;

            double t = accuracy >= 1 ? 1 : (accuracy - acc_floor) / (1 - acc_floor);
            double share = ExpCurve(t, acc_steepness);
            return double.IsFinite(share) ? Math.Clamp(share, 0, 1) : 0;
        }

        /// <summary>
        /// The normalised exponential both the accuracy curve and its steepness dial are built
        /// on: <c>(e^(k*t) - 1) / (e^k - 1)</c>, exactly 0 at t = 0 and exactly 1 at t = 1 for
        /// every k. k = 0 is the limit and is taken directly rather than through the ratio, which
        /// is 0/0 there: the curve is then the straight line from the floor to perfection.
        /// </summary>
        private static double ExpCurve(double t, double steepness)
            => steepness > 1e-6 ? (Math.Exp(steepness * t) - 1) / (Math.Exp(steepness) - 1) : t;

        /// <summary>
        /// The accuracy SOFT KNEE (backlog 227): a logistic centred on <see cref="acc_knee"/> and
        /// <see cref="acc_knee_width"/> wide, multiplying the timing term. See those two constants
        /// for what the dial does and why it is not simply a steeper exponent.
        /// </summary>
        private static double AccuracyKnee(double accuracy)
        {
            // A width of zero or less MEANS there is no knee, and the factor is then exactly 1.0
            // rather than the step function the logistic degenerates to (see acc_knee_width). Written
            // as a conditional expression rather than an if, so the dead half of a compile-time
            // constant folds away instead of reading as unreachable code.
            return acc_knee_width > 0
                ? 1.0 / (1.0 + Math.Exp(-(accuracy - acc_knee) / acc_knee_width))
                : 1.0;
        }

        /// <summary>
        /// pp for one play. <paramref name="starRating"/> is the play's EFFECTIVE rating
        /// (<see cref="StarsFor"/>), <paramref name="accuracy"/> the standardised hit accuracy and
        /// <paramref name="maxCombo"/> the highest combo reached.
        ///
        /// <para>Inputs are clamped rather than trusted: misses and combo into <c>[0, notes]</c>
        /// (the theoretical max combo of a typing map IS its note count), typos to non-negative
        /// (they have no upper bound: a player can press as many wrong keys as they like) and
        /// accuracy into <c>[0, 1]</c>. The result is guaranteed finite and non-negative.</para>
        ///
        /// <para>THERE IS NO RATE ARGUMENT (backlog 265). A rate is priced entirely by the
        /// <paramref name="starRating"/> it is handed, so a caller holding the effective rating
        /// holds the whole price. From v3 to v19 a base-rate Half Time play took an extra
        /// multiplier here, which every surface that could see one had to remember to pass; nothing
        /// does now, and the forgetting-to-pass-it failure mode is gone with it.</para>
        /// </summary>
        public static double Compute(
            double starRating,
            int notes,
            double difficultCharacters,
            int misses,
            double accuracy,
            int maxCombo,
            IReadOnlyList<Mod>? mods,
            int typos = 0)
        {
            // No notes describes no play; a zero or non-finite rating prices nothing.
            if (notes <= 0 || !double.IsFinite(starRating) || starRating <= 0)
                return 0;

            misses = Math.Clamp(misses, 0, notes);
            maxCombo = Math.Clamp(maxCombo, 0, notes);
            difficultCharacters = double.IsFinite(difficultCharacters) ? Math.Max(0, difficultCharacters) : 0;
            accuracy = double.IsFinite(accuracy) ? Math.Clamp(accuracy, 0, 1) : 0;

            double difficulty = Math.Pow(starRating, sr_exponent);

            // CLEANLINESS, over the map's DIFFICULT CHARACTERS rather than its cells, with the
            // power on the missed FRACTION. Two special cases are load-bearing rather than
            // defensive: a spotless play must be exactly 1.0 on every map, and a map whose difficulty
            // sits entirely below its own peak has NO difficult characters to spend, so any dropped
            // cell zeroes the term instead of producing 0/0. The clamp is load-bearing too: misses
            // are cells and a map has fewer difficult characters than cells, so the fraction can
            // exceed 1 and the base would run negative, where a fractional power is non-real rather
            // than merely wrong.
            double difficultShare = difficultCharacters > 0 ? misses / difficultCharacters : 0;
            double missBase = misses == 0 ? 1
                : difficultCharacters > 0 ? Math.Max(0, 1 - Math.Pow(difficultShare, count_power))
                : 0;
            double cleanliness = Math.Pow(missBase, miss_exponent);

            // THERE IS NO TYPO TERM: a wrong keypress the player recovered from costs nothing. The
            // count is still derived by CountNotes for the surfaces that display it, and the
            // parameter stays on this signature so those call sites read unchanged.

            // The play's accuracy, curved by accuracy_exponent and then bent through the SOFT KNEE
            // (see acc_knee): the exponent keeps doing the ordering work above the knee while the
            // logistic takes the bottom of the range out. NO CLAMP IS NEEDED HERE and none would
            // bite: accuracy is clamped into [0, 1] above, so at a width of 0.025 the argument to
            // Math.Exp runs between -32 and +8 and the knee between 3.4e-4 and 0.9997. The knee is
            // exactly 0.5 at accuracy == acc_knee and strictly increasing everywhere, so it can
            // respread this axis but never reorder two plays on it.
            // DEPARTURE 5 (v23): the accuracy curve above replaces the shipped power curve. The
            // knee still multiplies it, but ships at width 0 at the sandbox's active dials, where
            // it is exactly 1 and the curve is the whole of the accuracy shape.
            double timing = AccuracyShare(accuracy) * AccuracyKnee(accuracy);

            // THE COMBO BONUS MULTIPLIES THE PRICE, at a ceiling this map's LENGTH earns it: a
            // straight line through the origin worth combo_bonus_at_200_cells percent at 200 cells,
            // capped, times the share of the map the longest run held, times the kicker when nothing
            // was dropped at all. Because it is a percentage of the product rather than a number of
            // pp beside it, a price zeroed by misses stays zero, and the mod multiplier scales the
            // bonus along with everything else it multiplies.
            double comboCeiling = Math.Min(combo_bonus_cap, Math.Max(0, notes) / 200.0 * combo_bonus_at_200_cells) / 100.0;
            double comboRatio = (double)maxCombo / notes;
            bool comboPerfect = misses == 0 && maxCombo >= notes;
            double comboBonus = comboCeiling * comboRatio * (comboPerfect ? combo_bonus_perfect : 1);

            // THE BONUS IS INSIDE THE PRODUCT, deliberately, because it is a percentage OF the
            // price and not a number of pp beside it: a mod stack scales it along with the core,
            // and a core zeroed by misses cannot keep a consolation bonus. That is the opposite of
            // v21's placement, which is what the version bump records.
            double core = scale * difficulty * cleanliness * timing * ModMultiplier(mods, notes);
            double pp = core * (1 + comboBonus);

            return double.IsFinite(pp) && pp > 0 ? pp : 0;
        }

        /// <summary>
        /// pp for a play whose counts have already been read: the whole per-play pipeline in one
        /// call, so the live HUD counter and any end-of-play consumer can never disagree about the
        /// argument order. Mirrors the server's <c>ForScore</c> minus its storage concerns (the
        /// ranked flag, the settled/pending distinction), which have no client-side meaning.
        ///
        /// <para>The rating is the WHOLE price of a rate since backlog 265, so a caller holding
        /// <see cref="StarsFor"/>'s answer needs nothing else; there is no second half to fetch,
        /// and no surface can under- or over-pay a Half Time play by forgetting one.</para>
        /// </summary>
        public static double ForPlay(double starRating, NoteCounts counts, double accuracy, int maxCombo, IReadOnlyList<Mod>? mods)
            => Compute(starRating, counts.Notes, counts.DifficultCharacters, counts.Misses, accuracy, maxCombo, mods, counts.Typos);
    }
}
