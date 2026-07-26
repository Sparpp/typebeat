// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using osu.Framework.Bindables;
using osu.Framework.Localisation;
using typebeat.Game.Rulesets.Mods;

namespace typebeat.Game.Rulesets.TypeBeat.Mods
{
    /// <summary>
    /// Speeds up the track. The engine judges in beatmap-time (delta = time - cell.TargetTime),
    /// so the fixed sync windows narrow in real time, exactly osu's intended difficulty effect,
    /// no ruleset-specific code needed.
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
    public class TypeBeatModDoubleTime : ModDoubleTime
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
    }
}
