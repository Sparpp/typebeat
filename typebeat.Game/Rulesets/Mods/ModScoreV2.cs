// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using typebeat.Game.Graphics;

namespace typebeat.Game.Rulesets.Mods
{
    /// <remarks>
    /// This mod is used strictly to mark type!beatstable scores set with the "Score V2" mod active.
    /// It should not be used in any real capacity going forward.
    /// </remarks>
    public class ModScoreV2 : Mod
    {
        public override string Name => "Score V2";
        public override string Acronym => @"SV2";
        public override IconUsage? Icon => OsuIcon.ModScoreV2;
        public override ModType Type => ModType.System;
        public override LocalisableString Description => "Score set on earlier type!beat versions with the V2 scoring algorithm active.";
        public override bool UserPlayable => false;
        public override bool ValidForMultiplayer => false;
        public override bool ValidForMultiplayerAsFreeMod => false;
    }
}
