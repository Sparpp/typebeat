// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Localisation;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Rulesets.UI;

namespace typebeat.Game.Rulesets.TypeBeat.Mods
{
    /// <summary>
    /// Hard Rock: every judgement window is HALVED, so every character gives you half as long to hit
    /// it. The exact mirror of <see cref="TypeBeatModEasy"/>, built on the same general window scale
    /// (<see cref="Gameplay.TypingEngine.WindowScale"/>) and multiplying its factor in rather than
    /// assigning it, so it composes with a rate mod's scale in either application order. The ladder
    /// keeps its shape: each tier and each asymmetric side is halved, and a Syllable-timed cell is
    /// still judged more tightly than a Line-timed one.
    ///
    /// <para>HALVING IS DELIBERATELY HARSHER THAN osu's HARD ROCK, and it is not an approximation of
    /// it. osu multiplies OD by 1.4, and because its Great window is <c>80 - 6*OD</c> with an OD-10
    /// cap the resulting window ratio is not even monotonic in OD: it runs between roughly 0.56 and
    /// 1.0, and is exactly 1.0 (no tightening at all) for any map already at OD 7.15 or above.
    /// type!beat has no OD to bend, so the choice was between inventing a curve with no input and
    /// mirroring Easy exactly. Symmetry was chosen, by the user, on 2026-08-13: Easy doubles,
    /// Hard Rock halves, and the pair reads as one lever with two ends. Do not "correct" this back
    /// toward osu's ratio.</para>
    /// </summary>
    public class TypeBeatModHardRock : ModHardRock, IApplicableToDrawableRuleset<TypeBeatHitObject>
    {
        /// <summary>
        /// What the mod multiplies every judgement window by, the reciprocal of
        /// <see cref="TypeBeatModEasy.WINDOW_SCALE"/>. Read by
        /// <see cref="Scoring.TypeBeatReplayScorer"/> too, so a replay is re-judged on the same
        /// ladder the live run was.
        /// </summary>
        public const double WINDOW_SCALE = 0.5;

        public override LocalisableString Description => "Half as long to hit every character.";

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
        /// <see cref="ModEasy"/> is kept and is the live entry: the two scale the same windows in
        /// opposite directions, and a stack holding both would silently be a no-op ladder priced as
        /// two difficulty adjustments. Backlog 149 left the mirror-image entry on Easy in advance,
        /// so the exclusion fires from both sides without that file being reopened.
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

        public void ApplyToDrawableRuleset(DrawableRuleset<TypeBeatHitObject> drawableRuleset) =>
            ((DrawableTypeBeatRuleset)drawableRuleset).Engine.WindowScale *= WINDOW_SCALE;
    }
}
