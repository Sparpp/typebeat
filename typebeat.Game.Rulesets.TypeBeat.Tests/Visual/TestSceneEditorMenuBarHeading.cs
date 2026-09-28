// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Testing;
using osuTK;
using typebeat.Game.Graphics.Containers;
using typebeat.Game.Overlays;
using typebeat.Game.Screens.Edit.Components.Menus;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// The editor's menu bar carries the "type!beat editor" wordmark to the left of its items.
    /// <para>
    /// osu's heading was a fixed 114 px box with 8 px of padding, holding a 26 px pencil icon flush left
    /// and the text flush right, so the text had the 72 px between them to live in. "osu!editor" fitted;
    /// "type!beat editor" measures 88.32 px at the default font and scale, so it reached back over the
    /// icon. The icon is gone, the box auto-sizes to the text plus its padding, and the items start after
    /// the MEASURED heading rather than after a constant.
    /// </para>
    /// </summary>
    [TestFixture]
    public partial class TestSceneEditorMenuBarHeading : OsuTestScene
    {
        /// <summary>
        /// The 114 px heading box osu shipped, its 8 px padding, and the 26 px icon that used to sit at the
        /// left of it. Kept here only to say what the wordmark had to fit inside and no longer does.
        /// </summary>
        private const float old_text_slot_width = 114 - 2 * 8 - 26;

        /// <summary>
        /// The padding the heading keeps on each side of the wordmark.
        /// </summary>
        private const float heading_padding = 8;

        [Cached]
        private readonly OverlayColourProvider colourProvider = new OverlayColourProvider(OverlayColourScheme.Aquamarine);

        private EditorMenuBar menuBar = null!;

        /// <summary>
        /// The wordmark. It is the only <see cref="TextFlowContainer"/> in the bar: the items render
        /// through plain sprite text.
        /// </summary>
        private TextFlowContainer wordmark => menuBar.ChildrenOfType<TextFlowContainer>().Single();

        /// <summary>
        /// The heading box, which is the wordmark's own parent and is what the item offset is taken from.
        /// </summary>
        private Container headingArea => (Container)wordmark.Parent!;

        /// <summary>
        /// Where the menu items begin, as the bar itself states it.
        /// </summary>
        private float itemOffset => menuBar.ChildrenOfType<OsuScrollContainer>().Single().Margin.Left;

        /// <summary>
        /// The leftmost menu item, which is the one the wordmark can reach. Taken by position rather than
        /// by child order so that the assert does not quietly depend on how the flow container enumerates.
        /// </summary>
        private Menu.DrawableMenuItem leftmostItem => menuBar.ChildrenOfType<Menu.DrawableMenuItem>()
                                                            .OrderBy(item => item.ScreenSpaceDrawQuad.TopLeft.X)
                                                            .First();

        private void createBar(float uiScale)
        {
            AddStep($"create menu bar at {uiScale:0.##}x", () => Child = new Container
            {
                // The game applies UI scale as a uniform scale over the content it wraps, so this is the
                // transform 125 percent puts the editor under.
                RelativeSizeAxes = Axes.Both,
                Scale = new Vector2(uiScale),
                Child = new Container
                {
                    Name = "Top bar",
                    RelativeSizeAxes = Axes.X,
                    Height = 40,
                    Child = menuBar = new EditorMenuBar
                    {
                        Anchor = Anchor.CentreLeft,
                        Origin = Anchor.CentreLeft,
                        RelativeSizeAxes = Axes.Both,
                        MaxHeight = 600,
                        Items = new[]
                        {
                            new MenuItem("File") { Items = new[] { new MenuItem("Save") } },
                            new MenuItem("Edit") { Items = new[] { new MenuItem("Undo") } },
                            new MenuItem("View") { Items = new[] { new MenuItem("Waveform") } },
                            new MenuItem("Timing") { Items = new[] { new MenuItem("Set preview point") } },
                        }
                    }
                }
            });

            // The wordmark's text reaches the flow container a frame or more after construction and the
            // item offset is taken from the measurement, so the bar settles a couple of frames in. Waiting
            // on the measurement rather than on a fixed number of frames is the point.
            AddUntilStep("bar has measured", () => wordmark.DrawWidth > 0 && leftmostItem.DrawWidth > 0);
        }

        private void assertNothingOverlaps()
        {
            AddAssert("wordmark ends before the first item begins",
                () => wordmark.ScreenSpaceDrawQuad.TopRight.X,
                () => Is.LessThanOrEqualTo(leftmostItem.ScreenSpaceDrawQuad.TopLeft.X));

            AddAssert("the item offset is the measured heading width",
                () => itemOffset,
                () => Is.EqualTo(headingArea.DrawWidth).Within(0.01f));

            AddAssert("the heading is the wordmark plus its padding",
                () => headingArea.DrawWidth,
                () => Is.EqualTo(wordmark.DrawWidth + 2 * heading_padding).Within(0.01f));
        }

        /// <summary>
        /// The invariant: the items start after the wordmark, whatever the wordmark measures.
        /// </summary>
        [Test]
        public void TestNothingOverlapsAtDefaultScale()
        {
            createBar(1);
            assertNothingOverlaps();
        }

        /// <summary>
        /// The same at 125 percent UI scale. Uniform scaling cannot break a relation derived from a
        /// measurement, which is exactly the property a hardcoded pixel offset lacked, so this asserts the
        /// derivation still holds under the transform rather than re-deriving anything itself.
        /// </summary>
        [Test]
        public void TestNothingOverlapsAtIncreasedUiScale()
        {
            createBar(1.25f);
            assertNothingOverlaps();
        }

        /// <summary>
        /// The non-vacuity of the change: the wordmark really is wider than the 72 px osu's fixed heading
        /// left beside its icon, which is why it ran over the icon and why the box could not stay fixed.
        /// If this ever stops holding, the reported bug no longer exists.
        /// </summary>
        [Test]
        public void TestWordmarkOutgrewTheOldFixedHeading()
        {
            createBar(1);

            AddAssert("wordmark is wider than the slot beside the old icon",
                () => wordmark.DrawWidth, () => Is.GreaterThan(old_text_slot_width));
        }
    }
}
