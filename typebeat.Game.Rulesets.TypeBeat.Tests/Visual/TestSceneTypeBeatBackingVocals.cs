// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Timing;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// A line's BACKING VOCALS in gameplay (backlog 445): hidden until sung, then thrown up above the
    /// lyric over their anchor with a little overshoot and a tilt of their own, and kept there until the
    /// line goes. Driven by a manual clock, so every pose is a pure function of the song time: a seek
    /// back hides them again, and the "Show backing vocals" setting hides them live.
    /// </summary>
    public partial class TestSceneTypeBeatBackingVocals : OsuTestScene
    {
        private ManualClock clock = null!;
        private LyricLineDisplay display = null!;

        private static LyricLine line(params BackingVocal[] backing) => new LyricLine
        {
            RawText = "i wanna let my hair",
            StartTime = 1000,
            EndTime = 5000,
            SingEndTime = 4600,
            Units = new[]
            {
                new TimedUnit { Text = "i", StartTime = 1000, EndTime = 1400, Source = TimingSource.Explicit },
                new TimedUnit { Text = "wanna", StartTime = 1400, EndTime = 2200, Source = TimingSource.Explicit },
                new TimedUnit { Text = "let", StartTime = 2200, EndTime = 2800, Source = TimingSource.Explicit },
                new TimedUnit { Text = "my", StartTime = 2800, EndTime = 3400, Source = TimingSource.Explicit },
                new TimedUnit { Text = "hair", StartTime = 3400, EndTime = 4600, Source = TimingSource.Explicit },
            },
            BackingVocals = backing,
        };

        private void create(LyricLine source)
        {
            AddStep("create the line", () =>
            {
                clock = new ManualClock { CurrentTime = 0 };

                var beatmap = new LyricBeatmap
                {
                    Metadata = new LyricBeatmapMetadata { Artist = "Backing", Title = "Vocals", FolderPath = @"X:\nowhere", AudioFileName = "a.mp3" },
                    Lines = new[] { source },
                    Granularity = TimingGranularity.Word,
                };
                var engine = new TypingEngine(beatmap);

                Child = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Clock = new FramedClock(clock),
                    Child = display = new LyricLineDisplay(engine.Lines[0], engine)
                    {
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                    },
                };
            });

            AddUntilStep("loaded", () => display.IsLoaded && display.BackingVocalCount == source.BackingVocals.Count);
        }

        private void seek(double time)
        {
            AddStep($"seek to {time}", () => clock.CurrentTime = time);
            AddWaitStep("a frame", 1);
        }

        private float alpha(int index) => display.BackingVocalText(index).Alpha;

        [Test]
        public void TestBackingWordsPopUpWhenSungAndStay()
        {
            var first = new BackingVocal("down", 2600, 3000, 0.05);
            var second = new BackingVocal("down", 3800, 4200);

            create(line(first, second));

            seek(2000);
            AddAssert("nothing shows before it is sung", () => alpha(0) == 0 && alpha(1) == 0);

            seek(2600 + LyricLineDisplay.BACKING_POP_DURATION_MS / 6);
            AddAssert("the first is on its way up", () => alpha(0) > 0 && alpha(0) < 1 && display.BackingVocalText(0).Scale.X < 1);
            AddAssert("rising from below its resting place", () => display.BackingVocalText(0).Y > display.BackingVocalRest(0).Y);
            AddAssert("the second is not sung yet", () => alpha(1) == 0);

            seek(3500);
            AddAssert("settled: shown, full size, at its own tilt, where it rests", () =>
            {
                var text = display.BackingVocalText(0);
                return alpha(0) == 1 && text.Scale == osuTK.Vector2.One
                                     && Math.Abs(text.Rotation - LyricLineDisplay.BackingTilt(first, 0)) < 1e-3
                                     && (text.Position - display.BackingVocalRest(0)).Length < 0.01f;
            });

            seek(4999);
            AddAssert("both stay up to the end of the line", () => alpha(0) == 1 && alpha(1) == 1);

            seek(1500);
            AddAssert("seeking back hides them again", () => alpha(0) == 0 && alpha(1) == 0);
        }

        [Test]
        public void TestAnAnchorPlacesTheWordAcrossTheLine()
        {
            create(line(new BackingVocal("left", 2000, 2400, 0), new BackingVocal("mid", 2600, 3000), new BackingVocal("right", 3200, 3600, 1)));

            AddAssert("left at the lyric's start, right at its end, an unplaced one on its default place", () =>
            {
                float width = display.BackingLineWidth;
                return width > 0
                       && Math.Abs(display.BackingVocalRest(0).X) < 0.5f
                       && Math.Abs(display.BackingVocalRest(1).X - width * 0.5f) < 0.5f
                       && Math.Abs(display.BackingVocalRest(2).X - width) < 0.5f;
            });

            AddAssert("all above the lyric", () => Enumerable.Range(0, 3).All(i => display.BackingVocalRest(i).Y < 0));
        }

        [Test]
        public void TestTheSettingHidesThemLive()
        {
            create(line(new BackingVocal("down", 2600, 3000)));

            seek(3500);
            AddAssert("shown", () => alpha(0) == 1);

            AddStep("turn backing vocals off", () => display.SetBackingVocalsEnabled(false));
            AddAssert("hidden", () => alpha(0) == 0);

            AddStep("and back on", () => display.SetBackingVocalsEnabled(true));
            AddAssert("shown again", () => alpha(0) == 1);
        }

        [Test]
        public void TestTheHidingModsHideThem()
        {
            create(line(new BackingVocal("down", 2600, 3000)));

            seek(3500);
            AddAssert("shown", () => alpha(0) == 1);

            AddStep("Recite on", () => display.SetReciteEnabled(true));
            AddAssert("hidden under Recite", () => alpha(0) == 0);
            AddStep("Recite off", () => display.SetReciteEnabled(false));
            AddAssert("shown again", () => alpha(0) == 1);

            AddStep("Flashlight on, the line lit", () => display.SetFlashlightWindow(new LineWindow(0, 4, false, false), showSweep: true));
            AddAssert("hidden under Flashlight", () => alpha(0) == 0);
        }

        [Test]
        public void TestALineWithoutBackingVocalsDrawsNone()
        {
            create(line());
            AddAssert("none", () => display.BackingVocalCount == 0);
        }
    }
}
