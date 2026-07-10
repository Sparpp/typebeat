// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.ComponentModel;

namespace typebeat.Game.Configuration
{
    public enum ReleaseStream
    {
        Lazer,

        [Description("Tachyon (Unstable)")]
        Tachyon
    }
}
