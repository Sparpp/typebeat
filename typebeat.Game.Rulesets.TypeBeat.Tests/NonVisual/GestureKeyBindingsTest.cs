// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Extensions;
using osu.Framework.Input.Bindings;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The declaration side of backlog 183: the two word-level typing gestures are ordinary ruleset
    /// ACTIONS with defaults and labels, which is the whole of what the key configuration screen
    /// needs to render a rebindable row for each (see <c>VariantBindingsSubsection</c>, which builds
    /// its rows straight out of <see cref="Ruleset.GetDefaultKeyBindings"/> and captions them with
    /// each action's description). <c>TestSceneTypeBeatKeyBindings</c> pins the rows themselves;
    /// this pins what they are built from.
    /// </summary>
    [TestFixture]
    public class GestureKeyBindingsTest
    {
        [Test]
        public void GesturesAreBoundToTheirChordsByDefault()
        {
            var defaults = new TypeBeatRuleset().GetDefaultKeyBindings().ToList();

            Assert.That(combinationFor(defaults, TypeBeatAction.EraseWord),
                Is.EqualTo(new KeyCombination(InputKey.Control, InputKey.BackSpace)));

            Assert.That(combinationFor(defaults, TypeBeatAction.SelectBackToTypo),
                Is.EqualTo(new KeyCombination(InputKey.Control, InputKey.A)));
        }

        [Test]
        public void EveryActionIsBoundAndLabelled()
        {
            var defaults = new TypeBeatRuleset().GetDefaultKeyBindings().ToList();

            foreach (var action in Enum.GetValues<TypeBeatAction>())
            {
                Assert.That(defaults.Select(b => b.Action), Does.Contain(action), $"{action} has no default binding");
                Assert.That(action.GetDescription(), Is.Not.Empty, $"{action} has no description to caption its row with");
            }
        }

        /// <summary>
        /// Stored bindings key off the enum's INTEGER value (<c>RealmKeyBinding.ActionInt</c>), so the
        /// two actions that existed before backlog 183 have to keep the numbers they were saved under.
        /// Renumbering them would silently re-point every existing user's rows.
        /// </summary>
        [Test]
        public void PreExistingActionsKeepTheirStoredNumbers()
        {
            Assert.That((int)TypeBeatAction.Button1, Is.EqualTo(0));
            Assert.That((int)TypeBeatAction.Button2, Is.EqualTo(1));
        }

        private static KeyCombination combinationFor(System.Collections.Generic.IEnumerable<KeyBinding> defaults, TypeBeatAction action)
            => defaults.Single(b => b.Action is TypeBeatAction bound && bound == action).KeyCombination;
    }
}
