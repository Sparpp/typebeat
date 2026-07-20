// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.ComponentModel;
using osu.Framework.Input.Bindings;
using typebeat.Game.Rulesets.UI;

namespace typebeat.Game.Rulesets.TypeBeat
{
    public partial class TypeBeatInputManager : RulesetInputManager<TypeBeatAction>
    {
        public TypeBeatInputManager(RulesetInfo ruleset)
            : base(ruleset, 0, SimultaneousBindingMode.Unique)
        {
        }
    }

    public enum TypeBeatAction
    {
        [Description("Button 1")]
        Button1,

        [Description("Button 2")]
        Button2,
    }
}
