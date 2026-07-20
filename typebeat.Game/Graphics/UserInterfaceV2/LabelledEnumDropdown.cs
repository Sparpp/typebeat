// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using typebeat.Game.Graphics.UserInterface;

namespace typebeat.Game.Graphics.UserInterfaceV2
{
    public partial class LabelledEnumDropdown<TEnum> : LabelledDropdown<TEnum>
        where TEnum : struct, Enum
    {
        public LabelledEnumDropdown(bool padded)
            : base(padded)
        {
        }

        protected override OsuDropdown<TEnum> CreateDropdown() => new OsuEnumDropdown<TEnum>();
    }
}
