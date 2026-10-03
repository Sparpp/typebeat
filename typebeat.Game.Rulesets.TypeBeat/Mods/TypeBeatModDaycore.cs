// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using osu.Framework.Localisation;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Rulesets.UI;

namespace typebeat.Game.Rulesets.TypeBeat.Mods
{
    /// <summary>
    /// Half-time with constant pitch, the down-rate counterpart of <see cref="TypeBeatModNightcore"/>:
    /// the track is slowed AND the pitch is held, where Half Time lets the pitch drop with the rate.
    /// The non-generic base is used deliberately; the generic ModDaycore&lt;T&gt; injects a drum-beat
    /// overlay keyed off circle-game timing control points, which is meaningless for a lyric ruleset.
    ///
    /// <para>
    /// Ranked at every speed in [0.50x, 0.99x] and paid on the same continuous curve as
    /// <see cref="TypeBeatModHalfTime"/>: the two mods differ only in what happens to the pitch,
    /// which is not a difficulty lever, so they price identically at the same rate. That is why
    /// this class overrides <c>Ranked</c> rather than inheriting <see cref="ModDaycore"/>'s
    /// <c>UsesDefaultConfiguration</c>: like the other three rate mods, a custom rate is a ranked,
    /// differently paid play rather than an unranked oddity.
    /// </para>
    ///
    /// <para>
    /// The judgement windows are scaled by the rate here too, for the same reason and by the same
    /// seam as Half Time (backlog 150): a fixed map-time window elapses in 250/rate ms of real time,
    /// so leaving it alone would hand a slower track a looser window on top of the slower pace.
    /// </para>
    /// </summary>
    public class TypeBeatModDaycore : ModDaycore, IApplicableToDrawableRuleset<TypeBeatHitObject>
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

        /// <summary>See <see cref="TypeBeatModDoubleTime.ApplyToDrawableRuleset"/>.</summary>
        public void ApplyToDrawableRuleset(DrawableRuleset<TypeBeatHitObject> drawableRuleset) =>
            ((DrawableTypeBeatRuleset)drawableRuleset).Engine.WindowScale *= SpeedChange.Value;
    }
}
