// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Testing;
using osu.Framework.Timing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Configuration;
using typebeat.Game.Rulesets.Objects;
using typebeat.Game.Screens.Edit;
using typebeat.Game.Screens.Play;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    public partial class TestSceneEditorAudioOffset : OsuTestScene
    {
        [Resolved]
        private OsuConfigManager config { get; set; } = null!;

        private double previousOffset;

        protected override Ruleset CreateRuleset() => new TypeBeatRuleset();

        [SetUpSteps]
        public void SetUpSteps() => AddStep("save audio offset", () => previousOffset = config.Get<double>(OsuSetting.AudioOffset));

        [TearDownSteps]
        public void TearDownSteps() => AddStep("restore audio offset", () => config.SetValue(OsuSetting.AudioOffset, previousOffset));

        [TestCase(-125, 0.5)]
        [TestCase(-125, 1)]
        [TestCase(-125, 2)]
        [TestCase(125, 0.5)]
        [TestCase(125, 1)]
        [TestCase(125, 2)]
        public void EditorSeeksAndPlaysAtRawAudioTime(double offset, double rate)
        {
            EditorClock editor = null!;
            AddStep("load editor with nonzero audio offset", () =>
            {
                config.SetValue(OsuSetting.AudioOffset, offset);
                Child = editor = new EditorClock();
                editor.ChangeSource(new StopwatchClock { Rate = rate });
            });
            AddUntilStep("editor loaded", () => editor.IsLoaded);
            AddStep("seek to lyric time", () => editor.Seek(10000));
            AddAssert("audio seeks to the authored time", () => editor.Source.CurrentTime, () => Is.EqualTo(10000));
            AddAssert("editor displays the authored time", () => editor.CurrentTime, () => Is.EqualTo(10000));

            AddStep("change offset while editor is open", () => config.SetValue(OsuSetting.AudioOffset, -offset));
            AddAssert("editor stays at raw audio time", () => editor.CurrentTime, () => Is.EqualTo(editor.Source.CurrentTime));
            AddStep("replace audio source", () => editor.ChangeSource(new StopwatchClock { Rate = rate }));
            AddStep("seek with new source", () => editor.Seek(20000));
            AddAssert("replacement audio also seeks without offset", () => editor.Source.CurrentTime, () => Is.EqualTo(20000));

            AddStep("play editor audio", () => editor.Start());
            AddUntilStep("audio advances", () => editor.CurrentTime > 21000);
            AddStep("stop editor audio", () => editor.Stop());
            AddAssert("playback stays aligned with audio", () => editor.CurrentTime, () => Is.EqualTo(editor.Source.CurrentTime).Within(1));
            AddAssert("gameplay setting is retained", () => config.Get<double>(OsuSetting.AudioOffset), () => Is.EqualTo(-offset));
        }

        [TestCase(-125)]
        [TestCase(125)]
        public void GameplayStillAppliesTheAudioOffset(double offset)
        {
            TestGameplayClock gameplay = null!;
            double baseline = 0;
            AddStep("load gameplay clock", () =>
            {
                config.SetValue(OsuSetting.AudioOffset, 0.0);
                var beatmap = new Beatmap();
                beatmap.HitObjects.Add(new HitObject { StartTime = 10000 });
                Beatmap.Value = CreateWorkingBeatmap(beatmap);
                Child = gameplay = new TestGameplayClock(Beatmap.Value);
            });
            AddUntilStep("gameplay loaded", () => gameplay.IsLoaded);
            AddStep("record platform offset", () => baseline = gameplay.AppliedOffset);
            AddStep("set gameplay audio offset", () => config.SetValue(OsuSetting.AudioOffset, offset));
            AddAssert("gameplay includes configured offset", () => gameplay.AppliedOffset, () => Is.EqualTo(baseline + offset));
            AddStep("seek gameplay", () => gameplay.Seek(10000));
            AddUntilStep("audio seeks with gameplay offset", () => Beatmap.Value.Track.CurrentTime == 10000 - baseline - offset);
        }

        private partial class TestGameplayClock : MasterGameplayClockContainer
        {
            public TestGameplayClock(WorkingBeatmap beatmap)
                : base(beatmap, 10000)
            {
            }

            public double AppliedOffset => GameplayClock.TotalAppliedOffset;
        }
    }
}
