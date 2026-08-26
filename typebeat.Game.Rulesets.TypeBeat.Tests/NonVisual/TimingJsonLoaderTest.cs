// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Ported verbatim from type!beat TypeBeat.Game.Tests/NonVisual/TimingJsonLoaderTest.cs.
// Adaptations on entry: namespaces; public constant renames; real-map path via StandaloneMaps.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class TimingJsonLoaderTest
    {
        private readonly List<string> tempFiles = new List<string>();

        [TearDown]
        public void TearDown()
        {
            foreach (string f in tempFiles)
            {
                try
                {
                    if (File.Exists(f)) File.Delete(f);
                }
                catch
                {
                    // best effort
                }
            }

            tempFiles.Clear();
        }

        private string writeTemp(string content)
        {
            string path = Path.Combine(Path.GetTempPath(), "tb_timing_" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, content);
            tempFiles.Add(path);
            return path;
        }

        private static string realTimingJsonPath() => StandaloneMaps.Require("Friday Pilots Club - Spectator", "timing.json");

        [Test]
        public void RealSpectatorBackingVocalsEstimatedAndConfidenceAreThreaded()
        {
            Assert.That(TimingJsonLoader.TryLoad(realTimingJsonPath(), out var lines), Is.True);

            // Bracketed backing-vocal lines are dropped entirely, never displayed, never typed.
            Assert.That(lines.Any(l => l.RawText.Contains('(')), Is.False);

            // Dropping "(Cold stare...)" dissolves the overlap at its source: "Dying for a way to
            // let go" now extends to the next REAL line ("It's his voice..." at 36060), so its
            // genuine word timing (ending 32160) survives unclamped and needs no seal grace.
            Assert.That(lines[5].RawText, Does.StartWith("Dying"));
            Assert.That(lines[5].EndTime, Is.EqualTo(36060));
            Assert.That(lines[5].SealGraceMs, Is.EqualTo(0));
            Assert.That(lines[5].Units[^1].EndTime, Is.EqualTo(32160), "tail word timing unclamped");

            // 7 kept lines carry estimated:true (2 of the original 9 were bracketed and dropped).
            Assert.That(lines.Count(l => l.Estimated), Is.EqualTo(7));

            // Word confidences thread through: line 0 word[3] "it" has aligner score 0.001.
            Assert.That(lines[0].Units[3].Text, Is.EqualTo("it"));
            Assert.That(lines[0].Units[3].Confidence, Is.EqualTo(0.001).Within(1e-9));
            Assert.That(lines[0].Units[0].Confidence, Is.GreaterThan(0.9)); // "If" scored 0.939
        }

        [Test]
        public void WholeNumberFloatVersionTokenAccepted()
        {
            // JSON doesn't distinguish 2 from 2.0; a producer emitting a float version must not
            // silently lose the whole file (and with it, all word timing).
            string path = writeTemp("{\"version\": 2.0, \"song_end_ms\": 10000, \"lines\": [ {\"text\": \"ab\", \"start_ms\": 0, \"end_ms\": 1000, \"words\": [ {\"text\": \"ab\", \"start_ms\": 0, \"end_ms\": 1000} ] } ] }");
            Assert.That(TimingJsonLoader.TryLoad(path, out var lines), Is.True);
            Assert.That(lines.Count, Is.EqualTo(1));
        }

        [Test]
        public void LoadsRealSpectatorTimingJson()
        {
            Assert.That(TimingJsonLoader.TryLoad(realTimingJsonPath(), out var lines), Is.True);

            Assert.That(lines.Count, Is.EqualTo(36)); // 40 minus 4 bracketed backing-vocal lines
            Assert.That(lines[0].StartTime, Is.EqualTo(7880));

            foreach (var l in lines)
            {
                Assert.That(l.Units.Count, Is.EqualTo(l.RawText.Split(' ').Length), l.RawText);
                Assert.That(l.Units.All(u => u.Source == TimingSource.Explicit), Is.True, l.RawText);

                // Non-decreasing and inside [StartTime, EndTime].
                double prev = l.StartTime - 1e-6;

                foreach (var u in l.Units)
                {
                    Assert.That(u.StartTime, Is.GreaterThanOrEqualTo(prev - 1e-6));
                    Assert.That(u.StartTime, Is.GreaterThanOrEqualTo(l.StartTime - 1e-6));
                    Assert.That(u.EndTime, Is.LessThanOrEqualTo(l.EndTime + 1e-6));
                    Assert.That(u.EndTime, Is.GreaterThanOrEqualTo(u.StartTime - 1e-6));
                    prev = u.EndTime;
                }

                Assert.That(l.StartTime, Is.LessThanOrEqualTo(l.SingEndTime));
                Assert.That(l.SingEndTime, Is.LessThanOrEqualTo(l.EndTime));
            }
        }

        [Test]
        public void GapLineUsesAlignerVocalEnd()
        {
            Assert.That(TimingJsonLoader.TryLoad(realTimingJsonPath(), out var lines), Is.True);

            // The gap line "I'm exactly where you want me to be" before the 81200 instrumental
            // (index 17: two bracketed backing-vocal lines precede it and are dropped).
            var gap = lines[17];
            Assert.That(gap.RawText, Is.EqualTo("I'm exactly where you want me to be"));
            Assert.That(gap.SingEndTime, Is.EqualTo(74580), "aligner vocal end drives SingEndTime");
            Assert.That(gap.EndTime, Is.EqualTo(81200), "hard seal at the next line's start");
        }

        [Test]
        public void VersionMismatchReturnsFalse()
        {
            string path = writeTemp("{\"version\":1,\"song_end_ms\":1000,\"lines\":[{\"text\":\"hi\",\"start_ms\":0,\"end_ms\":500,\"words\":[{\"text\":\"hi\",\"start_ms\":0,\"end_ms\":500}]}]}");
            Assert.That(TimingJsonLoader.TryLoad(path, out var lines), Is.False);
            Assert.That(lines, Is.Empty);
        }

        /// <summary>
        /// The contract the aligner's syllabification owes the game, as fixture data.
        ///
        /// <para>lyriclab used to fall back to raw vowel-group splitting whenever pyphen had no
        /// hyphenation pattern for a word, which cut the silent final e off "life" and "breathe"
        /// as if it were a syllable of its own, while "remember" (which pyphen does know) came out
        /// correct. This pins the shape a fixed aligner emits, and pins what it costs downstream:
        /// a spurious subdivision is a spurious TAP in the editor's timing pass and a caret that
        /// changes speed mid-word for no reason.</para>
        /// </summary>
        [Test]
        public void AlignerSyllableContractOneSegmentPerRealSyllable()
        {
            // "life" and "breathe" are one syllable each, so one segment and NO internal boundary;
            // "remember" is three, so three segments and two boundaries.
            string path = writeTemp("{\"version\":2.0,\"song_end_ms\":10000,\"lines\":[{"
                                    + "\"text\":\"life breathe remember\",\"start_ms\":0,\"end_ms\":3000,\"words\":["
                                    + "{\"text\":\"life\",\"start_ms\":0,\"end_ms\":600,"
                                    + "\"syllables\":[{\"text\":\"life\",\"start_ms\":0,\"end_ms\":600}]},"
                                    + "{\"text\":\"breathe\",\"start_ms\":600,\"end_ms\":1200,"
                                    + "\"syllables\":[{\"text\":\"breathe\",\"start_ms\":600,\"end_ms\":1200}]},"
                                    + "{\"text\":\"remember\",\"start_ms\":1200,\"end_ms\":3000,"
                                    + "\"syllables\":[{\"text\":\"re\",\"start_ms\":1200,\"end_ms\":1600},"
                                    + "{\"text\":\"mem\",\"start_ms\":1600,\"end_ms\":2200},"
                                    + "{\"text\":\"ber\",\"start_ms\":2200,\"end_ms\":3000}]}"
                                    + "]}]}");

            Assert.That(TimingJsonLoader.TryLoad(path, out var lines), Is.True);

            var units = lines[0].Units;
            Assert.That(units.Select(u => u.Text), Is.EqualTo(new[] { "life", "breathe", "remember" }));

            Assert.That(units[0].SyllableBoundaries, Is.Empty, "life is one syllable");
            Assert.That(units[1].SyllableBoundaries, Is.Empty, "breathe is one syllable");
            Assert.That(units[2].SyllableBoundaries, Is.EqualTo(new[] { 1600d, 2200d }), "remember is three");

            // What that means for the mapper: this line is FIVE taps (1 + 1 + 3). Under the old
            // vowel-group fallback it would have asked for seven, two of them for syllables that
            // do not exist.
            Assert.That(TapTimingBuilder.BuildQueue(lines), Has.Count.EqualTo(5));
            Assert.That(units.Sum(TapTimingBuilder.SyllableCount), Is.EqualTo(5));
        }

        [Test]
        public void MalformedJsonReturnsFalse()
        {
            string path = writeTemp("{ this is not valid json ][");
            Assert.That(TimingJsonLoader.TryLoad(path, out var lines), Is.False);
            Assert.That(lines, Is.Empty);

            // Missing file also returns false, never throws.
            string missing = Path.Combine(Path.GetTempPath(), "tb_missing_" + Guid.NewGuid().ToString("N") + ".json");
            Assert.That(TimingJsonLoader.TryLoad(missing, out var none), Is.False);
            Assert.That(none, Is.Empty);
        }

        [Test]
        public void TokenMismatchFallsBackToInterpolationForThatLine()
        {
            // "one two three" has 3 tokens but only 1 word => that line interpolates instead of Explicit.
            string json =
                "{\"version\":2,\"song_end_ms\":20000,\"lines\":[" +
                "{\"text\":\"one two three\",\"start_ms\":1000,\"end_ms\":4000,\"words\":[{\"text\":\"one\",\"start_ms\":1000,\"end_ms\":2000}]}," +
                "{\"text\":\"aa bb\",\"start_ms\":5000,\"end_ms\":7000,\"words\":[{\"text\":\"aa\",\"start_ms\":5000,\"end_ms\":6000},{\"text\":\"bb\",\"start_ms\":6000,\"end_ms\":7000}]}]}";
            string path = writeTemp(json);

            Assert.That(TimingJsonLoader.TryLoad(path, out var lines), Is.True);
            Assert.That(lines.Count, Is.EqualTo(2));

            // Mismatched line: interpolated units spanning [StartTime, SingEndTime].
            Assert.That(lines[0].Units.Count, Is.EqualTo(3));
            Assert.That(lines[0].Units.All(u => u.Source == TimingSource.Interpolated), Is.True);

            // Matched line: explicit word times.
            Assert.That(lines[1].Units.Count, Is.EqualTo(2));
            Assert.That(lines[1].Units.All(u => u.Source == TimingSource.Explicit), Is.True);
            Assert.That(lines[1].Units[0].StartTime, Is.EqualTo(5000));
            Assert.That(lines[1].Units[1].EndTime, Is.EqualTo(7000));
        }

        #region Pipes in the aligner's display text (backlog 202)

        /// <summary>
        /// A one-line document whose single word "please" runs 1000..2000 and whose text is
        /// <paramref name="text"/>; <paramref name="syllables"/> is spliced in as the aligner's own
        /// subdivision of that word.
        /// </summary>
        private static string pipedJson(string text, string syllables = "")
            => "{\"version\":2,\"song_end_ms\":20000,\"lines\":["
               + $"{{\"text\":\"{text}\",\"start_ms\":1000,\"end_ms\":2000,\"words\":["
               + $"{{\"text\":\"{text}\",\"start_ms\":1000,\"end_ms\":2000{syllables}}}]}}]}}";

        [Test]
        public void AMatchingPipeCountKeepsTheAlignerBoundaryTimes()
        {
            // The aligner heard the turnover at 1400; the pipe only says WHERE the characters cut,
            // so its acoustic evidence must not be thrown away for an even division.
            string json = pipedJson("ple|ase", ",\"syllables\":[{\"start_ms\":1000,\"end_ms\":1400},{\"start_ms\":1400,\"end_ms\":2000}]");

            Assert.That(TimingJsonLoader.TryLoad(writeTemp(json), out var lines), Is.True);

            var unit = lines.Single().Units.Single();
            Assert.That(lines[0].RawText, Is.EqualTo("please"), "the pipe never reaches the stored lyric");
            Assert.That(unit.Text, Is.EqualTo("please"));
            Assert.That(unit.SyllableBoundaries, Is.EqualTo(new[] { 1400d }));
            Assert.That(unit.SyllableSplits, Is.EqualTo(new[] { 3 }));
        }

        [Test]
        public void APipeOnAnUnsubdividedWordDividesItEvenly()
        {
            Assert.That(TimingJsonLoader.TryLoad(writeTemp(pipedJson("ple|ase")), out var lines), Is.True);

            var unit = lines.Single().Units.Single();
            Assert.That(unit.SyllableBoundaries, Is.EqualTo(new[] { 1500d }));
            Assert.That(unit.SyllableSplits, Is.EqualTo(new[] { 3 }));
        }

        [Test]
        public void AMismatchedPipeCountRedividesTheWord()
        {
            // The aligner reported two turnovers, the mapper asked for two segments: its boundaries
            // cannot be paired with the split, so the word is re-divided into what was asked for.
            string json = pipedJson("ple|ase", ",\"syllables\":[{\"start_ms\":1000,\"end_ms\":1300},{\"start_ms\":1300,\"end_ms\":1700},{\"start_ms\":1700,\"end_ms\":2000}]");

            Assert.That(TimingJsonLoader.TryLoad(writeTemp(json), out var lines), Is.True);

            var unit = lines.Single().Units.Single();
            Assert.That(unit.SyllableBoundaries, Is.EqualTo(new[] { 1500d }));
            Assert.That(unit.SyllableSplits, Is.EqualTo(new[] { 3 }));
        }

        [Test]
        public void AnIllegalPipePatternLeavesTheAlignerDataAlone()
        {
            string json = pipedJson("please|", ",\"syllables\":[{\"start_ms\":1000,\"end_ms\":1400},{\"start_ms\":1400,\"end_ms\":2000}]");

            Assert.That(TimingJsonLoader.TryLoad(writeTemp(json), out var lines), Is.True);

            var unit = lines.Single().Units.Single();
            Assert.That(lines[0].RawText, Is.EqualTo("please"));
            Assert.That(unit.SyllableBoundaries, Is.EqualTo(new[] { 1400d }), "the aligner's own subdivision survives a bad pipe");
            Assert.That(unit.SyllableSplits, Is.Empty);
        }

        [Test]
        public void APipeAlsoSubdividesOnTheInterpolationFallback()
        {
            // Token/word mismatch, so this line interpolates; the pipe is read there too.
            string json = "{\"version\":2,\"song_end_ms\":20000,\"lines\":["
                          + "{\"text\":\"ple|ase now\",\"start_ms\":1000,\"end_ms\":2000,\"words\":[{\"text\":\"ple|ase\",\"start_ms\":1000,\"end_ms\":2000}]}]}";

            Assert.That(TimingJsonLoader.TryLoad(writeTemp(json), out var lines), Is.True);
            Assert.That(lines[0].RawText, Is.EqualTo("please now"));

            var unit = lines[0].Units[0];
            Assert.That(unit.Source, Is.EqualTo(TimingSource.Interpolated));
            Assert.That(unit.SyllableSplits, Is.EqualTo(new[] { 3 }));
            Assert.That(unit.SyllableBoundaries[0], Is.EqualTo((unit.StartTime + unit.EndTime) / 2).Within(1e-6));
        }

        [Test]
        public void ALineOfNothingButPipesIsDropped()
        {
            string json = "{\"version\":2,\"song_end_ms\":20000,\"lines\":["
                          + "{\"text\":\"real one\",\"start_ms\":1000,\"end_ms\":2000},"
                          + "{\"text\":\"||\",\"start_ms\":3000,\"end_ms\":4000}]}";

            Assert.That(TimingJsonLoader.TryLoad(writeTemp(json), out var lines), Is.True);
            Assert.That(lines.Count, Is.EqualTo(1));
            Assert.That(lines[0].RawText, Is.EqualTo("real one"));
        }

        [Test]
        public void PipeFreeDocumentsLoadExactlyAsBefore()
        {
            // The real shipped aligner output carries no pipe anywhere, so nothing in it moved.
            Assert.That(TimingJsonLoader.TryLoad(realTimingJsonPath(), out var lines), Is.True);
            Assert.That(lines.All(l => !l.RawText.Contains(Typeability.SPLIT_MARKER)), Is.True);
            Assert.That(lines.SelectMany(l => l.Units).All(u => u.SyllableSplits.Count == 0), Is.True);
        }

        #endregion

        [Test]
        public void LastLineEndUsesTailAndSongEnd()
        {
            Assert.That(TimingJsonLoader.TryLoad(realTimingJsonPath(), out var lines), Is.True);

            // Real last line end_ms 165300; song_end_ms 183159; tail 3000.
            double expected = Math.Min(183159, 165300 + TimingJsonLoader.LAST_LINE_TAIL_MS);
            Assert.That(lines[^1].EndTime, Is.EqualTo(expected)); // 168300

            // Synthetic case where song_end_ms is the binding cap.
            string json =
                "{\"version\":2,\"song_end_ms\":9000,\"lines\":[" +
                "{\"text\":\"final\",\"start_ms\":5000,\"end_ms\":8000,\"words\":[{\"text\":\"final\",\"start_ms\":5000,\"end_ms\":8000}]}]}";
            Assert.That(TimingJsonLoader.TryLoad(writeTemp(json), out var one), Is.True);
            Assert.That(one[^1].EndTime, Is.EqualTo(9000)); // min(9000, 8000+3000)
        }
    }
}
