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

            // 36 emitted lines; the trailing [02:45.39] terminator is consumed (not a 37th line)
            // and the 4 bracketed backing-vocal lines are no lines (never typed): their words are the
            // backing vocals of the line being sung (backlog 445).
            Assert.That(lines.Count, Is.EqualTo(36));
            Assert.That(lines[0].StartTime, Is.EqualTo(7480));
            Assert.That(lines[0].RawText, Is.EqualTo("If we take it from the top now"));

            // The terminator [02:45.39] = 165390 bounds the last line's EndTime.
            Assert.That(lines[^1].RawText, Is.EqualTo("Yeah I'm exactly where he wants me to be"));
            Assert.That(lines[^1].EndTime, Is.EqualTo(165390));
        }

        [Test]
        public void BackingVocalLinesAreNoLinesAndAreSpannedOver()
        {
            var lines = LrcParser.Parse("[00:01.00] real one\n[00:02.00] (backing echo)\n[00:04.00] real two\n[00:06.00]\n");

            Assert.That(lines.Count, Is.EqualTo(2));
            Assert.That(lines[0].RawText, Is.EqualTo("real one"));
            Assert.That(lines[0].EndTime, Is.EqualTo(4000)); // extends over the backing line
            Assert.That(lines[1].EndTime, Is.EqualTo(6000)); // trailing terminator still honoured

            // Its words are the first line's backing vocals (backlog 445).
            Assert.That(lines[0].BackingVocals.Select(w => w.Text), Is.EqualTo(new[] { "backing", "echo" }));
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
            // A curly apostrophe folds to the ASCII one and is KEPT: the stored line is the
            // author's form. What the player types is the derived default stream.
            var curly = LrcParser.Parse("[00:01.00] don’t stop\n");
            Assert.That(curly[0].RawText, Is.EqualTo("don't stop"));
            Assert.That(Typeability.ToDefaultStream(curly[0].RawText), Is.EqualTo("dont stop"));

            // Every supported mark survives normalization; the default stream drops all of them
            // except the hyphen, which becomes a word break.
            var punct = LrcParser.Parse("[00:01.00] It's his half-cut voice, through my lips!\n");
            Assert.That(punct[0].RawText, Is.EqualTo("It's his half-cut voice, through my lips!"));
            Assert.That(Typeability.ToDefaultStream(punct[0].RawText), Is.EqualTo("its his half cut voice through my lips"));

            // Unsupported chars still vanish outright, before the derivation ever sees them.
            // ('*' was one of these until backlog 202 made it a supported mark, and '~' until
            // backlog 255 did, so both now belong on the line above instead.)
            var unsupported = LrcParser.Parse("[00:01.00] a`b #c\n");
            Assert.That(unsupported[0].RawText, Is.EqualTo("ab c"));

            // The two marks backlog 255 added survive an import like any other supported mark; the
            // backing-vocal strip an import still runs is about BRACKETS, not about them.
            var newMarks = LrcParser.Parse("[00:01.00] slow_down oh~oh\n");
            Assert.That(newMarks[0].RawText, Is.EqualTo("slow_down oh~oh"));
            Assert.That(Typeability.ToDefaultStream(newMarks[0].RawText), Is.EqualTo("slowdown ohoh"));

            // The real file carries nothing but typeable chars and supported marks.
            var real = LrcParser.Parse(spectator_lyrics);
            Assert.That(real.All(l => l.RawText.All(c => Typeability.IsCell(c) || Typeability.IsPunctuation(c))), Is.True);

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

        #region Authoring marks in the LRC (backlog 202)

        [Test]
        public void PipesAuthorSyllableSubdivisions()
        {
            // "ple|ase": one pipe, so two segments, and nothing in an LRC says where the vocal
            // actually turns over, so the word's own span is divided EQUALLY.
            var lines = LrcParser.Parse("[00:14.90]ple|ase\n[00:16.00]\n");

            Assert.That(lines.Count, Is.EqualTo(1));
            Assert.That(lines[0].RawText, Is.EqualTo("please"), "the pipe is an authoring mark, never lyric");

            var unit = lines[0].Units.Single();
            Assert.That(unit.Text, Is.EqualTo("please"));
            Assert.That(unit.StartTime, Is.EqualTo(14900).Within(1e-6));
            Assert.That(unit.EndTime, Is.EqualTo(16000).Within(1e-6));
            Assert.That(unit.SyllableSplits, Is.EqualTo(new[] { 3 }));
            Assert.That(unit.SyllableBoundaries.Count, Is.EqualTo(1));
            Assert.That(unit.SyllableBoundaries[0], Is.EqualTo(15450).Within(1e-6));
        }

        [Test]
        public void PipesDivideOnlyTheirOwnWord()
        {
            // Two pipes on the second word: three equal segments of THAT word, the first word
            // untouched. Weights are unchanged too, because a pipe is not a typeable char.
            var lines = LrcParser.Parse("[00:01.00] a b|c|d\n[00:05.00]\n");
            var line = lines.Single();

            Assert.That(line.RawText, Is.EqualTo("a bcd"));
            Assert.That(line.Units[0].SyllableBoundaries, Is.Empty);

            var second = line.Units[1];
            Assert.That(second.Text, Is.EqualTo("bcd"));
            Assert.That(second.SyllableSplits, Is.EqualTo(new[] { 1, 2 }));

            double span = second.EndTime - second.StartTime;
            Assert.That(second.SyllableBoundaries[0], Is.EqualTo(second.StartTime + span / 3).Within(1e-6));
            Assert.That(second.SyllableBoundaries[1], Is.EqualTo(second.StartTime + 2 * span / 3).Within(1e-6));

            // The weighting is exactly what the same line without pipes would have produced.
            var plain = LrcParser.Parse("[00:01.00] a bcd\n[00:05.00]\n").Single();
            Assert.That(second.StartTime, Is.EqualTo(plain.Units[1].StartTime).Within(1e-9));
            Assert.That(second.EndTime, Is.EqualTo(plain.Units[1].EndTime).Within(1e-9));
        }

        [TestCase("|please", TestName = "IllegalPipe_LeadingEmptiesFirstSegment")]
        [TestCase("please|", TestName = "IllegalPipe_TrailingEmptiesLastSegment")]
        [TestCase("ple||ase", TestName = "IllegalPipe_DoubledEmptiesTheMiddle")]
        public void AnIllegalPipePatternAuthorsNothing(string word)
        {
            var line = LrcParser.Parse($"[00:01.00]{word}\n[00:05.00]\n").Single();

            Assert.That(line.RawText, Is.EqualTo("please"));
            Assert.That(line.Units.Single().SyllableBoundaries, Is.Empty, "a pipe that would empty a segment is forgiven, not obeyed");
            Assert.That(line.Units.Single().SyllableSplits, Is.Empty);
        }

        [Test]
        public void ALineOfNothingButPipesStaysABoundaryMarker()
        {
            // It must not become an empty line; it is exactly the junk-only case the parser has
            // always folded back into a bare terminator.
            var lines = LrcParser.Parse("[00:01.00] real one\n[00:03.00] ||\n");

            Assert.That(lines.Count, Is.EqualTo(1));
            Assert.That(lines[0].RawText, Is.EqualTo("real one"));
            Assert.That(lines[0].EndTime, Is.EqualTo(3000), "the pipe line still terminates the one before it");
        }

        [Test]
        public void AmpersandsSurviveAsFreestyleMarkers()
        {
            var line = LrcParser.Parse("[00:01.00] me & you\n[00:05.00]\n").Single();

            Assert.That(line.RawText, Is.EqualTo("me & you"));
            Assert.That(line.Units.Count, Is.EqualTo(3));
            Assert.That(line.Units[1].Text, Is.EqualTo("&"));

            // The marker occupies a CELL, so it counts for the density cap and the weights.
            Assert.That(Typeability.TypeableCount(line.RawText), Is.EqualTo(8));
        }

        [Test]
        public void AMarkerOnlyLineBecomesItsOwnLineInTheGap()
        {
            // An ad-lib or instrumental stretch the mapper marked as freestyle: before backlog 202
            // this line normalized to empty and vanished, leaving the previous line spanning the
            // whole gap. It now sits exactly where the LRC put it.
            var lines = LrcParser.Parse("[00:01.00] real one\n[00:03.00] &&&\n[00:09.00] real two\n[00:11.00]\n");

            Assert.That(lines.Count, Is.EqualTo(3));
            Assert.That(lines[1].RawText, Is.EqualTo("&&&"));
            Assert.That(lines[1].StartTime, Is.EqualTo(3000));
            Assert.That(lines[1].EndTime, Is.EqualTo(9000));

            Assert.That(lines[0].EndTime, Is.EqualTo(3000), "the previous line no longer spans the gap");
        }

        [Test]
        public void MarkFreeLyricsParseExactlyAsBefore()
        {
            // The whole blast radius is gated on a line actually carrying a mark, and the shipped
            // file carries neither, so nothing about it moved.
            var lines = LrcParser.Parse(spectator_lyrics);

            Assert.That(lines.Count, Is.EqualTo(36));
            Assert.That(lines.All(l => l.Units.All(u => u.SyllableBoundaries.Count == 0 && u.SyllableSplits.Count == 0)), Is.True);
            Assert.That(lines.All(l => !l.RawText.Contains(Typeability.SPLIT_MARKER) && !l.RawText.Contains(Typeability.FREESTYLE_MARKER)), Is.True);
        }

        #endregion

        #region Plain LRC is byte-identical (backlog 356)

        // Every plain-path feature in one file: offset, duplicated tags, 2- and 3-digit fractions,
        // backing vocals, pipes, ampersands, a '<' that is not a stamp, a romanised line and a
        // trailing terminator.
        private const string plain_features_lyrics =
            "[ti:Pin]\n[offset:+120]\n"
            + "[00:01.00][00:20.50] chorus line again\n"
            + "[00:03.250] I <3 you (oh yeah) so much\n"
            + "[00:05.00] beau|ti|ful & & day\n"
            + "[00:07.10] &&&\n"
            + "[00:09.00] さくら さくら\n"
            + "[00:11.00] (backing only)\n"
            + "[00:13.00] last one, isn't it?\n"
            + "[00:16.00]\n";

        /// <summary>Everything a parsed line carries, at full double precision.</summary>
        private static string fingerprint(System.Collections.Generic.IReadOnlyList<LyricLine> lines)
        {
            var sb = new System.Text.StringBuilder();
            var inv = System.Globalization.CultureInfo.InvariantCulture;

            foreach (var l in lines)
            {
                sb.Append(inv, $"L|{l.RawText}|{l.StartTime:R}|{l.EndTime:R}|{l.SingEndTime:R}|{l.Original}\n");

                foreach (var u in l.Units)
                {
                    sb.Append(inv, $"U|{u.Text}|{u.StartTime:R}|{u.EndTime:R}|{u.Source}|{u.Confidence:R}|{string.Join(",", u.SyllableBoundaries.Select(b => b.ToString("R", inv)))}"
                                   + $"|{string.Join(",", u.SyllableSplits)}|{u.Pauses.Count}|{u.Original}\n");
                }

                foreach (var p in l.UnromanisedWords)
                    sb.Append(inv, $"P|{p.Position}|{p.Original}|{p.StartTime:R}|{p.EndTime:R}\n");
            }

            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString())));
        }

        /// <summary>
        /// A plain LRC parses EXACTLY as it did before inline word stamps were recognised: these
        /// hashes were taken from the parser as it stood before backlog 356.
        /// </summary>
        [TestCase(false, "0902C1FE2A83A1C7CEF5ED7BE495989F9C0EA4987F8BC8A83941008B71ECD3DA")]
        [TestCase(true, "3D8A4F3242A8E6C59F22663F85FA964158B0B1819465CD772E5B239E925486B1")]
        public void PlainLrcParsesByteIdentically(bool features, string expected)
        {
            string lyrics = features ? plain_features_lyrics : spectator_lyrics;

            Assert.That(LrcParser.HasWordStamps(lyrics), Is.False);
            Assert.That(fingerprint(LrcParser.Parse(lyrics)), Is.EqualTo(expected));
            Assert.That(fingerprint(LrcParser.Parse(lyrics, null, out int clamped)), Is.EqualTo(expected));
            Assert.That(clamped, Is.Zero);
        }

        #endregion

        #region Enhanced LRC: inline word stamps (backlog 356)

        private const string rick_line = "[00:12.00]<00:12.00>Never <00:12.40>gonna <00:12.80>give <00:13.10>you <00:13.50>up";

        [Test]
        public void EveryStampedWordIsExplicitAndTheLastRunsToTheLineEnd()
        {
            var lines = LrcParser.Parse(rick_line, null, out int clamped);

            Assert.That(clamped, Is.Zero);
            Assert.That(lines.Count, Is.EqualTo(1));

            var line = lines[0];
            Assert.That(line.RawText, Is.EqualTo("Never gonna give you up"));
            Assert.That(line.StartTime, Is.EqualTo(12000));

            // Last line, no terminator: 12000 + min(5000, 350 * 19 typeable) = 17000.
            Assert.That(line.EndTime, Is.EqualTo(17000));
            Assert.That(line.SingEndTime, Is.EqualTo(17000));

            Assert.That(line.Units.Select(u => u.Text), Is.EqualTo(new[] { "Never", "gonna", "give", "you", "up" }));
            Assert.That(line.Units.Select(u => u.StartTime), Is.EqualTo(new[] { 12000.0, 12400, 12800, 13100, 13500 }));
            Assert.That(line.Units.Select(u => u.EndTime), Is.EqualTo(new[] { 12400.0, 12800, 13100, 13500, 17000 }));
            Assert.That(line.Units.All(u => u.Source == TimingSource.Explicit && u.Confidence == 1), Is.True);
        }

        [Test]
        public void TwoAndThreeDigitFractionsAndAClosingStamp()
        {
            var line = LrcParser.Parse("[00:01.00]<00:01.00>one <00:01.250>two <00:01.5>three<00:02.000>").Single();

            Assert.That(line.Units.Select(u => u.StartTime), Is.EqualTo(new[] { 1000.0, 1250, 1500 }));
            Assert.That(line.Units.Select(u => u.EndTime), Is.EqualTo(new[] { 1250.0, 1500, 2000 }), "the closing stamp bounds the last word");
            Assert.That(line.SingEndTime, Is.EqualTo(2000));

            Assert.That(LrcParser.TryParseTimestamp("<00:01.250>", out double angled), Is.True);
            Assert.That(angled, Is.EqualTo(1250));
            Assert.That(LrcParser.TryParseTimestamp("[00:01.250]", out double squared), Is.True);
            Assert.That(squared, Is.EqualTo(1250));
        }

        [Test]
        public void AClosingStampBoundsTheLastWordButNotTheLine()
        {
            var line = LrcParser.Parse(rick_line + "<00:14.00>").Single();

            Assert.That(line.Units[^1].StartTime, Is.EqualTo(13500));
            Assert.That(line.Units[^1].EndTime, Is.EqualTo(14000));
            Assert.That(line.SingEndTime, Is.EqualTo(14000));
            Assert.That(line.EndTime, Is.EqualTo(17000));
        }

        [Test]
        public void OffsetShiftsInlineStampsToo()
        {
            var lines = LrcParser.Parse("[offset:+500]\n[00:10.00]<00:10.00>hello <00:10.60>world\n[00:12.00]bye\n");

            Assert.That(lines[0].StartTime, Is.EqualTo(9500));
            Assert.That(lines[0].Units.Select(u => u.StartTime), Is.EqualTo(new[] { 9500.0, 10100 }));

            // Not the last line: the last word runs to the line's (density-capped) sung end, 11500.
            Assert.That(lines[0].Units[^1].EndTime, Is.EqualTo(11500));
            Assert.That(lines[1].StartTime, Is.EqualTo(11500));
            Assert.That(lines[1].Units.Single().Source, Is.EqualTo(TimingSource.Interpolated), "an unstamped line parses as before");
        }

        [Test]
        public void APartiallyStampedLineInterpolatesInsideTheStampedSpans()
        {
            var line = LrcParser.Parse("[00:20.00]<00:20.00>Never gonna <00:21.00>give you up<00:23.00>").Single();

            Assert.That(line.Units.Select(u => u.Text), Is.EqualTo(new[] { "Never", "gonna", "give", "you", "up" }));

            // [20000, 21000] by weight 6:6, then [21000, 23000] by weight 5:4:3.
            double[] starts = { 20000, 20500, 21000, 21000 + 2000 * 5 / 12.0, 22500 };
            double[] ends = { 20500, 21000, 21000 + 2000 * 5 / 12.0, 22500, 23000 };

            for (int i = 0; i < 5; i++)
            {
                Assert.That(line.Units[i].StartTime, Is.EqualTo(starts[i]).Within(1e-6), $"start {i}");
                Assert.That(line.Units[i].EndTime, Is.EqualTo(ends[i]).Within(1e-6), $"end {i}");
            }

            Assert.That(line.Units.Select(u => u.Source), Is.EqualTo(new[]
            {
                TimingSource.Explicit, TimingSource.Interpolated, TimingSource.Explicit, TimingSource.Interpolated, TimingSource.Interpolated
            }));

            // Words before the first stamp share [the line tag, that stamp].
            var lead = LrcParser.Parse("[00:30.00]oh <00:31.00>yes<00:31.50>").Single();
            Assert.That(lead.Units.Select(u => u.StartTime), Is.EqualTo(new[] { 30000.0, 31000 }));
            Assert.That(lead.Units[0].EndTime, Is.EqualTo(31000));
            Assert.That(lead.Units.Select(u => u.Source), Is.EqualTo(new[] { TimingSource.Interpolated, TimingSource.Explicit }));
        }

        [Test]
        public void OutOfOrderStampsAreClampedAndCounted()
        {
            var lines = LrcParser.Parse("[00:40.00]<00:39.50>zero <00:42.00>one <00:41.00>two <00:50.00>three\n[00:45.00]next\n", null, out int clamped);

            // 39500 before the line (up to 40000), 41000 behind 42000 (up to 42000), 50000 past the
            // line's end at the next line's start (down to 45000).
            Assert.That(clamped, Is.EqualTo(3));
            Assert.That(lines[0].Units.Select(u => u.StartTime), Is.EqualTo(new[] { 40000.0, 42000, 42000, 45000 }));
            Assert.That(lines[0].Units.Zip(lines[0].Units.Skip(1)).All(p => p.First.EndTime <= p.Second.StartTime + 1e-9), Is.True);
            Assert.That(lines[0].SingEndTime, Is.LessThanOrEqualTo(lines[0].EndTime));
        }

        [Test]
        public void DuplicatedLeadingTagsShiftTheStampsWithTheLine()
        {
            var lines = LrcParser.Parse("[00:10.00][00:30.00]<00:10.00>la <00:10.50>da\n[00:40.00]end\n", null, out int clamped);

            Assert.That(clamped, Is.Zero);
            Assert.That(lines.Where(l => l.RawText == "la da").Select(l => l.Units[1].StartTime), Is.EqualTo(new[] { 10500.0, 30500 }));
        }

        [Test]
        public void StampsInsideAWordOrABackingVocalAreNotWordBoundaries()
        {
            var line = LrcParser.Parse("[00:01.00]<00:01.00>Ne<00:01.20>ver (<00:01.50>oh) <00:02.00>mind<00:02.50>").Single();

            Assert.That(line.RawText, Is.EqualTo("Never mind"));
            Assert.That(line.Units.Select(u => u.StartTime), Is.EqualTo(new[] { 1000.0, 2000 }));
            Assert.That(line.Units.Select(u => u.EndTime), Is.EqualTo(new[] { 2000.0, 2500 }));
        }

        [Test]
        public void HasWordStampsDetection()
        {
            Assert.That(LrcParser.HasWordStamps(spectator_lyrics), Is.False);
            Assert.That(LrcParser.HasWordStamps(plain_features_lyrics), Is.False);
            Assert.That(LrcParser.HasWordStamps(""), Is.False);
            Assert.That(LrcParser.HasWordStamps("[ti:Song <00:01.00>]\nplain <00:01.00> text\n"), Is.False, "neither a metadata tag nor an untimed line counts");

            Assert.That(LrcParser.HasWordStamps(rick_line), Is.True);
            Assert.That(LrcParser.HasWordStamps("﻿[00:01.00]plain\n[00:02.00] <00:02.00>stamped\n"), Is.True);
            Assert.That(LrcParser.HasWordStamps("<00:02.00>no line tag <00:02.50>at all\n"), Is.True);

            // A line opening with a word stamp and no line tag is placed by that stamp.
            var line = LrcParser.Parse("<00:02.00>no <00:02.50>tag").Single();
            Assert.That(line.StartTime, Is.EqualTo(2000));
            Assert.That(line.Units.Select(u => u.StartTime), Is.EqualTo(new[] { 2000.0, 2500 }));
        }

        [Test]
        public void AJapaneseEnhancedLrcGetsAnOriginalPerWord()
        {
            var lines = LrcParser.Parse("[00:01.00]<00:01.00>さくら <00:01.60>さくら\n[00:08.00]<00:08.00>さくら <00:08.50>鿿 <00:09.00>さくら<00:09.50>\n",
                "japanese", out _);

            Assert.That(lines[0].RawText, Is.EqualTo("sakura sakura"));
            Assert.That(lines[0].Original, Is.EqualTo("さくら さくら"));
            Assert.That(lines[0].Units.Select(u => u.Original), Is.EqualTo(new[] { "さくら", "さくら" }));
            Assert.That(lines[0].Units.Select(u => u.StartTime), Is.EqualTo(new[] { 1000.0, 1600 }));
            Assert.That(lines[0].Units.All(u => u.Source == TimingSource.Explicit), Is.True);

            // A word the romaniser cannot spell keeps its own stamped span, at its place in the line.
            Assert.That(lines[1].RawText, Is.EqualTo("sakura sakura"));
            var pending = lines[1].UnromanisedWords.Single();
            Assert.That(pending.Position, Is.EqualTo(1));
            Assert.That(pending.Original, Is.EqualTo("鿿"));
            Assert.That(pending.StartTime, Is.EqualTo(8500));
            Assert.That(pending.EndTime, Is.EqualTo(9000));
            Assert.That(lines[1].Units.Select(u => u.StartTime), Is.EqualTo(new[] { 8000.0, 9000 }));
        }

        #endregion

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
