// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Input.Bindings;
using osu.Framework.Testing;
using typebeat.Game.Overlays.Settings.Sections.Input;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Backlog 183's settings half: the key configuration screen carries a type!beat section again,
    /// with a rebindable row per ruleset action. The section was dropped when its only rows were the
    /// two vestigial buttons; the two word-level typing gestures (erase word, select back to typo)
    /// are what brought it back, so this pins that they are actually reachable there rather than only
    /// declared in <see cref="TypeBeatRuleset.GetDefaultKeyBindings"/>.
    /// </summary>
    [TestFixture]
    public partial class TestSceneTypeBeatKeyBindings : OsuTestScene
    {
        private KeyBindingPanel panel = null!;

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("open the key configuration panel", () =>
            {
                Child = panel = new KeyBindingPanel();
                panel.Show();
            });

            AddUntilStep("panel loaded", () => panel.IsLoaded && rows().Any());
        }

        [Test]
        public void TestBothGesturesHaveARebindableRow()
        {
            AddAssert("erase word has a row", () => rowFor(TypeBeatAction.EraseWord) != null);
            AddAssert("select back to typo has a row", () => rowFor(TypeBeatAction.SelectBackToTypo) != null);

            AddAssert("erase word shows its default chord", () =>
                rowFor(TypeBeatAction.EraseWord)!.Defaults,
                () => Is.EqualTo(new[] { new KeyCombination(InputKey.Control, InputKey.BackSpace) }));

            AddAssert("select back to typo shows its default chord", () =>
                rowFor(TypeBeatAction.SelectBackToTypo)!.Defaults,
                () => Is.EqualTo(new[] { new KeyCombination(InputKey.Control, InputKey.A) }));

            // A row only takes a rebind while it can be focused; the ruleset subsection is the one
            // that also allows the main mouse buttons (a gameplay binding may legitimately be a click).
            AddAssert("the rows accept a rebind", () =>
                rowFor(TypeBeatAction.EraseWord)!.AllowMainMouseButtons
                && rowFor(TypeBeatAction.SelectBackToTypo)!.AllowMainMouseButtons);
        }

        /// <summary>
        /// The line skip (backlog 241) gets a row of its own, and it is the first action here with
        /// TWO defaults: one row, both Enter keys in it, which is what a player has to see before
        /// they can move the skip somewhere else.
        /// </summary>
        [Test]
        public void TestTheLineSkipHasOneRowCarryingBothEnterKeys()
        {
            AddAssert("skip line has a row", () => rowFor(TypeBeatAction.SkipLine) != null);

            AddAssert("showing both of its defaults", () =>
                rowFor(TypeBeatAction.SkipLine)!.Defaults,
                () => Is.EqualTo(new[] { new KeyCombination(InputKey.Enter), new KeyCombination(InputKey.KeypadEnter) }));

            AddAssert("and it accepts a rebind", () => rowFor(TypeBeatAction.SkipLine)!.AllowMainMouseButtons);
        }

        private IEnumerable<KeyBindingRow> rows() => panel.ChildrenOfType<KeyBindingRow>();

        private KeyBindingRow? rowFor(TypeBeatAction action) => rows().SingleOrDefault(r => r.Action is TypeBeatAction bound && bound == action);
    }
}
