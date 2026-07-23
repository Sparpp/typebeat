// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using typebeat.Game.Graphics;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Rulesets.UI;
using typebeat.Game.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Mods
{
    /// <summary>
    /// Flashlight, retuned for a typing game. The old implementation was osu's circular darkness
    /// shader tracking the caret, which reads as an ugly grey blob over a lyric stack. This version
    /// hides characters instead of dimming pixels: only a short run around the typing caret stays
    /// lit, <see cref="visible_radius"/> COUNTABLE chars (typeable, non-space) reaching each side of
    /// the caret head. Spaces and punctuation between lit chars stay lit but do not spend the budget,
    /// and everything else, including the two inactive lines of the stack, is hidden so you cannot
    /// read ahead. The window slides with the caret (correct and wrong-advancing input alike) and is
    /// purely visual, so autoplay/replays light up identically and judgement is unaffected.
    ///
    /// It no longer subclasses <c>ModFlashlight</c> (that base drags in the size/combo shader knobs,
    /// which are meaningless for a discrete character window). Acronym, type and ranked status are
    /// unchanged, and the score multiplier stays the flat 1.2x the old flashlight used at its
    /// default size (see <see cref="Scoring.TypeBeatScoreMultiplierCalculator"/>), so the server,
    /// which only keys off the "FL" acronym, needs no change.
    /// </summary>
    public class TypeBeatModFlashlight : Mod, IApplicableToDrawableRuleset<TypeBeatHitObject>, IApplicableToScoreProcessor
    {
        public override string Name => "Flashlight";

        public override string Acronym => "FL";

        public override IconUsage? Icon => OsuIcon.ModFlashlight;

        public override ModType Type => ModType.DifficultyIncrease;

        public override LocalisableString Description => "Only a few characters around the caret stay lit.";

        public override bool Ranked => true;

        // Legacy self-report only; the authoritative multiplier lives in the non-obsolete
        // TypeBeatScoreMultiplierCalculator (1.2x, unchanged from the old default-size flashlight).
#pragma warning disable CS0672 // Member overrides obsolete member
        public override double ScoreMultiplier => 1.2;
#pragma warning restore CS0672

        /// <summary>Countable characters lit either side of the caret head.</summary>
        private const int visible_radius = 5;

        public void ApplyToDrawableRuleset(DrawableRuleset<TypeBeatHitObject> drawableRuleset) =>
            ((DrawableTypeBeatRuleset)drawableRuleset).FlashlightVisibleRadius = visible_radius;

        public void ApplyToScoreProcessor(ScoreProcessor scoreProcessor)
        {
        }

        // Preserve the silver rank suffix the old flashlight applied (X -> XH, S -> SH).
        public ScoreRank AdjustRank(ScoreRank rank, double accuracy) => rank switch
        {
            ScoreRank.X => ScoreRank.XH,
            ScoreRank.S => ScoreRank.SH,
            _ => rank,
        };
    }
}
