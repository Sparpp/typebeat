// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Input;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Edit;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Tests.Visual;
using osuTK.Input;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Backlog 371 in the lyric editor: the compose screen's letter hotkeys and the editor's undo
    /// answer to the KEYCAP on AZERTY, pressed here as the physical keys a French keyboard sends. The
    /// compose screen matches plain <c>Key</c> values in its own <c>OnKeyDown</c>, so nothing in it
    /// changed; it follows the keycap because the key it is handed already is one.
    /// </summary>
    public partial class TestSceneKeycapComposeShortcuts : EditorTestScene
    {
        [Resolved]
        private OsKeyboardLayout osLayout { get; set; } = null!;

        protected override Ruleset CreateEditorRuleset() => new TypeBeatRuleset();

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var beatmap = new Beatmap
            {
                HitObjects = new List<Rulesets.Objects.HitObject>(),
            };

            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.BeatmapInfo.Metadata.Artist = "Editor";
            beatmap.BeatmapInfo.Metadata.Title = "Keycaps";

            addLine(beatmap, 0, "hello world", 1000, 3000);
            addLine(beatmap, 1, "second line", 3000, 5000);

            return beatmap;
        }

        private static void addLine(Beatmap beatmap, int index, string text, double start, double end)
        {
            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = start,
                LineIndex = index,
                Line = new LyricLine
                {
                    RawText = text,
                    StartTime = start,
                    EndTime = end,
                    SingEndTime = end,
                    Units = new[] { new TimedUnit { Text = text, StartTime = start, EndTime = end } },
                },
                Granularity = TimingGranularity.Line,
            });
        }

        [SetUpSteps]
        public void SetUpAzerty()
        {
            AddStep("AZERTY keycaps", () => osLayout.Current.Value = KeyboardLayout.Azerty);
            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());
            AddUntilStep("no text box has focus", () => Editor.ChildrenOfType<OsuTextBox>().All(b => !b.HasFocus));
        }

        [TearDownSteps]
        public void RestoreQwerty() => AddStep("QWERTY keycaps", () => osLayout.Current.Value = KeyboardLayout.Qwerty);

        /// <summary>
        /// M (merge with the next line) is the M keycap, which AZERTY puts on QWERTY's semicolon
        /// position; the physical M position carries ',' there and merges nothing.
        /// </summary>
        [Test]
        public void TestMergeFollowsTheMKeycap()
        {
            AddAssert("2 lines", () => EditorBeatmap.HitObjects.Count == 2);

            AddStep("press the physical M key (the ',' keycap)", () => InputManager.Key(Key.M));
            AddAssert("nothing merged", () => EditorBeatmap.HitObjects.Count == 2);

            AddStep("press the physical semicolon key (the M keycap)", () => InputManager.Key(Key.Semicolon));
            AddUntilStep("the lines merged", () => EditorBeatmap.HitObjects.Count == 1);
        }

        /// <summary>R (replay the line) sits on the same position on all three layouts, and still replays.</summary>
        [Test]
        public void TestReplayLineIsStillR()
        {
            AddAssert("paused", () => !EditorClock.IsRunning);
            AddStep("press R", () => InputManager.Key(Key.R));
            AddUntilStep("the line replays", () => EditorClock.IsRunning);
            AddStep("stop", () => EditorClock.Stop());
        }

        /// <summary>The editor's undo, a framework platform action, is Ctrl plus the Z keycap (QWERTY's W position).</summary>
        [Test]
        public void TestUndoIsCtrlPlusTheZKeycap()
        {
            AddStep("delete the first line", () => TypeBeatEditorOperations.DeleteLine(EditorBeatmap, TypeBeatEditorOperations.OrderedLines(EditorBeatmap)[0]));
            AddAssert("1 line", () => EditorBeatmap.HitObjects.Count == 1);

            ctrl(Key.Z);
            AddAssert("Ctrl plus the physical Z key (the W keycap) undid nothing", () => EditorBeatmap.HitObjects.Count == 1);

            ctrl(Key.W);
            AddUntilStep("Ctrl plus the Z keycap undid the delete", () => EditorBeatmap.HitObjects.Count == 2);
        }

        private void ctrl(Key key) => AddStep($"press Ctrl+{key} (physical)", () =>
        {
            InputManager.PressKey(Key.ControlLeft);
            InputManager.Key(key);
            InputManager.ReleaseKey(Key.ControlLeft);
        });
    }
}
