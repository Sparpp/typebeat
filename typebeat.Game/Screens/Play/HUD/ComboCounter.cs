// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Skinning;

namespace typebeat.Game.Screens.Play.HUD
{
    public abstract partial class ComboCounter : RollingCounter<int>, ISerialisableDrawable
    {
        public bool UsesFixedAnchor { get; set; }

        protected override double GetProportionalDuration(int currentValue, int newValue)
        {
            return Math.Abs(currentValue - newValue) * RollingDuration * 100.0f;
        }
    }
}
