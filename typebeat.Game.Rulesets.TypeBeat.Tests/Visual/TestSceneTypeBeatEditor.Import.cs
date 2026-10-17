// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Database;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Edit;
using typebeat.Game.Screens.Edit;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    public partial class TestSceneTypeBeatEditor
    {
        private readonly List<string> lyricImportFiles = new List<string>();

        [TearDownSteps]
        public void CleanupLyricImportFiles() => AddStep("remove temporary lyric files", () =>
        {
            foreach (string path in lyricImportFiles)
                File.Delete(path);
            lyricImportFiles.Clear();
        });

        [TestCase(".elrc", true)]
        [TestCase(".ELRC", true)]
        [TestCase(".elrc", false)]
        [TestCase(".lrc", false)]
        public void TestEnhancedLrcImportsItsOwnTimingWithoutAudio(string extension, bool drop)
        {
            string path = Path.Combine(Path.GetTempPath(), $"typebeat_editor_lyrics_{Guid.NewGuid():N}{extension}");
            FormFileSelector selector() => Editor.ChildrenOfType<TypeBeatSetupSection>().Single()
                .ChildrenOfType<FormFileSelector>().Single(s => s.Caption.ToString() == "Lyrics file");

            AddStep("open setup", () => Editor.Mode.Value = EditorScreenMode.SongSetup);
            AddUntilStep("lyric picker ready", () => Editor.ChildrenOfType<TypeBeatSetupSection>().Any());
            AddAssert("file chooser accepts enhanced LRC", () => selector().HandledExtensions.Contains(".elrc"));
            AddStep("write word-timed Japanese lyrics", () =>
            {
                lyricImportFiles.Add(path);
                File.WriteAllText(path, "[offset:250]\n[00:01.000]<00:01.000>さくら <00:02.250>せかい<00:03.000>\n[00:05.000]\n");
                EditorBeatmap.Metadata.Language = BeatmapLanguage.Japanese;
                EditorBeatmap.Metadata.AudioFile = string.Empty;
            });
            AddStep(drop ? "drop enhanced LRC" : "select enhanced LRC", () =>
            {
                if (drop)
                    _ = ((ICanAcceptFiles)selector()).Import(path);
                else
                    selector().Current.Value = new FileInfo(path);
            });
            AddUntilStep("selected file assigned", () => selector().Current.Value?.FullName == path);
            AddAssert("original-script file is staged without replacing the map", () =>
                EditorBeatmap.HitObjects.Count == 2 && firstLine().Line.RawText == "hello world");
            AddUntilStep("original lyrics can be aligned from the selected file", () => Editor.ChildrenOfType<TypeBeatSetupSection>().Single()
                .ChildrenOfType<FormButton>().Single(b => b.ButtonText.ToString() == "Generate lyrics").Enabled.Value);
            AddStep("generate timing from the file", () => Editor.ChildrenOfType<TypeBeatSetupSection>().Single()
                .ChildrenOfType<FormButton>().Single(b => b.ButtonText.ToString() == "Generate timing").Action!());
            AddUntilStep("word-timed lyrics imported", () => EditorBeatmap.HitObjects.Count == 1 && firstLine().Line.RawText == "sakura sekai");
            AddAssert("word timestamps and offset preserved", () =>
                firstLine().Line.Units.Select(u => u.StartTime).SequenceEqual(new[] { 750d, 2000 })
                && firstLine().Line.Units.Select(u => u.EndTime).SequenceEqual(new[] { 2000d, 2750 }));
            AddAssert("original lyrics retained", () =>
                firstLine().Line.Units.Select(u => u.Original).SequenceEqual(new[] { "さくら", "せかい" }));
            AddStep("undo import", () => Editor.Undo());
            AddUntilStep("previous map restored", () => EditorBeatmap.HitObjects.Count == 2 && firstLine().Line.RawText == "hello world");
            AddStep("redo import", () => Editor.Redo());
            AddUntilStep("word timing restored", () => EditorBeatmap.HitObjects.Count == 1
                && firstLine().Line.RawText == "sakura sekai" && firstLine().Line.Units[1].StartTime == 2000);
        }
    }
}
