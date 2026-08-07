// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Scoring
{
    /// <summary>
    /// What the GAME shows for a play's performance points, and how it shows it.
    ///
    /// <para>
    /// This is deliberately NOT part of <see cref="PerformancePoints"/>. That type is a
    /// byte-for-byte mirror of the server's <c>Typebeat.Web.Scoring.PerformancePoints</c>, pinned by
    /// the WireCompat parity test; it prices a play and nothing else, and it re-derives no
    /// eligibility beyond the clock rate (exactly as the server does not, since the server inherits
    /// the rest from the score's stored <c>ranked</c> flag). The rules for what a CLIENT surface may
    /// legitimately print are client-only, so they live here, in one place, shared by the surfaces
    /// that print them: the live counter in <see cref="UI.TypeBeatHudOverlay"/> and the results
    /// screen's Completion table (<see cref="TypeBeatRuleset.CreateCompletionStatistics"/>). The
    /// results screen's OTHER pp readout, the one in the score panel, is a shared component that
    /// cannot reference this assembly; it reaches the same eligibility rule through
    /// <see cref="TypeBeatRuleset.ScoreEarnsPerformancePoints"/> and the same value through
    /// <see cref="TypeBeatPerformanceCalculator"/>, which is what stops the two readings on one
    /// screen from disagreeing.
    /// </para>
    ///
    /// <para>
    /// THE THREE STATES A READOUT CAN BE IN, and why they must stay distinct:
    /// <list type="bullet">
    /// <item><b>A number.</b> The play can earn pp and this is what it is worth. Note that 0 is a
    /// perfectly ordinary member of this state: a give-up run prices to 0 and is still an eligible,
    /// priced play.</item>
    /// <item><b>Ineligible</b> (<see cref="INELIGIBLE_TEXT"/>). The play can NEVER earn pp, so no
    /// number describes it. Rendering it as "0" would collapse it into the state above and make an
    /// unranked-mod run look like a worthless one.</item>
    /// <item><b>Not priced by the server.</b> Not a rendering at all: a score that never reached the
    /// server, or that the server declined to price, carries no value, and the game decides for
    /// itself (see <see cref="ForScore"/>). This rests on a wire contract the server holds up its
    /// end of: <see cref="ScoreInfo.PP"/> carries a number ONLY for a play the server actually ran
    /// the formula for, so a value that is present is proof the play was eligible, and one that is
    /// absent is never a disguised zero.</item>
    /// </list>
    /// </para>
    /// </summary>
    public static class PerformancePointsDisplay
    {
        /// <summary>
        /// What every surface shows for a play that can never earn pp. Deliberately not a number:
        /// see the class docs.
        /// </summary>
        public const string INELIGIBLE_TEXT = "-";

        /// <summary>
        /// The star rating to price this play at, or null when the play is pp-INELIGIBLE for a
        /// reason that is knowable BEFORE it ends. The rating comes from the map's own lyric lines
        /// at the play's clock rate, i.e. the same <see cref="LyricDifficulty"/> pass that fills
        /// <see cref="TypeBeatDifficultyCalculator"/> and, through the server's mirrored copy of it,
        /// the stored <c>difficulty_rating</c> / <c>sr_dt</c> / <c>sr_ht</c> columns. Nothing is
        /// fetched from the server.
        ///
        /// <para>The three gates are the three the server would refuse to pay for:</para>
        /// <list type="number">
        /// <item>a CUSTOM rate (only the DT/NC 1.50x and HT 0.75x base rates earn pp, docs/pp.md);
        /// applied inside <see cref="PerformancePoints.StarsFor"/>;</item>
        /// <item>any UNRANKED mod in the stack (Mashing, Autoplay, Wind Up/Down, ...), which makes
        /// the submission path store the score <c>ranked = false</c>;</item>
        /// <item>a map that grants no pp (anything not Ranked/Approved: a local map, an unsubmitted
        /// map, a work in progress).</item>
        /// </list>
        ///
        /// <para>A FAILED play is NOT gated here, because failing is not knowable in advance and the
        /// live counter's contract is "what this play is worth if it ends right here". It IS gated
        /// once the play is over; see <see cref="Eligible"/>, which adds that one gate and is what
        /// the results screen uses. The two overlap on the first three deliberately: a play cannot
        /// be worth anything without a rating, and a rating exists only where those hold.</para>
        /// </summary>
        public static double? StarRatingFor(IBeatmap? playableBeatmap, IReadOnlyList<Mod>? mods)
        {
            if (playableBeatmap == null)
                return null;

            if (!playableBeatmap.BeatmapInfo.Status.GrantsPerformancePoints())
                return null;

            if (mods != null && mods.Any(m => !m.Ranked))
                return null;

            return PerformancePoints.StarsFor(playableBeatmap.HitObjects.OfType<TypeBeatHitObject>().Select(h => h.Line), mods);
        }

        /// <summary>
        /// The rate multiplier that goes with <see cref="StarRatingFor"/>: 1.0 for everything
        /// except a base-rate Half Time play, which is additionally priced by
        /// <see cref="PerformancePoints.HalfTimeMultiplier"/> (backlog 90). Kept beside the rating
        /// rather than folded into it because it is a multiplier on the PRICE, not on the
        /// difficulty: the rating a surface shows for an HT play is still plain <c>sr_ht</c>.
        ///
        /// <para>No gates of its own. It answers 1.0 for a map that is null or ineligible, which is
        /// harmless because those never reach a price at all (<see cref="StarRatingFor"/> returns
        /// null and the caller stops there).</para>
        /// </summary>
        public static double RateMultiplierFor(IBeatmap? playableBeatmap, IReadOnlyList<Mod>? mods)
            => playableBeatmap == null
                ? 1
                : PerformancePoints.RateMultiplier(playableBeatmap.HitObjects.OfType<TypeBeatHitObject>().Select(h => h.Line), mods);

        /// <summary>
        /// What a FINISHED score is worth, or null when it can never be worth anything (rendered as
        /// <see cref="INELIGIBLE_TEXT"/>).
        ///
        /// <para>A STORED value wins outright, ahead of every local gate. The server sends a number
        /// only for a play it actually ran the formula for, and null for everything else (an
        /// ineligible play, or one it cannot price yet), so a stored value is proof of eligibility
        /// by itself and needs no second opinion. It is also the authoritative number: it is what
        /// the leaderboards and the profile count, it was priced against the server's own stored
        /// star ratings, and it can encode refusals the client cannot see at all (the play-time
        /// gate, an out-of-bounds total, a blocked build). Taking it ahead of the gates is what
        /// keeps a genuinely earned number on screen when the local copy of the map has drifted from
        /// the ranked one the play was set on: opening it in the editor marks it LocallyModified,
        /// which the gates would otherwise read as earning nothing.</para>
        ///
        /// <para>With no stored value, <see cref="Eligible"/> decides between a dash and a local
        /// calculation. The local calculation covers most plays there are: an offline play, an
        /// imported <c>.osr</c>, a replay downloaded from the website, and any play whose submission
        /// failed. Those price identically to a submitted one, because both sides run the same
        /// formula over the same star rating.</para>
        ///
        /// <para>REPLAYS: watching a replay re-simulates it, and
        /// <see cref="ScoreProcessor.PopulateScore"/> drops the stored pp when it overwrites the
        /// statistics that pp was derived from. So a replay's reading is the pp OF THE SIMULATION,
        /// computed here, consistent with every other row of the same table (completion, missed
        /// characters, mistypes), which is likewise the simulation's. Opening the same score's
        /// results directly, without replaying it, still shows the recorded server value.</para>
        /// </summary>
        /// <param name="score">The finished score.</param>
        /// <param name="playableBeatmap">The beatmap the score was set on, converted with its mods.</param>
        public static double? ForScore(ScoreInfo score, IBeatmap? playableBeatmap)
        {
            if (score.PP is double stored)
                return stored;

            if (!Eligible(score) || StarRatingFor(playableBeatmap, score.Mods) is not double stars)
                return null;

            return PerformancePoints.ForPlay(stars, PerformancePoints.CountNotes(score), score.Accuracy, score.MaxCombo, score.Mods,
                RateMultiplierFor(playableBeatmap, score.Mods));
        }

        /// <summary>
        /// Whether a finished play COULD have earned pp: the gates of <see cref="StarRatingFor"/>
        /// read off the score itself, plus the one the live counter cannot have, a FAIL. Failing is
        /// unknowable while a play is running, which is why the HUD keeps counting through a run
        /// that might still be recovered or no-failed; once the play is over it is settled, and a
        /// failed run can never be worth anything.
        ///
        /// <para>Deliberately routed through <see cref="TypeBeatRuleset.ScoreEarnsPerformancePoints"/>
        /// rather than re-stating the rule: that override is the single authority, and it is also
        /// what the score panel's own pp readout asks (through
        /// <c>score.Ruleset.CreateInstance()</c>, since a shared component cannot reference this
        /// assembly). One implementation, two entry points, so the panel and the results table
        /// cannot gate a play differently. A local instance is used rather than
        /// <see cref="ScoreInfo.Ruleset"/> so this still answers for a score that carries no ruleset
        /// reference at all.</para>
        /// </summary>
        public static bool Eligible(ScoreInfo score) => new TypeBeatRuleset().ScoreEarnsPerformancePoints(score);

        /// <summary>
        /// The one rendering of a pp value, shared by every surface so they cannot round differently:
        /// whole points, or <see cref="INELIGIBLE_TEXT"/> for a play that can never earn any.
        /// Invariant culture, matching the rest of the completion table.
        /// </summary>
        public static string Format(double? pp)
            => pp is double value ? value.ToString("0", CultureInfo.InvariantCulture) : INELIGIBLE_TEXT;
    }
}
