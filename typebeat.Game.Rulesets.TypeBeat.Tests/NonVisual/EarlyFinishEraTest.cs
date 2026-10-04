// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// EARLY FINISH (TypingEngine.EarlyFinish): bit 4 of the SECOND CONFIG flags word. The final line seals
// the moment every typeable cell of it is typed correctly, so the run (and the results screen) follows
// the player's last word instead of waiting for the line's end. Clear, the final line seals at its end
// exactly as every stored replay was played.

using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class EarlyFinishEraTest
    {
        private static LyricLine line(string text, double start, double end) => new LyricLine
        {
            RawText = text,
            StartTime = start,
            EndTime = end,
            SingEndTime = end,
            Units = new[] { new TimedUnit { Text = text, StartTime = start, EndTime = end } },
        };

        private static TypingEngine engine(bool earlyFinish)
            => new TypingEngine(new LyricBeatmap
            {
                Metadata = new LyricBeatmapMetadata { Artist = "Test", Title = "Song", FolderPath = @"X:\nowhere", AudioFileName = "a.mp3" },
                Lines = new[] { line("ab", 1000, 2000), line("cd", 5000, 9000) },
                Granularity = TimingGranularity.Line,
            }) { EarlyFinish = earlyFinish };

        private static void typeLastLine(TypingEngine typing)
        {
            typing.Update(1000);
            typing.Update(2500);
            typing.Update(5000);
            typing.ProcessKey('c', 5000);
            typing.ProcessKey('d', 7000);
        }

        [Test]
        public void TestFinalLineSealsAsSoonAsItIsTyped()
        {
            var typing = engine(true);
            typeLastLine(typing);

            typing.Update(7100);

            Assert.IsTrue(typing.IsFinished, "fully typed final line finished the run long before its end at 9000");
        }

        [Test]
        public void TestLegacyEraWaitsForTheLineEnd()
        {
            var typing = engine(false);
            typeLastLine(typing);

            typing.Update(7100);
            Assert.IsFalse(typing.IsFinished);

            typing.Update(9000);
            Assert.IsTrue(typing.IsFinished);
        }

        [Test]
        public void TestIncompleteFinalLineIsHeldUntilItsEnd()
        {
            var typing = engine(true);
            typing.Update(1000);
            typing.Update(2500);
            typing.Update(5000);
            typing.ProcessKey('c', 5000);

            typing.Update(7100);
            Assert.IsFalse(typing.IsFinished, "one letter still owed");

            typing.ProcessKey('d', 7200);
            typing.Update(7300);
            Assert.IsTrue(typing.IsFinished, "the run ends on the player's last letter");
        }

        [Test]
        public void TestEarlierLinesStillSealOnTheirDeadline()
        {
            var typing = engine(true);
            typing.Update(1000);
            typing.ProcessKey('a', 1000);
            typing.ProcessKey('b', 1500);

            typing.Update(1500);
            Assert.IsFalse(typing.IsFinished, "the final line is still ahead");
        }

        [Test]
        public void TestEraBitRoundTripsAndIsClearedWithoutAHeader()
        {
            var frame = TypeBeatReplayFrame.CreateExtendedConfigFrame(1000, earlyFinish: true);
            var legacy = frame.ToLegacy(null!);
            Assert.AreEqual(16, legacy.MouseY, "early finish is bit 4 of the extended word");

            var decoded = new TypeBeatReplayFrame();
            decoded.FromLegacy(legacy, null!);
            Assert.IsTrue(decoded.IsConfigExtended && decoded.EarlyFinish);

            var typing = engine(true);
            ReplayEngineFeed.Apply(typing, TypeBeatReplayFrame.CreateConfigFrame(1000, allowWrongInput: false));
            Assert.IsFalse(typing.EarlyFinish, "a CONFIG frame alone is a replay recorded before the era");

            ReplayEngineFeed.Apply(typing, frame);
            Assert.IsTrue(typing.EarlyFinish);
        }
    }
}
