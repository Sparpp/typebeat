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
    /// Slows the track down. Sync windows widen in real time (see <see cref="TypeBeatModDoubleTime"/>).
    /// Ranked at every speed in [0.50x, 0.99x]; the penalty scales continuously with how far the
    /// rate is pulled below 1.0x, so 0.99x costs almost nothing and 0.50x costs almost everything.
    /// </summary>
    public class TypeBeatModHalfTime : ModHalfTime
    {
        public override bool Ranked => true;

        /// <summary>See <see cref="TypeBeatModDoubleTime.ExtendedIconInformation"/>.</summary>
        public override string ExtendedIconInformation => FormattableString.Invariant($@"{SpeedChange.Value:N2}x");

        public override IEnumerable<(LocalisableString setting, LocalisableString value)> SettingDescription
        {
            get
            {
                yield return ("Speed change", FormattableString.Invariant($@"{SpeedChange.Value:N2}x"));

                if (!AdjustPitch.IsDefault)
                    yield return ("Adjust pitch", AdjustPitch.Value ? "On" : "Off");
            }
        }

        /// <summary>See <see cref="TypeBeatModDoubleTime.AlwaysSerializeSetting"/>.</summary>
        public override bool AlwaysSerializeSetting(string propertyName) => propertyName == nameof(SpeedChange);
    }
}
