// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Testing;
using osu.Framework.Input.Bindings;
using typebeat.Game.Input.Bindings;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.Timing;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Screens.Play;
using typebeat.Game.Screens.Play.PlayerSettings;
using typebeat.Game.Tests.Visual;
using osuTK.Input;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    public partial class TestSceneTypeBeatOffsetShortcuts : PlayerTestScene
    {
        protected override Ruleset CreatePlayerRuleset() => new TypeBeatRuleset();
        protected override bool HasCustomSteps => true;
        protected override TestPlayer CreatePlayer(Ruleset ruleset) => new HeldPlayer();

        private DrawableTypeBeatRuleset ruleset => (DrawableTypeBeatRuleset)Player.DrawableRuleset;
        private TypingEngine engine => ruleset.Engine;
        private BeatmapOffsetControl offset => Player.ChildrenOfType<BeatmapOffsetControl>().Single();

        protected override IBeatmap CreateBeatmap(RulesetInfo rulesetInfo)
        {
            var beatmap = new TypeBeatBeatmap { BeatmapInfo = { Ruleset = rulesetInfo } };
            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = 10000,
                LineIndex = 0,
                Granularity = TimingGranularity.Word,
                Line = new LyricLine
                {
                    RawText = "I-I",
                    StartTime = 10000,
                    EndTime = 13000,
                    SingEndTime = 11000,
                    Units = new[] { new TimedUnit { Text = "I-I", StartTime = 10000, EndTime = 11000 } },
                },
            });
            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = 13000,
                LineIndex = 1,
                Granularity = TimingGranularity.Word,
                Line = new LyricLine
                {
                    RawText = "later",
                    StartTime = 13000,
                    EndTime = 65000,
                    SingEndTime = 65000,
                    Units = new[] { new TimedUnit { Text = "later", StartTime = 60000, EndTime = 65000 } },
                },
            });
            beatmap.Breaks.Add(new BreakPeriod(13000, 58000));
            return beatmap;
        }

        [Test]
        public void OffsetShortcutsWorkBeforeTypingBegins()
        {
            load();
            seek(9000);
            AddAssert("offset shortcuts allowed", () => ruleset.AllowGameplayOffsetShortcuts && !offset.Current.Disabled);
            shifted(Key.Minus);
            AddAssert("minus decreased offset", () => offset.Current.Value == -1);
            shifted(Key.Plus);
            AddAssert("plus increased offset", () => offset.Current.Value == 0);
        }

        [Test]
        public void OffsetShortcutsCloseAtTheFirstLinesEarlyInputWindow()
        {
            load();
            seek(9700);
            AddAssert("early typing window is open", () => engine.FirstLineTypingOpensAt(9700));
            AddAssert("general adjustment is still allowed", () => !offset.Current.Disabled);
            AddAssert("offset shortcuts closed", () => !ruleset.AllowGameplayOffsetShortcuts);
            shifted(Key.Minus);
            shifted(Key.Plus);
            AddAssert("both shortcuts left offset unchanged", () => offset.Current.Value == 0);
        }

        [Test]
        public void ShiftedHyphenTypesWithoutChangingOffset()
        {
            load();
            seek(10000);
            shifted(Key.I);
            AddAssert("capital I typed", () => engine.CaretIndex == 1 && engine.Lines[0].Cells[0].TypedChar == 'I');
            shifted(Key.Minus);
            AddAssert("accidental underscore reached typing", () => engine.CaretIndex == 2 && engine.Lines[0].Cells[1].TypedChar == '_');
            AddAssert("offset unchanged", () => offset.Current.Value == 0);
            AddStep("correct the hyphen", () => { InputManager.Key(Key.BackSpace); InputManager.Key(Key.Minus); });
            shifted(Key.I);
            AddAssert("I-I typed correctly", () => engine.Lines[0].Cells.All(c => c.State == CellState.Correct));
            AddAssert("offset still unchanged", () => offset.Current.Value == 0);
        }

        [Test]
        public void OffsetShortcutsStayClosedDuringLaterBreaks()
        {
            load();
            seek(20000);
            AddAssert("general adjustment allowed during break", () => !offset.Current.Disabled);
            AddAssert("offset shortcuts closed in break", () => !ruleset.AllowGameplayOffsetShortcuts);
            shifted(Key.Minus);
            shifted(Key.Plus);
            AddAssert("offset unchanged", () => offset.Current.Value == 0);
        }

        private void load()
        {
            AddStep("bind Shift plus/minus offset shortcuts", () => Realm.Write(r =>
            {
                foreach (var binding in r.All<RealmKeyBinding>().Where(b => b.RulesetName == null && b.Variant == null
                    && (b.ActionInt == (int)GlobalAction.IncreaseOffset || b.ActionInt == (int)GlobalAction.DecreaseOffset)).ToArray())
                    r.Remove(binding);

                r.Add(new RealmKeyBinding(GlobalAction.DecreaseOffset, new KeyCombination(new[] { InputKey.Shift, InputKey.Minus })));
                r.Add(new RealmKeyBinding(GlobalAction.IncreaseOffset, new KeyCombination(new[] { InputKey.Shift, InputKey.Plus })));
            }));
            AddStep("load Literate player", () => LoadPlayer(new Mod[] { new TypeBeatModLiterate() }));
            AddUntilStep("player loaded", () => Player.IsLoaded && Player.Alpha == 1);
            AddUntilStep("offset control ready", () => offset.IsLoaded && !offset.Current.Disabled);
        }

        private void seek(double time)
        {
            AddStep($"seek to {time}", () => ((HeldGameplayClock)Player.GameplayClockContainer).HoldAt(time));
            AddUntilStep("gameplay clock reached time", () => ruleset.FrameStableClock.CurrentTime == time);
        }

        private partial class HeldPlayer : TestPlayer
        {
            protected override GameplayClockContainer CreateGameplayClockContainer(WorkingBeatmap beatmap, double gameplayStart)
                => new HeldGameplayClock(beatmap, gameplayStart);
        }

        private partial class HeldGameplayClock : MasterGameplayClockContainer
        {
            public HeldGameplayClock(WorkingBeatmap beatmap, double gameplayStart)
                : base(beatmap, gameplayStart)
            {
            }

            public void HoldAt(double time)
            {
                // Stop time without pausing, so input and the offset control remain enabled.
                GameplayClock.Stop();
                Seek(time);
            }
        }

        private void shifted(Key key) => AddStep($"Shift+{key}", () =>
        {
            InputManager.PressKey(Key.ShiftLeft);
            InputManager.Key(key);
            InputManager.ReleaseKey(Key.ShiftLeft);
        });
    }
}
