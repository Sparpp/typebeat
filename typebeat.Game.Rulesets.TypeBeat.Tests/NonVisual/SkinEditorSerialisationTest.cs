// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using Newtonsoft.Json;
using NUnit.Framework;
using osu.Framework.Graphics;
using typebeat.Game.IO.Serialization;
using typebeat.Game.Overlays.SkinEditor;
using typebeat.Game.Screens.Edit.Components;
using typebeat.Game.Screens.Edit.Compose.Components;
using typebeat.Game.Skinning;
using typebeat.Game.Skinning.Components;
using osuTK;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Smoke coverage for the vendored skin editor stack that can run without a game host:
    /// the clipboard round-trip (<c>SkinEditor.Copy</c>/<c>Paste</c> are plain
    /// <see cref="JsonConvert"/> over <see cref="SerialisedDrawableInfo"/>), the component
    /// discovery the toolbox is built from, and bare construction of the key vendored types.
    /// </summary>
    [TestFixture]
    public class SkinEditorSerialisationTest
    {
        [Test]
        public void MovedComponentRoundTripsThroughClipboardJson()
        {
            var box = new BoxElement
            {
                Position = new Vector2(123.5f, -40),
                Rotation = 30,
                Scale = new Vector2(1.5f, 0.75f),
                Anchor = Anchor.TopCentre,
                Origin = Anchor.Centre,
                UsesFixedAnchor = true,
            };

            box.CornerRadius.Value = 0.4f;

            // The same shape SkinEditor.Copy/Paste and skin layout persistence put on the wire.
            // SkinEditor uses the host-level default serializer settings, which plain NUnit has no
            // game host to install, so serialize through the game's canonical global settings here.
            string clipboard = JsonConvert.SerializeObject(new[] { ((Drawable)box).CreateSerialisedInfo() }, JsonSerializableExtensions.CreateGlobalSettings());
            var restoredInfo = JsonConvert.DeserializeObject<SerialisedDrawableInfo[]>(clipboard, JsonSerializableExtensions.CreateGlobalSettings());

            Assert.That(restoredInfo, Is.Not.Null);
            Assert.That(restoredInfo, Has.Length.EqualTo(1));

            var restored = restoredInfo![0].CreateInstance();

            Assert.That(restored, Is.InstanceOf<BoxElement>());
            Assert.That(restored.Position, Is.EqualTo(new Vector2(123.5f, -40)));
            Assert.That(restored.Rotation, Is.EqualTo(30));
            Assert.That(restored.Scale, Is.EqualTo(new Vector2(1.5f, 0.75f)));
            Assert.That(restored.Anchor, Is.EqualTo(Anchor.TopCentre));
            Assert.That(restored.Origin, Is.EqualTo(Anchor.Centre));
            Assert.That(((BoxElement)restored).UsesFixedAnchor, Is.True);
            Assert.That(((BoxElement)restored).CornerRadius.Value, Is.EqualTo(0.4f));
        }

        [Test]
        public void GlobalSerialisableDrawablesIncludeStockComponents()
        {
            var types = SerialisedDrawableInfo.GetAllAvailableDrawables();

            Assert.That(types, Does.Contain(typeof(BoxElement)));
            Assert.That(types, Does.Contain(typeof(TextElement)));
        }

        [Test]
        public void EditorTypesConstruct()
        {
            Assert.DoesNotThrow(() =>
            {
                _ = new SkinEditor();
                _ = new SelectionBox();
                _ = new DragBox();
                _ = new EditorSidebar();
            });
        }
    }
}
