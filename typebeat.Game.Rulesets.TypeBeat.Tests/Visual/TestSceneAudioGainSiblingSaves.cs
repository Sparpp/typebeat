// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Database;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.IO;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Screens.Edit;
using typebeat.Game.Screens.Edit.Setup;
using typebeat.Game.Storyboards;
using typebeat.Game.Tests.Visual;
using Decoder = typebeat.Game.Beatmaps.Formats.Decoder;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Releasing the audio gain bar saves the open difficulty on the update thread and every sibling in the
    /// background, one release after another: whatever the order the saves finish in, every difficulty's file
    /// ends on the last gain released, every row still resolves its file, and the editor's own graph takes the
    /// siblings' new files so its next save does not write the old ones back.
    /// </summary>
    public partial class TestSceneAudioGainSiblingSaves : EditorTestScene
    {
        [Resolved]
        private BeatmapManager manager { get; set; } = null!;

        [Resolved]
        private RealmAccess database { get; set; } = null!;

        /// <summary>Every difficulty of the fixture, the opened one first.</summary>
        private Guid[] ids = Array.Empty<Guid>();

        protected override bool IsolateSavingFromDatabase => false;
        protected override Ruleset CreateEditorRuleset() => new TypeBeatRuleset();

        /// <summary>Three difficulties, the editor opened on the second.</summary>
        protected override WorkingBeatmap CreateWorkingBeatmap(IBeatmap beatmap, Storyboard? storyboard = null)
        {
            var ruleset = database.Run(r => r.Find<RulesetInfo>("typebeat")!.Detach());
            var first = manager.CreateNew(ruleset, API.LocalUser.Value);
            Guid firstId = first.BeatmapInfo.ID;

            manager.Save(first.BeatmapInfo, new TypeBeatBeatmap { BeatmapInfo = first.BeatmapInfo });

            var setOrder = new List<Guid> { firstId };

            for (int i = 0; i < 2; i++)
                setOrder.Add(manager.CopyExistingDifficulty(reload(firstId).BeatmapSetInfo, reload(firstId)).BeatmapInfo.ID);

            ids = setOrder.Skip(1).Take(1).Concat(setOrder.Where(id => id != setOrder[1])).ToArray();
            return reload(ids[0]);
        }

        [Test]
        public void OneReleaseWritesEveryDifficulty()
        {
            showSetup();

            AddStep("release the bar at 150%", () => gainBar().Current.Value = 1.5);
            waitForSiblingSaves();

            AddAssert("every file carries the gain", () => ids.All(id => savedGain(id) == 1.5));
            AddAssert("every row carries the gain", () => ids.All(id => savedRow(id).Metadata.AudioGain == 1.5));
            AddAssert("every row still resolves its file", () => ids.All(rowResolvesItsFile));
        }

        [Test]
        public void TheLastOfTwoQuickReleasesWins()
        {
            showSetup();

            AddStep("release twice before the first save is done", () =>
            {
                gainBar().Current.Value = 1.5;
                gainBar().Current.Value = 2;
            });
            waitForSiblingSaves();

            AddAssert("every file carries the second gain", () => ids.All(id => savedGain(id) == 2));
            AddAssert("every row still resolves its file", () => ids.All(rowResolvesItsFile));
        }

        /// <summary>
        /// The one window the background leaves: an editor save between a sibling's background write and the
        /// update thread taking its new file into the graph writes the old file's hash back. Forced here by
        /// waiting for the writes inside the step and saving before the frame ends.
        /// </summary>
        [Test]
        public void AnEditorSaveInsideTheWindowIsRepaired()
        {
            showSetup();

            AddStep("release, wait for the writes, save the editor at once", () =>
            {
                gainBar().Current.Value = 1.5;
                Assert.That(resources().LastSiblingGainSave.Wait(TimeSpan.FromSeconds(30)), Is.True, "background saves did not finish");
                Editor.Save();

                // Checked inside the same frame: the update thread takes the new files at the start of the next one.
                Assert.That(ids.Skip(1).Any(id => !rowResolvesItsFile(id)), Is.True, "the save did not write a stale hash back");
            });

            waitForSiblingSaves();

            AddAssert("every row resolves its file again", () => ids.All(rowResolvesItsFile));
            AddAssert("every file carries the gain", () => ids.All(id => savedGain(id) == 1.5));

            AddStep("save the editor again", () => Editor.Save());
            AddAssert("and it keeps them", () => ids.All(rowResolvesItsFile) && ids.All(id => savedGain(id) == 1.5));
        }

        /// <summary>
        /// The queue runs as one of realm's pending async operations, which realm's disposal on exit waits for,
        /// so quitting while siblings are still being written does not cut the later ones off.
        /// </summary>
        [Test]
        public void TheQueueIsAPendingRealmOperation()
        {
            showSetup();

            AddStep("release the bar", () =>
            {
                gainBar().Current.Value = 1.5;
                Assert.That(pendingRealmOperations(), Is.GreaterThan(0), "the sibling saves are not tracked by realm");
            });
            waitForSiblingSaves();
        }

        private int pendingRealmOperations()
            => ((System.Threading.CountdownEvent)typeof(RealmAccess).GetField("pendingAsyncOperations", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                                                                  .GetValue(database)!).CurrentCount;

        private void showSetup()
        {
            AddStep("open setup", () => Editor.Mode.Value = EditorScreenMode.SongSetup);
            AddUntilStep("resources ready", () => Editor.ChildrenOfType<ResourcesSection>().Any());
        }

        /// <summary>The background writes done, and the editor's graph holding each sibling's current file.</summary>
        private void waitForSiblingSaves()
        {
            AddUntilStep("background saves done", () => resources().LastSiblingGainSave.IsCompleted);
            AddUntilStep("editor graph took the new files", () => ids.Skip(1).All(id =>
                Beatmap.Value.BeatmapSetInfo.Beatmaps.Single(b => b.ID == id).Hash == savedRow(id).Hash && rowResolvesItsFile(id)));
        }

        private ResourcesSection resources() => Editor.ChildrenOfType<ResourcesSection>().Single();

        private FormSliderBar<double> gainBar() => resources().ChildrenOfType<FormSliderBar<double>>().Single(s => s.Caption.ToString() == "Audio gain");

        private BeatmapInfo savedRow(Guid id) => database.Run(r => r.Find<BeatmapInfo>(id)!.Detach());

        // Read on the managed row: a detached set carries no files to resolve against.
        private bool rowResolvesItsFile(Guid id) => database.Run(r => r.Find<BeatmapInfo>(id)!.File != null);

        private WorkingBeatmap reload(Guid id)
        {
            var info = database.Run(r => r.Find<BeatmapInfo>(id)!.Detach());
            ((IWorkingBeatmapCache)manager).Invalidate(info.BeatmapSet!);
            return manager.GetWorkingBeatmap(info);
        }

        /// <summary>The gain the difficulty's .osu file itself states, decoded from what was written.</summary>
        private double savedGain(Guid id)
        {
            var working = reload(id);
            using var stream = working.GetStream(working.BeatmapSetInfo.GetPathForFile(working.BeatmapInfo.Path!));
            using var reader = new LineBufferedReader(stream);
            return Decoder.GetDecoder<Beatmap>(reader).Decode(reader).BeatmapInfo.Metadata.AudioGain;
        }
    }
}
