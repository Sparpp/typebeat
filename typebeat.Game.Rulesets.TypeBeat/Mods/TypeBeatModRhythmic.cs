// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Localisation;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Rulesets.UI;

namespace typebeat.Game.Rulesets.TypeBeat.Mods
{
    /// <summary>
    /// Rhythmic: a keypress is judged on WHEN it happened, not on where the caret is relative to the
    /// playhead. It selects the millisecond ladder of <see cref="SyncWindows"/>
    /// (<see cref="SyncMeasure.Milliseconds"/>) instead of the character-distance ladder backlog 133
    /// made the default, so every character has to be pressed at its own
    /// <see cref="TypingCell.TargetTime"/> rather than merely close to the character the song is on.
    ///
    /// <para>WHY THAT IS HARDER ON REAL MAPS, and not by a fixed amount. A character distance
    /// reduces to a millisecond delta divided by the local cell spacing, so the two ladders coincide
    /// exactly where a map runs at 10 characters per second (the Perfect rows are 1.25/2.00
    /// characters and 125/200 ms, and 1.25 characters at 100 ms each IS 125 ms). Below that pace,
    /// which is where lyrics live almost all of the time, the millisecond windows are the tighter
    /// pair: at 5 characters per second they are half the width, at 2.5 a quarter. Above it, in a
    /// burst faster than 10 characters per second, they are the LOOSER pair. The mod is therefore a
    /// difficulty increase on the map as a whole while being locally forgiving in the fastest bars,
    /// which is the honest description of "type it as the map would have pressed it".</para>
    ///
    /// <para>IT KEEPS ALL FOUR TIERS, deliberately, and does not collapse back to the three the game
    /// judged in before backlog 133. The ladder's Great/Ok/Meh rows already ARE those three windows
    /// byte for byte, so the run reproduces the old judgement rather than approximating it; the new
    /// top row subdivides the old top window and is not optional, because
    /// <c>maximum_statistics</c> is one <see cref="Rulesets.Scoring.HitResult.Perfect"/> per cell. A
    /// three-tier Rhythmic could never award one, so no Rhythmic play could reach 100% accuracy or
    /// rank X, and its scores would be incomparable on the boards they share with everything else.
    /// </para>
    ///
    /// <para>Implemented by setting a single engine property
    /// (<see cref="Gameplay.TypingEngine.Measure"/>), the pattern Mashing, Fletcher and Gatekeeper
    /// use, so the unmodded judgement path is untouched. The property must be set before the first
    /// keypress and is never revisited, which <see cref="IApplicableToDrawableRuleset{T}"/>
    /// guarantees: it runs while the ruleset loads, long before a line is active. The other half of
    /// that guarantee is <see cref="Scoring.TypeBeatReplayScorer"/>, which builds its own engine from
    /// the run's mods and sets the same property, so a stored Rhythmic score re-derives under the
    /// ladder it was played on.</para>
    ///
    /// <para>Composes with everything (see <see cref="Mod.IncompatibleMods"/>, left empty). It
    /// changes the UNIT a press is measured in, where Mashing changes which keys count and Fletcher
    /// changes where the caret may be, so none of the three contends for the same decision. Under
    /// Fletcher in particular the rush cap stays a character count
    /// (<see cref="Gameplay.TypingEngine.FLETCHER_MAX_CHARS_AHEAD"/>): it governs COMBO, not
    /// judgement, and the two were always separate levers.</para>
    /// </summary>
    public class TypeBeatModRhythmic : Mod, IApplicableToDrawableRuleset<TypeBeatHitObject>
    {
        public override string Name => "Rhythmic";

        public override string Acronym => "RH";

        public override LocalisableString Description => "Every character on its own beat, not just near the playhead.";

        /// <summary>
        /// A difficulty increase, not a conversion: it asks for the same keys in the same order and
        /// only narrows the window each of them lands in (see the pace argument above). Fletcher is
        /// the ruleset's Conversion because it trades one kind of pressure for another; this one
        /// simply adds pressure.
        /// </summary>
        public override ModType Type => ModType.DifficultyIncrease;

        // No Icon override, so the mod-select overlay renders the acronym pill, exactly as Literate,
        // Fletcher and Gatekeeper do. Flashlight is the ruleset's only icon-bearing non-rate mod and
        // it inherits that icon from its osu base class.

        // The real multiplier is defined in TypeBeatScoreMultiplierCalculator (the authoritative,
        // non-obsolete path osu now uses). This obsolete override is kept only so the mod also
        // self-reports 1.10x for any legacy reader.
#pragma warning disable CS0672 // Member overrides obsolete member
        public override double ScoreMultiplier => 1.10;
#pragma warning restore CS0672

        /// <summary>
        /// Ranked, and paid a bonus at both score and pp: every character still has to be typed, and
        /// each one now has to be typed at its own target time rather than anywhere near the
        /// playhead. Scores therefore reach the shared leaderboards, which is why the whole mod is
        /// one property on the engine and is pinned by the replay round-trip.
        /// </summary>
        public override bool Ranked => true;

        public void ApplyToDrawableRuleset(DrawableRuleset<TypeBeatHitObject> drawableRuleset) =>
            ((DrawableTypeBeatRuleset)drawableRuleset).Engine.Measure = SyncMeasure.Milliseconds;
    }
}
