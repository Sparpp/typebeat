// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Replays;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// THE MAP'S FIRST LINE OPENS EARLY. Every later line can be reached up to
    /// <see cref="TypingEngine.FLETCHER_DRAG_GRACE_MS"/> before its first word by RUSHING from the line
    /// before it, but line 0 has nothing to rush from and the mapper's boundary for it is normally set
    /// ON its first word - so <see cref="TypingLine.ActivationTime"/>'s clamp to that boundary left the
    /// player unable to type a single character until the word was already being sung. It opens
    /// <see cref="TypingEngine.FIRST_LINE_LEAD_MS"/> before its word now, and no earlier; a LATER line
    /// keeps waiting for its own boundary exactly as it did.
    ///
    /// <para>The head start is an ERA (<see cref="TypingEngine.FirstLineLeadIn"/>, CONFIG frame
    /// bit 16), because it decides whether a keystroke is accepted: the live engine sets it, a bare
    /// engine and every replay stored before it keep the old gate.</para>
    /// </summary>
    public class FirstLineLeadInTest
    {
        private static TimedUnit unit(string text, double start, double end)
            => new TimedUnit { Text = text, StartTime = start, EndTime = end };

        private static LyricLine line(string text, double start, double end, double singEnd, params TimedUnit[] units)
            => new LyricLine { RawText = text, StartTime = start, EndTime = end, SingEndTime = singEnd, Units = units };

        private static LyricBeatmap map(params LyricLine[] lines) => new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata { Artist = "Test", Title = "Song", FolderPath = @"X:\nowhere", AudioFileName = "a.mp3" },
            Lines = lines,
            Granularity = TimingGranularity.Line,
        };

        // Both lines put their boundary ON their first word, which is the shape that needed the fix:
        // line 0's word is at 3000 and line 1's at 4000, so the line's own ActivationTime is 3000 and
        // 4000 - the whole of the wait the player used to have.
        private static LyricBeatmap twoLines() => map(
            line("ab", 3000, 4000, 3800, unit("ab", 3000, 3800)),
            line("cd", 4000, 5000, 4800, unit("cd", 4000, 4800)));

        [Test]
        public void TheFirstLineOpensBeforeItsFirstWord()
        {
            var engine = new TypingEngine(twoLines()) { FirstLineLeadIn = true };

            engine.Update(2699);
            Assert.That(engine.ActiveLineIndex, Is.EqualTo(-1), "one ms short of the lead is still too early");
            Assert.That(engine.ProcessKey('a', 2699), Is.False, "and a press there is refused");

            engine.Update(2700);
            Assert.That(engine.ActiveLineIndex, Is.EqualTo(-1), "the clock alone does not hand the line over early");

            Assert.That(engine.ProcessKey('a', 2700), Is.True, "but a PRESS opens it, FIRST_LINE_LEAD_MS before the word");
            Assert.That(engine.ActiveLineIndex, Is.EqualTo(0), "and the caret is on it from that press");
        }

        [Test]
        public void ALaterLineStillWaitsForItsOwnBoundary()
        {
            // A short first line and a gap, so the later line can be watched opening on its own: the
            // FIRST line in this map is over long before the second one is due.
            var engine = new TypingEngine(map(
                line("ab", 1000, 2000, 1800, unit("ab", 1000, 1800)),
                line("cd", 3000, 4000, 3800, unit("cd", 3000, 3800))))
            {
                FirstLineLeadIn = true,
            };

            // The line's own activation is untouched - only the first line's GATE moves - which is
            // what keeps the WPM clock (armed from ActivationTime) re-deriving a stored run as played.
            Assert.Multiple(() =>
            {
                Assert.That(engine.Lines[0].ActivationTime, Is.EqualTo(1000), "the first line's own activation has not moved");
                Assert.That(engine.Lines[1].ActivationTime, Is.EqualTo(3000), "nor has a later line's");
            });

            // Line 0 has sealed by now, and line 1's word is not due for another 900 ms: nothing is
            // active, because the head start belongs to the map's first line alone.
            engine.Update(2100);
            Assert.That(engine.ActiveLineIndex, Is.EqualTo(-1), "a later line gets no first-line head start");
            Assert.That(engine.SongWindowOpen, Is.False, "and its window stays shut until its own boundary");
            Assert.That(engine.ProcessKey('c', 2900), Is.False, "nor may a press open it early");

            engine.Update(3000);
            Assert.That(engine.ActiveLineIndex, Is.EqualTo(1), "it opens on its own word, as it always did");
        }

        /// <summary>
        /// With the era CLEAR - a bare engine, and every replay stored before the head start - the
        /// first line opens exactly as it did before it existed: on its own activation, with a press
        /// anywhere before that refused, the head start's window included.
        /// </summary>
        [Test]
        public void WithTheEraClearTheFirstLineOpensOnItsOwnActivation()
        {
            var engine = new TypingEngine(twoLines());

            Assert.That(engine.FirstLineLeadIn, Is.False, "a bare engine is in the old era, like every other era flag");

            engine.Update(2700);
            Assert.That(engine.FirstLineTypingOpensAt(2700), Is.False, "no head start to open");
            Assert.That(engine.ProcessKey('a', 2700), Is.False, "so a press inside what would be the window is refused");
            Assert.That(engine.ProcessKey('a', 2999), Is.False, "as is one a millisecond before the line's activation");
            Assert.That(engine.ActiveLineIndex, Is.EqualTo(-1), "and neither opened the line");

            engine.Update(3000);
            Assert.That(engine.ActiveLineIndex, Is.EqualTo(0), "the clock opens it on its own activation");
            Assert.That(engine.ProcessKey('a', 3000), Is.True, "where a press lands as it always did");
        }

        /// <summary>
        /// A stored replay RE-DERIVES under the gate its CONFIG frame names. The tape is a player
        /// pressing 'a' inside the head start (2800, before the line's own activation at 3000) and 'b'
        /// once the line is open. Under bit 16 that is the run the player saw: the first press opens
        /// the line and both letters land. With the bit clear it is the old gate: the early press is
        /// refused, so 'b' arrives with the caret still on the line's first cell and is a typo.
        /// </summary>
        [TestCase(true)]
        [TestCase(false)]
        public void AStoredReplayReDerivesUnderTheGateItsFrameNames(bool leadIn)
        {
            // What a live engine starts as, so the frame's bit is the only thing deciding the gate.
            var engine = new TypingEngine(twoLines()) { FirstLineLeadIn = true, AllowWrongInput = true };

            var frames = new[]
            {
                TypeBeatReplayFrame.CreateConfigFrame(2800, allowWrongInput: true, firstLineLeadIn: leadIn),
                new TypeBeatReplayFrame(2800, 'a'),
                new TypeBeatReplayFrame(3100, 'b'),
            };

            foreach (var frame in frames)
                ReplayEngineFeed.Apply(engine, frame);

            Assert.That(engine.FirstLineLeadIn, Is.EqualTo(leadIn), "the frame's bit is what the engine re-derives under");
            Assert.That(engine.ActiveLineIndex, Is.EqualTo(0));

            if (leadIn)
            {
                Assert.That(engine.CaretIndex, Is.EqualTo(2), "the early press opened the line, and both letters landed");
                Assert.That(engine.Mistypes, Is.Zero);
            }
            else
            {
                Assert.That(engine.CaretIndex, Is.EqualTo(1), "the early press was refused, so only 'b' moved the caret");
                Assert.That(engine.Mistypes, Is.EqualTo(1), "and it landed on the 'a' cell as a typo");
            }
        }
    }
}
