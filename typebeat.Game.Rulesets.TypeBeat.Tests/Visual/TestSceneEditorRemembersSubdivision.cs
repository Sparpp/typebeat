// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Reflection;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Screens;
using typebeat.Game.Beatmaps;
using typebeat.Game.Database;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Screens.Edit;
using typebeat.Game.Storyboards;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Backlog 507: changing only the grid subdivision is not an unsaved change, so leaving the editor
    /// used to drop it. It is kept in the map's local row on exit, without rewriting the map file.
    /// </summary>
    public partial class TestSceneEditorRemembersSubdivision : EditorTestScene
    {
        [Resolved]
        private BeatmapManager manager { get; set; } = null!;

        [Resolved]
        private RealmAccess database { get; set; } = null!;

        private Guid id;

        protected override bool IsolateSavingFromDatabase => false;
        protected override Ruleset CreateEditorRuleset() => new TypeBeatRuleset();

        protected override WorkingBeatmap CreateWorkingBeatmap(IBeatmap beatmap, Storyboard? storyboard = null)
        {
            var ruleset = database.Run(r => r.Find<RulesetInfo>("typebeat")!.Detach());
            var created = manager.CreateNew(ruleset, API.LocalUser.Value);
            id = created.BeatmapInfo.ID;

            manager.Save(created.BeatmapInfo, new TypeBeatBeatmap { BeatmapInfo = created.BeatmapInfo });
            return manager.GetWorkingBeatmap(database.Run(r => r.Find<BeatmapInfo>(id)!.Detach()));
        }

        [Test]
        public void TestLeavingWithoutSavingKeepsTheSubdivision()
        {
            string hash = string.Empty;

            AddStep("note the file", () => hash = database.Run(r => r.Find<BeatmapInfo>(id)!.Hash));
            AddStep("pick 1/3", () => divisor().SetArbitraryDivisor(3));
            AddStep("leave without saving", () => Editor.Exit());

            AddUntilStep("the row keeps 1/3", () => database.Run(r => r.Find<BeatmapInfo>(id)!.BeatDivisor) == 3);
            AddAssert("the file was not rewritten", () => database.Run(r => r.Find<BeatmapInfo>(id)!.Hash) == hash);
        }

        /// <summary>The editor's own divisor, the one the Timing menu sets.</summary>
        private BindableBeatDivisor divisor()
            => (BindableBeatDivisor)typeof(Editor).GetField("beatDivisor", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Editor)!;
    }
}
