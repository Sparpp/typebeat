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
    /// legitimately print are client-only, so they live here, in one place, shared by the two
    /// surfaces that print them: the live counter in <see cref="UI.TypeBeatHudOverlay"/> and the
    /// results screen's Completion table (<see cref="TypeBeatRuleset.CreateCompletionStatistics"/>).
    /// The two therefore cannot disagree about a gate or about a rounding.
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
    /// <item><b>Unknown to the server.</b> Not a rendering at all: a score that never reached the
    /// server simply has no stored value, and the game prices it itself (see
    /// <see cref="ForScore"/>). <see cref="ScoreInfo.PP"/> being <c>null</c> rather than 0 is what
    /// keeps this separate from an ineligible submission, which the server DOES store as 0.</item>
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
        /// once the play is over; see <see cref="ForScore"/>, the one gate the results screen has
        /// that the HUD cannot.</para>
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
        /// What a FINISHED score is worth, or null when it can never be worth anything (rendered as
        /// <see cref="INELIGIBLE_TEXT"/>).
        ///
        /// <para>A STORED value is the authoritative one: it is what the leaderboards and the profile
        /// actually count, it was priced against the server's own stored star ratings, and it can
        /// encode refusals the client cannot see at all (the play-time gate, an out-of-bounds total,
        /// a blocked build). But only a POSITIVE one may be trusted on sight, because the server
        /// prices every ineligible play at a flat 0 (its <c>ForScore</c> short-circuits on the
        /// <c>ranked</c> flag), which makes a stored 0 ambiguous between "worth nothing" and "could
        /// never be worth anything". A positive value is self-certifying, and taking it ahead of the
        /// local gates is what keeps a genuinely earned number on screen when the local copy of the
        /// map has drifted from the ranked one the play was set on (an edit in progress marks it
        /// LocallyModified, which the gates would otherwise read as earning nothing).</para>
        ///
        /// <para>Everything else is gated locally first, and only then falls back to the stored 0 or
        /// to a local calculation. The local calculation covers most plays there are: an offline
        /// play, an imported <c>.osr</c>, a replay downloaded from the website, and any play whose
        /// submission failed. Those price identically to a submitted one, because both sides run the
        /// same formula over the same star rating.</para>
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
            // A price the server actually paid. Only the server can know it, and only an eligible
            // play can carry it.
            if (score.PP is > 0)
                return score.PP;

            // The one gate the results screen has and the live counter does not: a fail earns
            // nothing, and after the fact that is knowable. Both flags are checked because they are
            // set by different paths (ScoreProcessor.FailScore sets Rank; a decoded legacy score
            // carries only what its file recorded).
            if (!score.Passed || score.Rank == ScoreRank.F)
                return null;

            if (StarRatingFor(playableBeatmap, score.Mods) is not double stars)
                return null;

            // Past the gates the play is eligible, so a stored 0 is a real price and stands; only a
            // play the server never priced at all is priced here.
            return score.PP ?? PerformancePoints.ForPlay(stars, PerformancePoints.CountNotes(score), score.Accuracy, score.MaxCombo, score.Mods);
        }

        /// <summary>
        /// The one rendering of a pp value, shared by every surface so they cannot round differently:
        /// whole points, or <see cref="INELIGIBLE_TEXT"/> for a play that can never earn any.
        /// Invariant culture, matching the rest of the completion table.
        /// </summary>
        public static string Format(double? pp)
            => pp is double value ? value.ToString("0", CultureInfo.InvariantCulture) : INELIGIBLE_TEXT;
    }
}
