// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Rulesets.Mods;

namespace typebeat.Game.Rulesets.TypeBeat.Mods
{
    /// <summary>
    /// Double-time with constant pitch. The non-generic base is used deliberately — the generic
    /// ModNightcore&lt;T&gt; injects a drum-beat overlay keyed off circle-game timing control points,
    /// which is meaningless for a lyric ruleset.
    /// </summary>
    public class TypeBeatModNightcore : ModNightcore
    {
    }
}
