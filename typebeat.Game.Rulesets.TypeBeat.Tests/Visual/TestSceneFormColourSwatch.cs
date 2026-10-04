// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Testing;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Overlays;
using typebeat.Game.Tests.Visual;
using osuTK.Input;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// The shared hex-plus-swatch colour row on its own, bound to a plain bindable, so nothing
    /// downstream can re-normalise what it commits. (The editor's freestyle row re-saves every colour
    /// through the map, which hid a commit that stored alpha 1/255.)
    /// </summary>
    public partial class TestSceneFormColourSwatch : OsuManualInputManagerTestScene
    {
        [Cached]
        private readonly OverlayColourProvider colourProvider = new OverlayColourProvider(OverlayColourScheme.Aquamarine);

        private readonly Bindable<Colour4> colour = new Bindable<Colour4>(new Colour4(255, 138, 61, 255));

        private FormColourSwatch row = null!;

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("reset colour", () => colour.SetDefault());
            AddStep("create row", () => Child = new PopoverContainer
            {
                RelativeSizeAxes = Axes.Both,
                Child = row = new FormColourSwatch
                {
                    Caption = "Colour",
                    Width = 0.5f,
                    Current = colour,
                },
            });
        }

        [Test]
        public void TestTypedColourIsStoredOpaque()
        {
            commitHex("#00ff00");

            AddAssert("colour is the typed one", () => colour.Value.R == 0 && colour.Value.G == 1 && colour.Value.B == 0);
            AddAssert("and fully opaque", () => colour.Value.A == 1);
            AddAssert("field spells it without an alpha suffix", () => hexIs("#00ff00"));
        }

        [Test]
        public void TestASecondColourCanBeTypedOverTheFirst()
        {
            commitHex("#00ff00");
            commitHex("123456");

            AddAssert("the second colour took", () => hexIs("#123456"));
            AddAssert("opaque", () => colour.Value.A == 1);
        }

        [Test]
        public void TestAnAlphaInTheHexIsDropped()
        {
            commitHex("#11223380");

            AddAssert("stored opaque", () => colour.Value.A == 1);
            AddAssert("field spells six digits", () => hexIs("#112233"));
        }

        [Test]
        public void TestClearingResetsToDefault()
        {
            commitHex("#123456");
            commitHex(string.Empty);

            AddAssert("back to the default", () => colour.IsDefault);
            AddAssert("field spells the default", () => hexIs("#ff8a3d"));
        }

        private void commitHex(string value)
        {
            TextBox box() => row.HexBox.ChildrenOfType<TextBox>().Single();

            AddStep("focus the hex field", () => row.HexBox.TriggerClick());
            AddUntilStep("hex field focused", () => box().HasFocus);
            AddStep($"type '{value}' and press Enter", () =>
            {
                box().Text = value;
                InputManager.Key(Key.Enter);
            });
        }

        private bool hexIs(string expected) => string.Equals(row.HexBox.Current.Value, expected, System.StringComparison.OrdinalIgnoreCase);
    }
}
