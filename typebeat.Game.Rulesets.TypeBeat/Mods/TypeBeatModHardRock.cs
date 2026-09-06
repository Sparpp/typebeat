// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Localisation;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Mods;

namespace typebeat.Game.Rulesets.TypeBeat.Mods
{
    /// <summary>
    /// Hard Rock: every character is graded on its OWN target time rather than on the syllable it
    /// belongs to, so a word has to be typed in step with the vocal instead of anywhere inside it.
    /// One rule, applied in <c>DrawableTypeBeatRuleset.createEngine</c> and not here
    /// (<see cref="Gameplay.TypingEngine.SyllableTiming"/> = false under HR alone), and the mod owns
    /// no other live behaviour.
    ///
    /// <para>THE JUDGEMENT REVERT (backlog 180) IS WHAT THE MOD IS. Since backlog 179 a press is
    /// graded on distance from its syllable's whole SUNG SPAN, delta 0 anywhere inside it. A span is
    /// typically hundreds of milliseconds wide, so most presses land at delta 0 and never reach the
    /// ladder at all. Under HR the engine keeps the classic per-character point targets, so the
    /// ladder actually grades something. Easy and every other stack keep the syllable rule. The flag
    /// is set at engine construction because it is an ERA bit the replay recorder stamps into its
    /// CONFIG frame (bit 2), so it must be right from the first frame: an HR replay records bit 2
    /// CLEAR and re-derives on point targets forever, with no mod inspection in
    /// <see cref="Scoring.TypeBeatReplayScorer"/>. The sung syllable still lights up on screen: that
    /// is a look, not the rule.</para>
    ///
    /// <para>HISTORY, BECAUSE THE STORED ROWS STILL LIVE IN IT. Backlog 150 shipped HR as a HALVING
    /// of every judgement window, the exact mirror of <see cref="TypeBeatModEasy"/> on the same
    /// general scale (<see cref="Gameplay.TypingEngine.WindowScale"/>), chosen by the user on
    /// 2026-08-13 for symmetry rather than as an approximation of osu's OD x 1.4 (which type!beat
    /// has no OD to bend, and whose window ratio is not even monotonic: roughly 0.56 to 1.0, and
    /// exactly 1.0 for any map already at OD 7.15). Backlog 180 then added the judgement revert
    /// ON TOP, and the two stacked made the mod unplayable for nearly everyone: half a window, and a
    /// point target instead of a span to aim at. Backlog 264 dropped the halving, by the user's
    /// decision, and kept the revert alone at normal windows. Easy is untouched and is no longer a
    /// mirror of anything: its 2.0 is its own.</para>
    ///
    /// <para>The halving lives on as the ERA every stored HR row was played under, which is why
    /// <see cref="WINDOW_SCALE"/> is still here and why the mod no longer writes it anywhere. See
    /// <see cref="Gameplay.TypingEngine.UnhalvedHardRockWindows"/> (CONFIG frame bit 13) for the
    /// mechanism: the run says which era it is, the score's mods say whether HR was on, and the
    /// engine multiplies the ladder itself.</para>
    /// </summary>
    public class TypeBeatModHardRock : ModHardRock
    {
        /// <summary>
        /// What Hard Rock multiplied every judgement window by from backlog 150 until backlog 264
        /// retired the halving. NOT a live constant any more: no mod, and no engine factory, applies
        /// it. It is the ERA constant, read only by
        /// <see cref="Gameplay.TypingEngine"/>'s window-scale seam and only for a run whose CONFIG
        /// frame says it was played before 264 (bit 13 clear) with HR on its score, so those stored
        /// rows re-derive on the ladder their player's fingers were really graded on.
        ///
        /// <para>Its numeric relationship to <see cref="TypeBeatModEasy.WINDOW_SCALE"/> is now a
        /// coincidence of history rather than a design: Easy's 2.0 is independent and was not
        /// touched by 264.</para>
        /// </summary>
        public const double WINDOW_SCALE = 0.5;

        public override LocalisableString Description => "Every character is timed on its own, not on the syllable around it.";

        // The real multiplier is defined in TypeBeatScoreMultiplierCalculator (the authoritative,
        // non-obsolete path osu now uses), which is also where the provenance of the value is
        // recorded. This obsolete override is kept only so the mod also self-reports 1.10x for any
        // legacy reader.
#pragma warning disable CS0672 // Member overrides obsolete member
        public override double ScoreMultiplier => 1.10;
#pragma warning restore CS0672

        /// <summary>
        /// Narrowed to the mods this ruleset actually offers, exactly as
        /// <see cref="TypeBeatModEasy.IncompatibleMods"/> is. osu's <see cref="ModHardRock"/> names
        /// <see cref="ModEasy"/> and <see cref="ModDifficultyAdjust"/>; the second has no type!beat
        /// implementation and can never have one (it moves CircleSize / ApproachRate / DrainRate,
        /// none of which a typing game has), so it is dropped rather than inherited.
        /// <see cref="ModEasy"/> is kept and is the live entry. Its original argument was that the
        /// two scaled the same windows in opposite directions, which backlog 264 retired along with
        /// the halving; the exclusion stands on its own terms, and the user kept it deliberately.
        /// The pair is still the game's one difficulty lever with two ends (Easy widens every
        /// window, Hard Rock takes the syllable's shelter away), so a stack holding both would be
        /// priced as two difficulty adjustments for a handicap that is mostly cancelled. Backlog 149
        /// left the mirror-image entry on Easy in advance, so the exclusion fires from both sides
        /// without that file being reopened.
        /// </summary>
        public override Type[] IncompatibleMods => new[] { typeof(ModEasy) };

        /// <summary>
        /// Overridden AWAY, not extended, for the same reason as
        /// <see cref="TypeBeatModEasy.ApplyToDifficulty"/>: osu's Hard Rock raises DrainRate, and
        /// type!beat has no drain, so inheriting it would move a number nothing reads while the
        /// thing the mod is actually for (the judgement windows) went untouched.
        /// </summary>
        public override void ApplyToDifficulty(BeatmapDifficulty difficulty)
        {
        }
    }
}
