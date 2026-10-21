// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using osu.Framework.Graphics;
using typebeat.Game.Rulesets.TypeBeat.Edit;
using typebeat.Game.Tests.Visual;
using osuTK.Input;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    public partial class TestSceneTypeBeatOriginalLyricInput : OsuManualInputManagerTestScene
    {
        [TestCase(true, "「あいう」、。")]
        [TestCase(false, "[あいう],.")]
        public void TestCommittedInputAndIme(bool japanese, string expected)
        {
            OriginalLyricTextBox box = null!;
            var os = ((KeycapManualInputManager)InputManager).TextInput;
            AddStep("create input", () => Child = box = new OriginalLyricTextBox
            {
                Anchor = Anchor.Centre, Origin = Anchor.Centre, Width = 400, Height = 40,
                IsJapaneseOriginal = () => japanese,
            });
            AddStep("focus", () =>
            {
                InputManager.MoveMouseTo(box);
                InputManager.Click(MouseButton.Left);
            });
            AddUntilStep("focused", () => box.HasFocus);
            AddStep("commit text", () => os.Text("[あいう],."));
            AddUntilStep("normalized without committing field", () => box.Text == expected);
            AddStep("move caret into text", () => InputManager.Key(Key.Home));
            AddStep("insert punctuation", () => os.Text("."));
            AddUntilStep("caret retained", () => box.Text == (japanese ? "。" : ".") + expected);
            AddStep("type after replaced character", () => os.Text("a"));
            AddUntilStep("caret remains after replacement", () => box.Text == (japanese ? "。a" : ".a") + expected);
            AddStep("clear input", () => box.Text = "");
            AddStep("compose", () => os.TriggerImeComposition("[あいう],.", 7, 0));
            AddUntilStep("composition untouched", () => box.Text == "[あいう],.");
            AddStep("finish composition", () => os.TriggerImeResult("[あいう],."));
            AddUntilStep("result normalized", () => box.Text == expected);
        }
    }
}
