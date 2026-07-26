// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using osu.Framework.Localisation;
using typebeat.Game.Rulesets.Mods;

namespace typebeat.Game.Rulesets.TypeBeat.Mods
{
    /// <summary>
    /// Double-time with constant pitch. The non-generic base is used deliberately; the generic
    /// ModNightcore&lt;T&gt; injects a drum-beat overlay keyed off circle-game timing control points,
    /// which is meaningless for a lyric ruleset.
    ///
    /// <para>
    /// Ranked at every speed and paid on the same curve as <see cref="TypeBeatModDoubleTime"/>: the
    /// two mods differ only in what happens to the pitch, which is not a difficulty lever.
    /// </para>
    /// </summary>
    public class TypeBeatModNightcore : ModNightcore
    {
        public override bool Ranked => true;

        /// <summary>See <see cref="TypeBeatModDoubleTime.ExtendedIconInformation"/>.</summary>
        public override string ExtendedIconInformation => FormattableString.Invariant($@"{SpeedChange.Value:N2}x");

        public override IEnumerable<(LocalisableString setting, LocalisableString value)> SettingDescription
        {
            get { yield return ("Speed change", FormattableString.Invariant($@"{SpeedChange.Value:N2}x")); }
        }

        /// <summary>See <see cref="TypeBeatModDoubleTime.AlwaysSerializeSetting"/>.</summary>
        public override bool AlwaysSerializeSetting(string propertyName) => propertyName == nameof(SpeedChange);
    }
}
