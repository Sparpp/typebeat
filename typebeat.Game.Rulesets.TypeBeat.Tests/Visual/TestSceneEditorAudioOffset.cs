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

        /// <summary>
        /// The editor's audio follows the global audio offset exactly as gameplay does (as in lazer):
        /// the offset compensates the player's own hardware latency, so authoring against it keeps
        /// what the mapper hears in step with what the player will hear.
        /// </summary>
        [TestCase(-125)]
        [TestCase(125)]
        public void EditorAppliesTheAudioOffset(double offset)
        {
            EditorClock editor = null!;
            double atZero = 0;
            AddStep("load editor with zero audio offset", () =>
            {
                config.SetValue(OsuSetting.AudioOffset, 0.0);
                Child = editor = new EditorClock();
                editor.ChangeSource(new StopwatchClock());
            });
            AddUntilStep("editor loaded", () => editor.IsLoaded);
            AddStep("seek at zero offset", () =>
            {
                editor.Seek(10000);
                atZero = editor.Source.CurrentTime;
            });
            AddStep("set audio offset", () => config.SetValue(OsuSetting.AudioOffset, offset));
            AddStep("seek again", () => editor.Seek(10000));
            AddAssert("audio seeks by the offset", () => editor.Source.CurrentTime, () => Is.EqualTo(atZero - offset).Within(1));
            AddAssert("editor shows the lyric time", () => editor.CurrentTime, () => Is.EqualTo(10000).Within(1));
        }

        [TestCase(-125)]
        [TestCase(125)]
        public void GameplayAppliesTheAudioOffset(double offset)
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
