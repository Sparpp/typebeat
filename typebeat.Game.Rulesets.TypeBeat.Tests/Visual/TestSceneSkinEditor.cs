// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;
using typebeat.Game.Overlays.SkinEditor;
using typebeat.Game.Screens.Edit;
using typebeat.Game.Skinning;
using typebeat.Game.Skinning.Components;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Boots the vendored skin editor against a stub target rather than a full game screen: a bare
    /// container carrying one <see cref="SkinnableContainer"/> for the playfield layer (whose default
    /// layout is empty, so no gameplay dependencies are dragged in headlessly). Covers
    /// target pickup (a blueprint container appears and blueprints track components), placing a new
    /// component via the paste path, and the placeholder shown for a target with nothing skinnable.
    /// <see cref="SkinEditorOverlay"/> itself is not booted here: it resolves the full game shell
    /// (OsuGame, MusicController, screen performer), which only exists in a game-level test.
    /// </summary>
    [TestFixture]
    public partial class TestSceneSkinEditor : OsuTestScene
    {
        [Cached]
        public readonly EditorClipboard Clipboard = new EditorClipboard();

        private TestSkinEditor skinEditor = null!;
        private Container target = null!;

        [Test]
        public void TestSkinnableTargetGetsBlueprints()
        {
            AddStep("create editor with skinnable target", () =>
            {
                Children = new Drawable[]
                {
                    target = new Container
                    {
                        RelativeSizeAxes = Axes.Both,
                        Child = new SkinnableContainer(new GlobalSkinnableContainerLookup(GlobalSkinnableContainers.Playfield)),
                    },
                    skinEditor = new TestSkinEditor(target),
                };
            });

            AddUntilStep("blueprint container created", () => skinEditor.ChildrenOfType<SkinBlueprintContainer>().Any());

            AddStep("put box element on clipboard", () => Clipboard.Content.Value = JsonConvert.SerializeObject(new[] { new BoxElement().CreateSerialisedInfo() }));
            AddStep("paste", () => skinEditor.Paste());

            AddUntilStep("component placed in target", () => target.ChildrenOfType<SkinnableContainer>().Single().Components.OfType<BoxElement>().Count() == 1);
            AddUntilStep("pasted component selected", () => skinEditor.SelectedComponents.Count == 1);
            AddUntilStep("blueprint created for component", () => skinEditor.ChildrenOfType<SkinBlueprint>().Count() == 1);

            AddStep("delete selection", () => skinEditor.DeleteItems(skinEditor.SelectedComponents.ToArray()));
            AddUntilStep("component removed", () => !target.ChildrenOfType<SkinnableContainer>().Single().Components.Any());
        }

        [Test]
        public void TestNonSkinnableTargetShowsPlaceholder()
        {
            AddStep("create editor with bare target", () =>
            {
                Children = new Drawable[]
                {
                    target = new Container { RelativeSizeAxes = Axes.Both },
                    skinEditor = new TestSkinEditor(target),
                };
            });

            AddUntilStep("placeholder shown", () => skinEditor.ChildrenOfType<NonSkinnableScreenPlaceholder>().Any());
        }

        private partial class TestSkinEditor : SkinEditor
        {
            public TestSkinEditor(Drawable targetScreen)
                : base(targetScreen)
            {
            }

            public new void Paste() => base.Paste();
        }
    }
}
