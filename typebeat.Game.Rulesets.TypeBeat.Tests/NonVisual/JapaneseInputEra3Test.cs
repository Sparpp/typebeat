// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Replays;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Replays;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Extended replay bit 8 covers the romaji input PLAN as well as the word skip: a line whose
    /// only original is one unspaced word is read per timed unit under the era, and exactly as
    /// before it without.
    /// </summary>
    [TestFixture]
    public class JapaneseInputEra3Test
    {
        [Test]
        public void TheEraSegmentsALineOnlyOriginalAndRecordsItsWords()
        {
            var plan = JapaneseInputPlan.Create("明日と", line());
            Assert.That(plan.WordAt(0), Is.EqualTo(new JapaneseInputPlan.Word(0, 2)));
            Assert.That(plan.WordAt(2), Is.EqualTo(new JapaneseInputPlan.Word(2, 3)));
            Assert.That(plan.At(0)!.Reading, Is.EqualTo("あす"), "the mapper's spelling is the reading");
        }

        [Test]
        public void WithoutTheEraThePlanIsTheDictionaryOne()
        {
            var plan = JapaneseInputPlan.Create("明日と", line(), era3: false);
            Assert.That(plan.At(0)!.Reading, Is.EqualTo("あした"), "a line-only original has no unit to read per word");
            Assert.That(plan.WordAt(2), Is.EqualTo(new JapaneseInputPlan.Word(2, 3)), "only dictionary words remain");
            Assert.That(plan.WordAt(0), Is.EqualTo(new JapaneseInputPlan.Word(0, 2)));
        }

        [TestCase(true, "asuto")]
        [TestCase(false, "ashitato")]
        public void TheEngineTypesThePlanOfItsEra(bool era3, string input)
        {
            var engine = create();
            engine.JapaneseInputEra3 = era3;
            foreach (char c in input)
                Assert.That(engine.ProcessKey(c, 1100), Is.True, c.ToString());
            Assert.That(engine.Lines[0].Cells.Select(c => c.State), Is.All.EqualTo(CellState.Correct));
            Assert.That(engine.Mistypes, Is.Zero);
        }

        [Test]
        public void FlippingTheEraBackRestoresTheLivePlan()
        {
            var engine = create();
            engine.JapaneseInputEra3 = false;
            engine.JapaneseInputEra3 = true;
            foreach (char c in "asuto")
                Assert.That(engine.ProcessKey(c, 1100), Is.True, c.ToString());
            Assert.That(engine.Lines[0].Cells.Select(c => c.State), Is.All.EqualTo(CellState.Correct));
        }

        /// <summary>
        /// A replay whose extended frame lacks bit 8 re-derives the plan and the skip it was played
        /// on: the dictionary reading types, and Space abandons one character rather than the word.
        /// </summary>
        [TestCase(false)]
        [TestCase(true)]
        public void AReplayReDerivesThePlanAndSkipOfItsEra(bool era3)
        {
            var extended = TypeBeatReplayFrame.CreateExtendedConfigFrame(0, inputEra2: true, authoredSyllablesOnly: true,
                japaneseRomajiInput: true, japaneseWordTiming: true, japaneseInputEra2: true, japaneseInputEra3: era3);
            var decoded = new TypeBeatReplayFrame { Time = 0 };
            decoded.FromLegacy(extended.ToLegacy(new Beatmap()), new Beatmap());
            string typed = era3 ? "asu" : "ashita";
            var frames = new ReplayFrame[] { TypeBeatReplayFrame.CreateConfigFrame(0, true, spaceSkipsWord: true), decoded }
                         .Concat(typed.Select((c, i) => (ReplayFrame)new TypeBeatReplayFrame(1100 + i, c)))
                         .Append(new TypeBeatReplayFrame(2600, 't'))
                         .Append(new TypeBeatReplayFrame(2610, ' '))
                         .ToArray();
            var engine = create();
            ReplayEngineFeed.RebuildTo(engine, frames, 2700);
            Assert.Multiple(() =>
            {
                Assert.That(engine.JapaneseInputEra3, Is.EqualTo(era3));
                Assert.That(engine.Lines[0].Cells.Take(2).Select(c => c.State), Is.All.EqualTo(CellState.Correct));
                Assert.That(engine.Lines[0].Cells[2].State, Is.EqualTo(CellState.Abandoned));
                Assert.That(engine.Mistypes, Is.Zero);
            });
        }

        [Test]
        public void ALegacySkipAbandonsOneCharacterOfAWord()
        {
            var engine = create();
            engine.JapaneseInputEra3 = false;
            engine.ProcessKey('a', 1100);
            Assert.That(engine.ProcessKey(' ', 1200), Is.True);
            Assert.That(engine.CaretIndex, Is.EqualTo(1), "only 明 was skipped");
            Assert.That(engine.Lines[0].Cells[1].State, Is.EqualTo(CellState.Untyped));

            var live = create();
            live.ProcessKey('a', 1100);
            Assert.That(live.ProcessKey(' ', 1200), Is.True);
            Assert.That(live.CaretIndex, Is.EqualTo(2), "the whole of 明日 was skipped");
        }

        private static LyricLine line() => new LyricLine
        {
            RawText = "asu to", Original = "明日と", StartTime = 0, EndTime = 6000, SingEndTime = 5000,
            Units = new[]
            {
                new TimedUnit { Text = "asu", StartTime = 1000, EndTime = 2000 },
                new TimedUnit { Text = "to", StartTime = 2500, EndTime = 5000 },
            },
        };

        private static TypingEngine create()
        {
            var engine = new TypingEngine(new LyricBeatmap
            {
                Metadata = new LyricBeatmapMetadata { Artist = "A", Title = "T", AudioFileName = "a.mp3", FolderPath = string.Empty },
                Granularity = TimingGranularity.Word,
                Lines = new[] { line() },
            }, polyglot: true, polyglotLanguage: "japanese")
            {
                // This fixture compares replay bit 8 before character timing was introduced in bit 9.
                JapaneseCharacterTiming = false,
                SpaceSkipsWord = true, InputEra2 = true, AuthoredSyllablesOnly = true,
            };
            engine.Update(1000);
            return engine;
        }
    }
}
