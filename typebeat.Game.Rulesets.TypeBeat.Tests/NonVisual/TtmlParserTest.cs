// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Import;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The Apple Music TTML converter: the &lt;p&gt;/&lt;span&gt; reading, the three time formats,
    /// the backing-vocal drop, and the round trip through the synthesized timing.json into the
    /// loader the editor and the decoder both go through.
    /// </summary>
    [TestFixture]
    public class TtmlParserTest
    {
        /// <summary>An "Abracadabra"-shaped line: five spans with no space between them is ONE word.</summary>
        private const string syllable_timed =
            """
            <tt xmlns="http://www.w3.org/ns/ttml" xmlns:itunes="http://music.apple.com/lyric-ttml-internal"
                xmlns:ttm="http://www.w3.org/ns/ttml#metadata" itunes:timing="Word" xml:lang="en">
              <head><metadata>
                <ttm:agent type="person" xml:id="v1"/>
                <iTunesMetadata xmlns="http://music.apple.com/lyric-ttml-internal" leadingSilence="0.140">
                  <audio lyricOffset="0.046" role="spatial"/>
                </iTunesMetadata>
              </metadata></head>
              <body dur="3:43.398">
                <div begin="0.155" end="7.878" itunes:songPart="Intro">
                  <p begin="0.155" end="3.899" itunes:key="L1" ttm:agent="v1"><span begin="0.155" end="0.587">Ab</span><span begin="0.587" end="0.720">ra</span><span begin="0.720" end="0.960">ca</span><span begin="0.960" end="1.176">dab</span><span begin="1.176" end="1.414">ra,</span> <span begin="1.414" end="1.688">ab</span><span begin="1.688" end="1.926">ra</span><span begin="1.926" end="2.144">ca</span><span begin="2.144" end="2.987">dab</span><span begin="2.987" end="3.899">ra</span></p>
                  <p begin="3.985" end="7.878" itunes:key="L2" ttm:agent="v1"><span begin="3.985" end="4.385">Ab</span><span begin="4.385" end="4.518">ra</span></p>
                </div>
              </body>
            </tt>
            """;

        /// <summary>A "Shape of You"-shaped document: line timing, no spans, and mm:ss stamps.</summary>
        private const string line_timed =
            """
            <tt xmlns="http://www.w3.org/ns/ttml" xmlns:itunes="http://music.apple.com/lyric-ttml-internal"
                xmlns:ttm="http://www.w3.org/ns/ttml#metadata" itunes:timing="Line" xml:lang="en">
              <head><metadata><audio lyricOffset="0.046" role="spatial"/></metadata></head>
              <body dur="3:53.713">
                <div begin="9.731" end="29.942" itunes:songPart="Verse">
                  <p begin="9.731" end="12.105" itunes:key="L1" ttm:agent="v1">The club isn't the best place to find a lover</p>
                  <p begin="12.105" end="14.063" itunes:key="L2" ttm:agent="v1">So the bar is where I go</p>
                </div>
                <div begin="1:10.131" end="1:30.208" itunes:songPart="Chorus">
                  <p begin="1:10.131" end="1:12.610" itunes:key="L3" ttm:agent="v1">Oh I, oh I, oh I, oh I</p>
                </div>
              </body>
            </tt>
            """;

        #region Parsing

        [Test]
        public void AdjacentSpansAreOneWordAndSpacesSeparateWords()
        {
            Assert.That(TtmlParser.TryParse(syllable_timed, out IReadOnlyList<LyricLine> lines, out _), Is.True);
            Assert.That(lines.Count, Is.EqualTo(2));

            LyricLine first = lines[0];

            // Five spans, one word; the literal space between the two runs is the word boundary.
            Assert.That(first.RawText, Is.EqualTo("Abracadabra, abracadabra"));
            Assert.That(first.StartTime, Is.EqualTo(155).Within(0.001));
            Assert.That(first.SingEndTime, Is.EqualTo(3899).Within(0.001));
            Assert.That(first.Units.Count, Is.EqualTo(2), "one unit per whitespace token of the stored lyric");

            var word = first.Units[0];

            Assert.That(word.Text, Is.EqualTo("Abracadabra,"));
            Assert.That(word.Source, Is.EqualTo(TimingSource.Explicit));
            Assert.That(word.StartTime, Is.EqualTo(155).Within(0.001));
            Assert.That(word.EndTime, Is.EqualTo(1414).Within(0.001));

            // Four internal syllable starts, cut at the character each syllable begins.
            Assert.That(word.SyllableBoundaries, Is.EqualTo(new[] { 587d, 720d, 960d, 1176d }));
            Assert.That(word.SyllableSplits, Is.EqualTo(new[] { 2, 4, 6, 9 }));
        }

        [Test]
        public void EverySpanLinesUpWithItsUnitText()
        {
            // The split is only meaningful if it is a real cut of the word it is attached to, so the
            // segments the engine will derive from it are pinned to the source syllables.
            TtmlParser.TryParse(syllable_timed, out IReadOnlyList<LyricLine> lines, out _);

            TimedUnit word = lines[0].Units[0];
            IReadOnlyList<string> segments = Gameplay.SyllableSegments.SegmentTexts(word.Text, word.SyllableSplits);

            Assert.That(segments, Is.EqualTo(new[] { "Ab", "ra", "ca", "dab", "ra," }));
        }

        [Test]
        public void BackingVocalsAreDroppedWithTheirSubtree()
        {
            const string withBackingVocal =
                """
                <tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata"><body><div>
                  <p begin="2:19.958" end="2:23.336"><span begin="2:19.958" end="2:20.489">You</span> <span begin="2:20.489" end="2:20.806">made</span> <span begin="2:20.806" end="2:21.838">heart</span> <span begin="2:21.838" end="2:22.686">ache</span> <span ttm:role="x-bg"><span begin="2:20.363" end="2:20.763">(I</span> <span begin="2:20.763" end="2:21.080">still</span> <span begin="2:21.080" end="2:23.336">see</span></span></p>
                </div></body></tt>
                """;

            Assert.That(TtmlParser.TryParse(withBackingVocal, out IReadOnlyList<LyricLine> lines, out _), Is.True);
            Assert.That(lines.Single().RawText, Is.EqualTo("You made heart ache"));
            Assert.That(lines.Single().Units.Count, Is.EqualTo(4), "a backing vocal never becomes a word the player types");
        }

        [Test]
        public void LineTimedDocumentIsLeftLineLevelAndInterpolated()
        {
            Assert.That(TtmlParser.TryParse(line_timed, out IReadOnlyList<LyricLine> lines, out TtmlParser.TtmlMetadata metadata), Is.True);

            Assert.That(metadata.Timing, Is.EqualTo("Line"));
            Assert.That(lines.Count, Is.EqualTo(3));
            Assert.That(lines[0].RawText, Is.EqualTo("The club isn't the best place to find a lover"));

            // No spans carry times, so there is no word reading to record: the words are the
            // loader's own interpolation across the line, one per token, so the map stays at Line
            // granularity instead of every word after the first collapsing onto a point. The
            // apostrophe survives as a stored mark.
            Assert.That(lines[0].Units.Count, Is.EqualTo(10));
            Assert.That(lines[0].Units[0].StartTime, Is.EqualTo(9731).Within(0.001));
            Assert.That(lines[0].Units.All(u => u.Source == TimingSource.Interpolated), Is.True);
            Assert.That(lines[0].Units.All(u => u.SyllableBoundaries.Count == 0), Is.True);
            Assert.That(lines[0].Units.All(u => u.EndTime > u.StartTime), Is.True, "interpolated words keep a real span each");
            Assert.That(TypeBeatEditorOperations.InferGranularity(lines), Is.EqualTo(TimingGranularity.Line));

            // A mm:ss stamp is the same instant as its seconds form: 1:10.131 == 70131 ms.
            Assert.That(lines[2].StartTime, Is.EqualTo(70131).Within(0.001));
        }

        [Test]
        public void TimesParseInAllThreeForms()
        {
            Assert.That(TtmlParser.TryParseTimestamp("0.155", out double seconds), Is.True);
            Assert.That(TtmlParser.TryParseTimestamp("1:00.331", out double minutes), Is.True);
            Assert.That(TtmlParser.TryParseTimestamp("1:02:03.500", out double hours), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(seconds, Is.EqualTo(155).Within(0.001));
                Assert.That(minutes, Is.EqualTo(60331).Within(0.001));
                Assert.That(hours, Is.EqualTo(3723500).Within(0.001));
                Assert.That(TtmlParser.TryParseTimestamp("", out _), Is.False);
                Assert.That(TtmlParser.TryParseTimestamp("garbage", out _), Is.False);
                Assert.That(TtmlParser.TryParseTimestamp("-1", out _), Is.False);
            });
        }

        [Test]
        public void MetadataIsReportedAndNotBakedIn()
        {
            TtmlParser.TryParse(syllable_timed, out IReadOnlyList<LyricLine> lines, out TtmlParser.TtmlMetadata metadata);

            Assert.Multiple(() =>
            {
                Assert.That(metadata.Timing, Is.EqualTo("Word"));
                Assert.That(metadata.LeadingSilenceMs, Is.EqualTo(140).Within(0.001));
                Assert.That(metadata.LyricOffsetMs, Is.EqualTo(46).Within(0.001));
                Assert.That(metadata.SongEndMs, Is.EqualTo(223398).Within(0.001));

                // The document's own timeline is what the lines carry: neither the leading silence
                // nor the lyric offset is silently added to it.
                Assert.That(lines[0].StartTime, Is.EqualTo(155).Within(0.001));
            });
        }

        [Test]
        public void OffsetMovesLinesWordsAndBoundariesTogether()
        {
            TtmlParser.TryParse(syllable_timed, out IReadOnlyList<LyricLine> lines, out _, offsetMs: -140);

            Assert.That(lines[0].StartTime, Is.EqualTo(15).Within(0.001));
            Assert.That(lines[0].EndTime, Is.EqualTo(lines[1].StartTime).Within(0.001));
            Assert.That(lines[0].Units[0].StartTime, Is.EqualTo(15).Within(0.001));
            Assert.That(lines[0].Units[0].SyllableBoundaries, Is.EqualTo(new[] { 447d, 580d, 820d, 1036d }));

            // Char splits are indices: a shift cannot invalidate one.
            Assert.That(lines[0].Units[0].SyllableSplits, Is.EqualTo(new[] { 2, 4, 6, 9 }));
        }

        [Test]
        public void LinesAreSealedAgainstTheNextStart()
        {
            TtmlParser.TryParse(syllable_timed, out IReadOnlyList<LyricLine> lines, out _);

            Assert.That(lines[0].EndTime, Is.EqualTo(lines[1].StartTime).Within(0.001));

            // The last line runs to the document's stated end (or a bounded tail past its own sung
            // end), exactly as the timing.json loader would seal it. Its sung end is the <p>'s own
            // end (7.878s), which is what the tail is measured from.
            Assert.That(lines[^1].SingEndTime, Is.EqualTo(7878).Within(0.001));
            Assert.That(lines[^1].EndTime, Is.EqualTo(7878 + TimingJsonLoader.LAST_LINE_TAIL_MS).Within(0.001));
        }

        [Test]
        public void ContentlessLinesAreDroppedAndSpannedOver()
        {
            const string withPunctuationOnlyLine =
                """
                <tt><body><div>
                  <p begin="1.0" end="2.0">real one</p>
                  <p begin="2.0" end="3.0">...</p>
                  <p begin="3.0" end="4.0">real two</p>
                </div></body></tt>
                """;

            TtmlParser.TryParse(withPunctuationOnlyLine, out IReadOnlyList<LyricLine> lines, out _);

            Assert.That(lines.Select(l => l.RawText), Is.EqualTo(new[] { "real one", "real two" }));
            Assert.That(lines[0].EndTime, Is.EqualTo(3000).Within(0.001), "the dropped line's span belongs to the line before it");
        }

        [Test]
        public void NonTtmlIsRejectedRatherThanThrowing()
        {
            Assert.Multiple(() =>
            {
                Assert.That(TtmlParser.TryParse("<html><body>hi</body></html>", out _, out _), Is.False);
                Assert.That(TtmlParser.TryParse("not xml at all", out _, out _), Is.False);
                Assert.That(TtmlParser.TryParse("<tt><body>", out _, out _), Is.False, "malformed XML");
                Assert.That(TtmlParser.TryParse("<tt><body><div><p>no stamps at all here</p></div></body></tt>", out _, out _), Is.False,
                    "a line nothing can place in time is not lyric timing");
                Assert.That(TtmlParser.TryParse("<tt><body><div><p>fine</p></div></body></tt>", out _, out _), Is.False);
                Assert.That(TtmlParser.TryParse("", out _, out _), Is.False);
            });
        }

        [Test]
        public void LooksLikeTtmlRecognisesTheRootAndNotAMereNamespaceHit()
        {
            Assert.Multiple(() =>
            {
                Assert.That(TtmlParser.LooksLikeTtml(syllable_timed), Is.True);
                Assert.That(TtmlParser.LooksLikeTtml("<?xml version=\"1.0\"?><tt xmlns=\"http://www.w3.org/ns/ttml\"/>"), Is.True);
                Assert.That(TtmlParser.LooksLikeTtml("[00:01.00] hello\n[00:03.00] world"), Is.False);
                Assert.That(TtmlParser.LooksLikeTtml("hello world"), Is.False);
                Assert.That(TtmlParser.LooksLikeTtml(""), Is.False);
                Assert.That(TtmlParser.IsTtmlFile("song.ttml"), Is.True);
                Assert.That(TtmlParser.IsTtmlFile("song.TTML"), Is.True);
                Assert.That(TtmlParser.IsTtmlFile("song.lrc"), Is.False);
            });
        }

        #endregion

        #region Through the timing.json the map is actually built from

        [Test]
        public void SynthesizedTimingJsonSurvivesTheLoaderRoundTrip()
        {
            string? timing = LyricMapImporter.SynthesizeTimingJsonFromTtml(syllable_timed);
            Assert.That(timing, Is.Not.Null);

            Assert.That(TimingJsonLoader.TryParse(timing!, out IReadOnlyList<LyricLine> lines), Is.True);

            Assert.That(lines.Count, Is.EqualTo(2));
            Assert.That(lines[0].RawText, Is.EqualTo("Abracadabra, abracadabra"));
            Assert.That(lines[0].Units[0].SyllableSplits, Is.EqualTo(new[] { 2, 4, 6, 9 }));
            Assert.That(lines[0].Units[0].SyllableBoundaries, Is.EqualTo(new[] { 587d, 720d, 960d, 1176d }));
            Assert.That(TypeBeatEditorOperations.InferGranularity(lines), Is.EqualTo(TimingGranularity.Syllable));
        }

        [Test]
        public void LineTimedTtmlSynthesizesALineGranularityDocument()
        {
            string? timing = LyricMapImporter.SynthesizeTimingJsonFromTtml(line_timed);
            Assert.That(timing, Is.Not.Null);
            Assert.That(TimingJsonLoader.TryParse(timing!, out IReadOnlyList<LyricLine> lines), Is.True);

            // The document states no word times, so none are invented: the stored document has line
            // stamps only, and the loader interpolates the words the same way it does for an LRC.
            Assert.That(TypeBeatEditorOperations.InferGranularity(lines), Is.EqualTo(TimingGranularity.Line));
            Assert.That(lines[0].Units.Count, Is.EqualTo(10));

            using var document = JsonDocument.Parse(timing!);
            Assert.That(document.RootElement.GetProperty("lines")[0].TryGetProperty("words", out _), Is.False);
        }

        [Test]
        public void SynthesizedDocumentCarriesTheSplitChars()
        {
            string timing = LyricMapImporter.SynthesizeTimingJsonFromTtml(syllable_timed)!;

            using var document = JsonDocument.Parse(timing);
            JsonElement firstWord = document.RootElement.GetProperty("lines")[0].GetProperty("words")[0];

            Assert.That(firstWord.GetProperty("text").GetString(), Is.EqualTo("Abracadabra,"));
            Assert.That(firstWord.GetProperty("split_chars").EnumerateArray().Select(e => e.GetInt32()), Is.EqualTo(new[] { 2, 4, 6, 9 }));
            Assert.That(firstWord.GetProperty("syllables").GetArrayLength(), Is.EqualTo(5));
            Assert.That(firstWord.GetProperty("syllables")[0].GetProperty("text").GetString(), Is.EqualTo("Ab"));
        }

        [Test]
        public void UnusableTtmlSynthesizesNothing()
        {
            Assert.That(LyricMapImporter.SynthesizeTimingJsonFromTtml("<html><body/></html>"), Is.Null);
            Assert.That(LyricMapImporter.SynthesizeTimingJsonFromTtml(""), Is.Null);
        }

        [Test]
        public void ImportPipelineUsesTtmlTimingWithoutAnAligner()
        {
            string audio = Path.Combine(Path.GetTempPath(), $"tb_ttml_audio_{Guid.NewGuid():N}.mp3");
            File.WriteAllBytes(audio, new byte[16]);

            try
            {
                (var result, string? timing) = LyricMapImporter.ProduceTimingJsonAsync(
                    audio, syllable_timed, "Lady Gaga", "Abracadabra",
                    configuredLyricLabPath: null,
                    startDirectories: new[] { Path.GetTempPath() },
                    progress: _ => { },
                    token: System.Threading.CancellationToken.None).GetAwaiter().GetResult();

                Assert.That(result.Success, Is.True, result.Error);
                Assert.That(timing, Is.Not.Null);

                Assert.That(TimingJsonLoader.TryParse(timing!, out IReadOnlyList<LyricLine> lines), Is.True);
                Assert.That(lines[0].Units[0].SyllableSplits, Is.EqualTo(new[] { 2, 4, 6, 9 }));
                Assert.That(TypeBeatEditorOperations.InferGranularity(lines), Is.EqualTo(TimingGranularity.Syllable),
                    "the TTML's own word timing is used instead of the LRC fallback");
            }
            finally
            {
                File.Delete(audio);
            }
        }

        #endregion

        #region The shipped examples

        [Test]
        public void ShippedTtmlExamplesConvert()
        {
            string? dir = ttmlDirectory();

            if (dir == null)
                Assert.Ignore("No ttml/ examples directory found (set TYPEBEAT_TTML_DIR); skipping the real-file pin.");

            foreach (string file in Directory.EnumerateFiles(dir, "*.ttml"))
            {
                string content = File.ReadAllText(file);
                string? timing = LyricMapImporter.SynthesizeTimingJsonFromTtml(content);
                string name = Path.GetFileName(file);

                Assert.That(timing, Is.Not.Null, $"{name} converted");
                Assert.That(TimingJsonLoader.TryParse(timing!, out IReadOnlyList<LyricLine> lines), Is.True, $"{name} parsed back");
                Assert.That(lines.Count, Is.GreaterThan(10), $"{name} produced lines");
                Assert.That(lines.All(l => l.Units.Count > 0), Is.True, $"{name} gave every line its words");
                Assert.That(lines.All(l => l.Units.All(u => u.EndTime > u.StartTime)), Is.True,
                    $"{name}: no word collapsed onto a point");

                // The parser's own reading and the document an import stores must agree line for
                // line, because the editor applies the former and a saved map stores the latter.
                TtmlParser.TryParse(content, out IReadOnlyList<LyricLine> direct, out _);
                Assert.That(direct.Count, Is.EqualTo(lines.Count), $"{name}: same line count both ways");

                // Non-decreasing in time and never inside out.
                Assert.That(lines.Zip(lines.Skip(1)).All(p => p.First.EndTime <= p.Second.StartTime + 0.001), Is.True, name);
                Assert.That(lines.All(l => l.SingEndTime >= l.StartTime), Is.True, name);

                // A word-timed document must keep its granularity; a line-timed one has none to
                // keep, and lands on the loader's line shape (the same as an .lrc import).
                TtmlParser.TryParse(content, out _, out TtmlParser.TtmlMetadata metadata);

                bool wordTimed = metadata.Timing is "Word" or "Syllable";

                if (wordTimed)
                {
                    Assert.That(TypeBeatEditorOperations.InferGranularity(lines), Is.Not.EqualTo(TimingGranularity.Line), name);
                    Assert.That(lines.SelectMany(l => l.Units).All(u => u.Source == TimingSource.Explicit), Is.True, name);
                }
            }
        }

        /// <summary>
        /// The examples directory: an explicit override first (mirroring <c>StandaloneMaps</c>),
        /// otherwise the repo's own ttml/ folder found by walking up from the test assembly.
        /// </summary>
        private static string? ttmlDirectory()
        {
            string? configured = Environment.GetEnvironmentVariable("TYPEBEAT_TTML_DIR");

            if (!string.IsNullOrEmpty(configured))
                return Directory.Exists(configured) ? configured : null;

            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "ttml");

                if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "*.ttml").Any())
                    return candidate;
            }

            return null;
        }

        #endregion
    }
}
