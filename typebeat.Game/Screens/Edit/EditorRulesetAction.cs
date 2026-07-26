// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Bindables;

namespace typebeat.Game.Screens.Edit
{
    /// <summary>
    /// One optional extra action the ACTIVE RULESET's compose screen may publish into the editor's
    /// bottom bar, immediately left of the Test button.
    ///
    /// The editor owns the button; the ruleset only sets a label and a callback. That keeps the
    /// ruleset out of the game's drawable tree entirely, so a compose screen being swapped out (or
    /// disposed off-thread on a mode change) has nothing to tear down: it just clears
    /// <see cref="Visible"/>. The slot collapses to zero width whenever no ruleset publishes one,
    /// which is every ruleset but type!beat, where it is the "Time" (tap-timing) button.
    /// </summary>
    public class EditorRulesetAction
    {
        /// <summary>Whether the slot is occupied at all. False collapses it to zero width.</summary>
        public readonly Bindable<bool> Visible = new Bindable<bool>();

        /// <summary>The button label.</summary>
        public readonly Bindable<string> Text = new Bindable<string>(string.Empty);

        /// <summary>Draws the button in the armed (recording) colour instead of the idle one.</summary>
        public readonly Bindable<bool> Armed = new Bindable<bool>();

        /// <summary>Invoked on click. Null while nothing is published.</summary>
        public Action? Activated;

        /// <summary>Publishes an action into the slot.</summary>
        public void Publish(string text, Action activated)
        {
            Text.Value = text;
            Activated = activated;
            Armed.Value = false;
            Visible.Value = true;
        }

        /// <summary>Empties the slot (the compose screen is going away).</summary>
        public void Withdraw()
        {
            Visible.Value = false;
            Armed.Value = false;
            Activated = null;
            Text.Value = string.Empty;
        }
    }
}
