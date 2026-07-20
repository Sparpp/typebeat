// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Ported verbatim from type!beat TypeBeat.Game.Tests/NonVisual/LrcParserTest.cs.
// Adaptations on entry: namespaces; public constant renames (fork ALL_UPPER style).

using System;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class LrcParserTest
    {
        // The real shipped Spectator lyrics.txt: 40 timed lines + a trailing bare terminator [02:45.39].
        private const string spectator_lyrics =
            """
            [00:07.48] If we take it from the top now
            [00:11.74] Just how you left me before
            [00:16.78] Found me breaking out of blackout
            [00:20.12] With one hand on the door
            [00:25.75] I feel my body calling
            [00:29.46] Dying for a way to let go
            [00:32.21] (Cold stare, are you tasting the glare)
            [00:35.85] It's his voice, through my lips
            [00:38.05] Yeah I'm beggin' him to leave me alone
            [00:41.53] (No shot, boy I'm all that you got)
            [00:45.89] Spectator
            [00:48.14] Mirror me
            [00:50.40] A best seller
            [00:51.82] First edition of a lip read
            [00:55.04] Cold killer
            [00:57.09] A big tease
            [00:59.55] I'm on the edge
            [01:00.94] I'm exactly where you want me
            [01:05.84] Exactly where you want me
            [01:10.08] I'm exactly where you want me to be
            [01:20.55] Alone in your perception
            [01:25.45] A personal hell
            [01:29.77] I drown in self deception
            [01:32.72] Seems like I'm serving you well
            [01:40.42] Spectator
            [01:42.91] Mirror me
            [01:45.24] A best seller
            [01:46.66] First edition of a lip read
            [01:50.02] Cold killer
            [01:52.22] A big tease
            [01:54.23] I'm on the edge
            [01:55.42] I'm exactly where you want me
            [02:00.68] Exactly where you want me
            [02:04.52] I'm exactly where you want me to be
            [02:13.93] Another critic in the front seat
            [02:23.24] I'm exactly where you want me to be
            [02:26.89] (Spectator)
            [02:32.56] So distraught by what I don't see
            [02:35.39] (Spectator)
            [02:41.94] Yeah I'm exactly where he wants me to be
            [02:45.39]
            """;

        [Test]
        public void ParsesSpectatorFile()
        {
            var lines = LrcParser.Parse(spectator_lyrics);

            // 36 emitted lines — the trailing [02:45.39] terminator is consumed (not a 37th line)
            // and the 4 bracketed backing-vocal lines are dropped (never typed).
            Assert.That(lines.Count, Is.EqualTo(36));
            Assert.That(lines[0].StartTime, Is.EqualTo(7480));
            Assert.That(lines[0].RawText, Is.EqualTo("If we take it from the top now"));

            // The terminator [02:45.39] = 165390 bounds the last line's EndTime.
            Assert.That(lines[^1].RawText, Is.EqualTo("Yeah Im exactly where he wants me to be"));
            Assert.That(lines[^1].EndTime, Is.EqualTo(165390));
        }

        [Test]
        public void BackingVocalLinesAreDroppedAndSpannedOver()
        {
            var lines = LrcParser.Parse("[00:01.00] real one\n[00:02.00] (backing echo)\n[00:04.00] real two\n[00:06.00]\n");

            Assert.That(lines.Count, Is.EqualTo(2));
            Assert.That(lines[0].RawText, Is.EqualTo("real one"));
            Assert.That(lines[0].EndTime, Is.EqualTo(4000)); // extends over the dropped backing line
            Assert.That(lines[1].EndTime, Is.EqualTo(6000)); // trailing terminator still honoured
        }

        [Test]
        public void InlineBracketedSpansAreStripped()
        {
            var lines = LrcParser.Parse("[00:01.00] hello (yeah) world\n[00:03.00]\n");

            Assert.That(lines.Count, Is.EqualTo(1));
            Assert.That(lines[0].RawText, Is.EqualTo("hello world"));
        }

        [Test]
        public void EndTimeEqualsNextStart()
        {
            var lines = LrcParser.Parse(spectator_lyrics);
            for (int i = 0; i < lines.Count - 1; i++)
                Assert.That(lines[i].EndTime, Is.EqualTo(lines[i + 1].StartTime), $"line {i}");

            // Invariants across every line.
            foreach (var l in lines)
            {
                Assert.That(l.StartTime, Is.LessThanOrEqualTo(l.SingEndTime));
                Assert.That(l.SingEndTime, Is.LessThanOrEqualTo(l.EndTime));
            }
        }

        [Test]
        public void DensityCapAppliesOnGapLine()
        {
            var lines = LrcParser.Parse(spectator_lyrics);

            // The 01:10.08 line (= 70080) precedes the long instrumental gap to 01:20.55 (= 80550).
            var gap = lines.Single(l => l.StartTime == 70080);
            Assert.That(gap.EndTime, Is.EqualTo(80550), "hard seal stays at the next line's start");

            int typeable = Typeability.TypeableCount(gap.RawText);
            double expectedSingEnd = 70080 + Math.Min(gap.EndTime - gap.StartTime, LrcParser.MAX_MS_PER_TYPEABLE_CHAR * typeable);
            Assert.That(gap.SingEndTime, Is.EqualTo(expectedSingEnd).Within(1e-6));
            Assert.That(gap.SingEndTime, Is.LessThanOrEqualTo(gap.EndTime));
        }

        [Test]
        public void TimestampVariantsAndOffset()
        {
            // mm:ss.xx and mm:ss.xxx both supported.
            Assert.That(LrcParser.TryParseTimestamp("00:07.48", out double a), Is.True);
            Assert.That(a, Is.EqualTo(7480));
            Assert.That(LrcParser.TryParseTimestamp("01:02.395", out double b), Is.True);
            Assert.That(b, Is.EqualTo(62395));
            Assert.That(LrcParser.TryParseTimestamp("garbage", out _), Is.False);

            // Multiple leading tags duplicate the line at each time.
            var multi = LrcParser.Parse("[00:01.00][00:05.00] repeat\n[00:09.00] end\n");
            Assert.That(multi.Count(l => l.RawText == "repeat"), Is.EqualTo(2));
            Assert.That(multi.Select(l => l.StartTime), Does.Contain(1000).And.Contain(5000));

            // [offset:] applies to ALL times; positive shifts earlier (subtract).
            var offset = LrcParser.Parse("[offset:+500]\n[00:10.00] hello world\n[00:12.00] bye\n");
            Assert.That(offset[0].StartTime, Is.EqualTo(9500));
            Assert.That(offset[1].StartTime, Is.EqualTo(11500));
            Assert.That(offset[0].EndTime, Is.EqualTo(offset[1].StartTime));
        }

        [Test]
        public void HeadersAndBlanksIgnored()
        {
            var lines = LrcParser.Parse(
                "[ti:Spectator]\n[ar:Friday Pilots Club]\n[Lyrics]\n\nplain untimed text\n[00:01.00] real one\n[00:03.00] real two\n");

            Assert.That(lines.Count, Is.EqualTo(2));
            Assert.That(lines[0].RawText, Is.EqualTo("real one"));
            Assert.That(lines[1].RawText, Is.EqualTo("real two"));
        }

        [Test]
        public void NormalizationAndWeights()
        {
            // Apostrophes (curly or ASCII) can't be typed, so they are removed outright.
            var curly = LrcParser.Parse("[00:01.00] don’t stop\n");
            Assert.That(curly[0].RawText, Is.EqualTo("dont stop"));

            // Commas and every other untypeable char vanish from the game text too.
            var punct = LrcParser.Parse("[00:01.00] It's his voice, through my lips!\n");
            Assert.That(punct[0].RawText, Is.EqualTo("Its his voice through my lips"));

            // The real file carries no untypeable chars at all after normalization.
            var real = LrcParser.Parse(spectator_lyrics);
            Assert.That(real.All(l => Typeability.TypeableCount(l.RawText) == l.RawText.Length), Is.True);

            // Token weight = typeableCount + 1: "a"(2) vs "bcd"(4) => bcd span is twice a's span.
            var weighted = LrcParser.Parse("[00:01.00] a bcd\n");
            var line = weighted[0];
            Assert.That(line.Units.Count, Is.EqualTo(2));
            double spanA = line.Units[0].EndTime - line.Units[0].StartTime;
            double spanBcd = line.Units[1].EndTime - line.Units[1].StartTime;
            Assert.That(spanBcd / spanA, Is.EqualTo(2.0).Within(1e-6));

            // Unit times monotonic and covering [StartTime, SingEndTime].
            foreach (var l in real)
            {
                Assert.That(l.Units.Count, Is.EqualTo(l.RawText.Split(' ').Length), l.RawText);
                Assert.That(l.Units[0].StartTime, Is.EqualTo(l.StartTime).Within(1e-6));
                Assert.That(l.Units[^1].EndTime, Is.EqualTo(l.SingEndTime).Within(1e-6));

                double prev = double.NegativeInfinity;

                foreach (var u in l.Units)
                {
                    Assert.That(u.StartTime, Is.GreaterThanOrEqualTo(prev - 1e-6));
                    Assert.That(u.EndTime, Is.GreaterThanOrEqualTo(u.StartTime - 1e-6));
                    Assert.That(u.Source, Is.EqualTo(TimingSource.Interpolated));
                    prev = u.EndTime;
                }
            }
        }

        [Test]
        public void LastLineWithoutTerminatorGetsDefaultDuration()
        {
            var lines = LrcParser.Parse("[00:01.00] only line without terminator\n");
            Assert.That(lines.Count, Is.EqualTo(1));

            int typeable = Typeability.TypeableCount(lines[0].RawText);
            double expected = 1000 + Math.Min(LrcParser.DEFAULT_LAST_LINE_DURATION_MS, LrcParser.MAX_MS_PER_TYPEABLE_CHAR * typeable);
            Assert.That(lines[0].EndTime, Is.EqualTo(expected).Within(1e-6));
        }
    }
}
