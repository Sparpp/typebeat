// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.ComponentModel;

namespace typebeat.Game.Configuration
{
    public enum ScrollVisualisationMethod
    {
        [Description("Sequential")]
        Sequential,

        [Description("Overlapping")]
        Overlapping,

        [Description("Constant")]
        Constant
    }
}
