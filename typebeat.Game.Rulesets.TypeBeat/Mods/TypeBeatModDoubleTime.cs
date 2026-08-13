// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using osu.Framework.Bindables;
using osu.Framework.Localisation;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Rulesets.UI;

namespace typebeat.Game.Rulesets.TypeBeat.Mods
{
    /// <summary>
    /// Speeds up the track, and widens every judgement window by the same factor so that the
    /// tolerance around each character is constant in REAL time.
    ///
    /// <para>
    /// THE WINDOW SCALING IS THE MOD'S DOING, NOT THE ENGINE'S (backlog 150). The engine judges
    /// entirely in MAP time (delta = time - cell.TargetTime) and holds no rate at all, so a fixed
    /// 250 ms map-time window elapses in 250/rate ms of real time: left alone, speeding the track up
    /// TIGHTENED the windows and slowing it down LOOSENED them, on top of the rate change itself,
    /// and neither was ever designed. Multiplying <see cref="Gameplay.TypingEngine.WindowScale"/> by
    /// the rate cancels that exactly, so the player's real-time tolerance under any rate is the one
    /// they have unmodded, and the mod's difficulty is purely the pace it asks them to type at.
    /// </para>
    ///
    /// <para>
    /// The factor is read off <see cref="ModRateAdjust.SpeedChange"/>, never a constant 1.50: the
    /// slider is user-adjustable across the whole ranked range, so a 1.01x nudge widens the windows
    /// by 1%, and a 2.00x sprint doubles them. It is MULTIPLIED in rather than assigned, the seam
    /// <see cref="TypeBeatModEasy"/> uses, so a rate and Easy compose (1.50x plus Easy is a 3.0x
    /// ladder) in either application order.
    /// </para>
    ///
    /// <para>
    /// Unlike osu, type!beat ranks this at EVERY speed in [1.01x, 2.00x], not only at the 1.50x
    /// default: the reward is a continuous function of the rate
    /// (<see cref="Scoring.TypeBeatRateMultiplier"/>), so there is nothing to game by picking an
    /// odd number and no reason to lock the slider off the leaderboards. The trade is that "DT" on
    /// a board is no longer self-describing, which is why the rate is surfaced unconditionally
    /// below instead of only when it differs from the default.
    /// </para>
    /// </summary>
    public class TypeBeatModDoubleTime : ModDoubleTime, IApplicableToDrawableRuleset<TypeBeatHitObject>
    {
        public override bool Ranked => true;

        /// <summary>
        /// Always shown, even at the default 1.50x. Every rate in range is now a ranked, differently
        /// paid play, so an icon that reads a bare "DT" would be ambiguous between a 1.01x nudge and
        /// a 2.00x sprint. <see cref="typebeat.Game.Rulesets.UI.ModIcon"/> renders this as a pill
        /// welded to the icon, so the number travels with the mod everywhere score displays show it.
        /// </summary>
        public override string ExtendedIconInformation => FormattableString.Invariant($@"{SpeedChange.Value:N2}x");

        public override IEnumerable<(LocalisableString setting, LocalisableString value)> SettingDescription
        {
            get
            {
                // Same reasoning as above, for the text form used by tooltips and preset rows.
                yield return ("Speed change", FormattableString.Invariant($@"{SpeedChange.Value:N2}x"));

                if (!AdjustPitch.IsDefault)
                    yield return ("Adjust pitch", AdjustPitch.Value ? "On" : "Off");
            }
        }

        /// <summary>
        /// Pin the rate onto the wire even at the default, so a server never has to know this
        /// client's default to price or display the play. See <see cref="Mod.AlwaysSerializeSetting"/>.
        /// </summary>
        public override bool AlwaysSerializeSetting(string propertyName) => propertyName == nameof(SpeedChange);

        /// <summary>
        /// Scale the judgement windows by the clock rate (see the class docs). Mirrored by
        /// <see cref="Scoring.TypeBeatReplayScorer"/>, which re-judges a stored replay from its
        /// keystrokes and would otherwise re-derive every rate play on unscaled windows.
        /// </summary>
        public void ApplyToDrawableRuleset(DrawableRuleset<TypeBeatHitObject> drawableRuleset) =>
            ((DrawableTypeBeatRuleset)drawableRuleset).Engine.WindowScale *= SpeedChange.Value;
    }
}
