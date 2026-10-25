// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using typebeat.Game.IO;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Import;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// BACKING VOCALS (backlog 445): the model, its <c>"backing"</c> array in a stored map, the
    /// ranked-status strip, the three import paths that used to throw bracketed lyrics away, and the
    /// pose a backing word pops up in. The editor half is TypeBeatEditorOperationsTest.BackingVocals
    /// and the drawn half TestSceneTypeBeatBackingVocals.
    /// </summary>
    [TestFixture]
    public class BackingVocalsTest
    {
        [SetUp]
        public void SetUp() => LyricBeatmapDecoder.Register();

        private static typebeat.Game.Beatmaps.Beatmap decode(string osuText)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(osuText));
            using var reader = new LineBufferedReader(stream);
            return typebeat.Game.Beatmaps.Formats.Decoder.GetDecoder<typebeat.Game.Beatmaps.Beatmap>(reader).Decode(reader);
        }

        private static List<LyricLine> decodedLines(string osuText)
            => decode(osuText).HitObjects.OfType<TypeBeatHitObject>().OrderBy(h => h.LineIndex).Select(h => h.Line).ToList();

        private static string encode(typebeat.Game.Beatmaps.IBeatmap beatmap)
        {
            var writer = new StringWriter();
            TypeBeatBeatmapEncoder.Encode(beatmap, writer);
            return writer.ToString();
        }

        private static IReadOnlyList<LyricLine> timingJson(string json)
        {
            Assert.That(TimingJsonLoader.TryParse(json, out IReadOnlyList<LyricLine> lines), Is.True);
            return lines;
        }

        #region The model

        [Test]
        public void DefaultAnchorsSpreadEvenlyAcrossTheLine()
        {
            Assert.That(BackingVocals.DefaultAnchor(0, 1), Is.EqualTo(0.5));
            Assert.That(Enumerable.Range(0, 4).Select(i => BackingVocals.DefaultAnchor(i, 4)), Is.EqualTo(new[] { 0.125, 0.375, 0.625, 0.875 }));

            var words = new[] { new BackingVocal("a", 0, 10), new BackingVocal("b", 20, 30, 0.05), new BackingVocal("c", 40, 50, 3) };
            Assert.That(BackingVocals.AnchorOf(words, 0), Is.EqualTo(1 / 6.0), "an unplaced word keeps its default place");
            Assert.That(BackingVocals.AnchorOf(words, 1), Is.EqualTo(0.05), "a placed one sits where it was put");
            Assert.That(BackingVocals.AnchorOf(words, 2), Is.EqualTo(1), "and never off the line");
        }

        [Test]
        public void FitKeepsWordsInsideTheirLineAndInOrder()
        {
            var fitting = new[] { new BackingVocal("a", 1000, 1200), new BackingVocal("b", 1500, 1700) };
            Assert.That(BackingVocals.Fit(fitting, 1000, 2000), Is.SameAs(fitting), "a list that already fits comes back untouched");

            var loose = new[]
            {
                new BackingVocal("late", 2500, 2600),
                new BackingVocal("", 1100, 1200),
                new BackingVocal("early", 500, 1400),
                new BackingVocal("inverted", 1600, 1500),
            };

            Assert.That(BackingVocals.Fit(loose, 1000, 2000), Is.EqualTo(new[]
            {
                new BackingVocal("early", 1000, 1400),
                new BackingVocal("inverted", 1600, 1600),
                new BackingVocal("late", 2000, 2000),
            }));

            Assert.That(BackingVocals.Fit(null, 0, 10), Is.Empty);
        }

        [Test]
        public void RebaseKeepsAPlacedAnchorOverTheSameMoment()
        {
            // Placed half way across [0, 1000]: the moment 500, which is the right end of [0, 500].
            var words = new[] { new BackingVocal("a", 100, 200, 0.5), new BackingVocal("b", 300, 400) };
            var rebased = BackingVocals.Rebase(words, 0, 1000, 0, 500);

            Assert.That(rebased[0].Anchor, Is.EqualTo(1));
            Assert.That(rebased[1].Anchor, Is.Null, "a default anchor stays default");
        }

        [Test]
        public void TheBackingArrayRoundTripsThroughJson()
        {
            var words = new[] { new BackingVocal("down", 2600, 3000, 0.123456), new BackingVocal("do]wn \"x\"", 3100, 3500) };

            string json = writeLine(words);
            Assert.That(json, Does.EndWith("]}"), "the array is the last key of the line");

            using var doc = JsonDocument.Parse(json);
            Assert.That(BackingVocals.Read(doc.RootElement), Is.EqualTo(new[] { words[0] with { Anchor = 0.1235 }, words[1] }));

            Assert.That(writeLine(Array.Empty<BackingVocal>()), Is.EqualTo("{\"text\":\"x\"}"), "a line without backing vocals writes no key at all");
        }

        [Test]
        public void ReadSkipsWhatIsNotAWord()
        {
            const string line = "{\"text\":\"x\",\"backing\":[1,{\"start_ms\":5},{\"text\":\"  \",\"start_ms\":5},{\"text\":\"no start\"},"
                                + "{\"text\":\" a  b \",\"start_ms\":10},{\"text\":\"c\",\"start_ms\":20,\"end_ms\":5,\"anchor\":-2},{\"text\":\"d\",\"start_ms\":30,\"anchor\":\"left\"}]}";

            using var doc = JsonDocument.Parse(line);

            Assert.That(BackingVocals.Read(doc.RootElement), Is.EqualTo(new[]
            {
                new BackingVocal("a b", 10, 10),
                new BackingVocal("c", 20, 20, 0),
                new BackingVocal("d", 30, 30),
            }));

            using var none = JsonDocument.Parse("{\"text\":\"x\",\"backing\":{}}");
            Assert.That(BackingVocals.Read(none.RootElement), Is.Null);
        }

        private static string writeLine(IReadOnlyList<BackingVocal> words)
        {
            using var stream = new MemoryStream();

            using (var json = new Utf8JsonWriter(stream))
            {
                json.WriteStartObject();
                json.WriteString("text", "x");
                BackingVocals.Write(json, words);
                json.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        [TestCase("i wanna let my hair down (down)", "i wanna let my hair down ", "down", 25)]
        [TestCase("(oh) hello", " hello", "oh", 0)]
        [TestCase("hold (on [to] me", "hold ", "on to me", 5)]
        [TestCase("stray ) closer", "stray  closer", null, 0)]
        [TestCase("( ) empty", " empty", null, 0)]
        [TestCase("[Chorus]", "", null, 0)]
        [TestCase("[x2] hello (world)", " hello ", "world", 7)]
        [TestCase("([oh] yeah)", "", "oh yeah", 0)]
        [TestCase("[spoken (aside)] hey", " hey", null, 0)]
        public void ExtractSplitsTheTypedTextFromItsBackingVocals(string raw, string main, string? span, int offset)
        {
            var extraction = BackingVocals.Extract(raw);

            Assert.That(extraction.Main, Is.EqualTo(main));
            Assert.That(extraction.Main, Is.EqualTo(Typeability.StripBackingVocals(raw)), "the typed text is exactly the strip it always was");
            Assert.That(extraction.Spans.Select(s => s.Text), Is.EqualTo(span == null ? Array.Empty<string>() : new[] { span }));

            if (span != null)
                Assert.That(extraction.Spans[0].MainOffset, Is.EqualTo(offset));
        }

        [Test]
        public void ASquareBracketIsAnAnnotationNotABackingVocal()
        {
            // Off the typed line all the same, as it always was, but nothing pops up for it.
            var lrc = LrcParser.Parse("[00:01.00] hello [x2] (hey)\n[00:03.00] [Chorus]\n[00:04.00] next\n[00:06.00]\n");

            Assert.That(lrc.Select(l => l.RawText), Is.EqualTo(new[] { "hello", "next" }));
            Assert.That(lrc.SelectMany(l => l.BackingVocals).Select(w => w.Text), Is.EqualTo(new[] { "hey" }));

            var stamped = LrcParser.Parse("[00:01.00]<00:01.00>let <00:01.40>[x2] <00:01.80>(go) <00:02.20>on<00:02.60>").Single();
            Assert.That(stamped.BackingVocals.Select(w => w.Text), Is.EqualTo(new[] { "go" }));

            const string json = "{\"version\":2,\"song_end_ms\":9000,\"lines\":[" +
                                "{\"text\":\"[x2] you (made) it\",\"start_ms\":1000,\"end_ms\":4000,\"words\":[" +
                                "{\"text\":\"[x2]\",\"start_ms\":1000,\"end_ms\":1200},{\"text\":\"you\",\"start_ms\":1200,\"end_ms\":1900}," +
                                "{\"text\":\"(made)\",\"start_ms\":1900,\"end_ms\":2400},{\"text\":\"it\",\"start_ms\":2400,\"end_ms\":3000}]}]}";

            var line = timingJson(json).Single();
            Assert.That(line.Units.Select(u => u.Text), Is.EqualTo(new[] { "you", "it" }));
            Assert.That(line.BackingVocals, Is.EqualTo(new[] { new BackingVocal("made", 1900, 2400) }));
        }

        [Test]
        public void TwoSpansStayTwoSpans()
        {
            var extraction = BackingVocals.Extract("(ooh) baby (yeah yeah)");

            Assert.That(extraction.Spans, Is.EqualTo(new[] { new BackingVocals.Span("ooh", 0), new BackingVocals.Span("yeah yeah", 6) }));
        }

        [Test]
        public void PlaceTimesSpansWhereTheyWereWritten()
        {
            // "(ooh) baby (yeah yeah)": 3 + 6 + 9 = 18 written characters over [0, 1800], so 100 ms each.
            var extraction = BackingVocals.Extract("(ooh) baby (yeah yeah)");
            var words = BackingVocals.Place(extraction.Spans, extraction.Main.Length, 0, 1800, double.PositiveInfinity);

            Assert.That(words.Select(w => (w.Text, w.StartTime, w.EndTime)), Is.EqualTo(new[]
            {
                ("ooh", 0.0, 300.0), ("yeah", 900.0, 1350.0), ("yeah", 1350.0, 1800.0),
            }));

            // Never past the window, and never shorter than the minimum.
            Assert.That(BackingVocals.Place(extraction.Spans, extraction.Main.Length, 0, 1800, 1000).All(w => w.EndTime <= 1000));
            Assert.That(BackingVocals.Place(new[] { new BackingVocals.Span("a", 0) }, 0, 500, 500, 9999).Single().EndTime, Is.EqualTo(500 + BackingVocals.MIN_SPAN_MS));
        }

        #endregion

        #region A stored map

        private static LyricLine lineWithBacking() => new LyricLine
        {
            RawText = "i wanna let my hair",
            StartTime = 1000,
            EndTime = 4000,
            SingEndTime = 3800,
            Units = new[]
            {
                new TimedUnit { Text = "i", StartTime = 1000, EndTime = 1300, Source = TimingSource.Explicit },
                new TimedUnit { Text = "wanna", StartTime = 1300, EndTime = 2000, Source = TimingSource.Explicit },
                new TimedUnit { Text = "let", StartTime = 2000, EndTime = 2500, Source = TimingSource.Explicit },
                new TimedUnit { Text = "my", StartTime = 2500, EndTime = 3000, Source = TimingSource.Explicit },
                new TimedUnit { Text = "hair", StartTime = 3000, EndTime = 3800, Source = TimingSource.Explicit },
            },
            BackingVocals = new[] { new BackingVocal("down", 2600, 3000, 0.05), new BackingVocal("(down)", 3100, 3500) },
        };

        private static typebeat.Game.Beatmaps.Beatmap beatmapOf(params LyricLine[] lines)
        {
            var beatmap = new typebeat.Game.Beatmaps.Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.Metadata.Artist = "Backing";
            beatmap.Metadata.Title = "Vocals";
            beatmap.Metadata.AudioFile = "audio.mp3";

            for (int i = 0; i < lines.Length; i++)
                beatmap.HitObjects.Add(new TypeBeatHitObject { StartTime = lines[i].StartTime, LineIndex = i, Line = lines[i], Granularity = TimingGranularity.Word });

            return beatmap;
        }

        [Test]
        public void AStoredMapKeepsItsBackingVocalsExactly()
        {
            string osu = encode(beatmapOf(lineWithBacking()));
            var lines = decodedLines(osu);

            Assert.That(osu, Does.Contain(",\"backing\":[{\"text\":\"down\",\"start_ms\":2600,\"end_ms\":3000,\"anchor\":0.05},{\"text\":\"(down)\",\"start_ms\":3100,\"end_ms\":3500}]}"));
            Assert.That(lines.Single().BackingVocals, Is.EqualTo(lineWithBacking().BackingVocals), "a stored bracket is the word's own text, as in the typed lyric");
            Assert.That(lines.Single().RawText, Is.EqualTo("i wanna let my hair"));
            Assert.That(encode(decode(osu)), Is.EqualTo(osu), "and it saves back byte for byte");
        }

        [Test]
        public void AStoredWordOutsideItsLineIsFittedOnLoad()
        {
            string osu = encode(beatmapOf(lineWithBacking())).Replace("\"start_ms\":3100,\"end_ms\":3500", "\"start_ms\":3100,\"end_ms\":9500");

            Assert.That(decodedLines(osu).Single().BackingVocals[1].EndTime, Is.EqualTo(4000));
        }

        [Test]
        public void BackingVocalsAreNoGameplayChange()
        {
            var ruleset = new TypeBeatRuleset();
            var plain = lineWithBacking();
            string without = encode(beatmapOf(BackingVocals.With(plain, null)));
            string with = encode(beatmapOf(plain));

            Assert.That(with, Is.Not.EqualTo(without));
            Assert.That(LyricOsuFormat.StripBacking(with), Is.EqualTo(without), "the strip takes the array and nothing else");
            Assert.That(ruleset.NativeEncodingsEquivalentForStatus(with, without), Is.True, "adding backing vocals keeps the rank");

            string moved = with.Replace("\"anchor\":0.05", "\"anchor\":0.9");
            Assert.That(ruleset.NativeEncodingsEquivalentForStatus(with, moved), Is.True, "and so does placing one");

            string retyped = with.Replace("{\"text\":\"hair\"", "{\"text\":\"hare\"");
            Assert.That(retyped, Is.Not.EqualTo(with));
            Assert.That(ruleset.NativeEncodingsEquivalentForStatus(with, retyped), Is.False, "while a typed word is still a gameplay change");
        }

        [Test]
        public void NothingThatPlaysTheLineSeesTheBackingVocals()
        {
            var with = lineWithBacking();
            var without = BackingVocals.With(with, null);

            foreach (bool literate in new[] { false, true })
            {
                var a = typebeat.Game.Rulesets.TypeBeat.Gameplay.TypingLine.FromLyricLine(with, literate, alignSubdivisionTargets: true).Cells;
                var b = typebeat.Game.Rulesets.TypeBeat.Gameplay.TypingLine.FromLyricLine(without, literate, alignSubdivisionTargets: true).Cells;

                Assert.That(a.Select(c => (c.Expected, c.TargetTime)), Is.EqualTo(b.Select(c => (c.Expected, c.TargetTime))), $"literate {literate}");
            }
        }

        #endregion

        #region Imports

        [Test]
        public void AnLrcBracketBecomesABackingVocalWhereItSat()
        {
            var line = LrcParser.Parse("[00:01.00] i wanna let my hair down (down)\n[00:05.00]\n").Single();

            Assert.That(line.RawText, Is.EqualTo("i wanna let my hair down"), "the typed lyric is what it always was");
            Assert.That(line.BackingVocals.Select(w => w.Text), Is.EqualTo(new[] { "down" }));

            // Where it was written: the last 4 of the line's 28 characters (the space after the tag
            // counts for nothing), so it is sung at the end of the line and ends with it, rather than
            // starting as the line leaves.
            double sung = line.SingEndTime - line.StartTime;
            Assert.That(line.BackingVocals[0].StartTime, Is.EqualTo(line.StartTime + sung * 24 / 28).Within(1));
            Assert.That(line.BackingVocals[0].EndTime, Is.EqualTo(line.SingEndTime).Within(1));
        }

        [Test]
        public void AWholeLineLrcBackingVocalJoinsTheLineBeingSung()
        {
            var lines = LrcParser.Parse("[00:01.00] real one\n[00:02.00] (backing echo)\n[00:04.00] real two\n[00:06.00]\n");

            Assert.That(lines.Select(l => l.RawText), Is.EqualTo(new[] { "real one", "real two" }), "the line itself is still not a line");
            Assert.That(lines[0].EndTime, Is.EqualTo(4000), "and the typed windows are what they were");

            Assert.That(lines[0].BackingVocals.Select(w => w.Text), Is.EqualTo(new[] { "backing", "echo" }));
            Assert.That(lines[0].BackingVocals[0].StartTime, Is.EqualTo(2000));
            Assert.That(lines[0].BackingVocals[^1].EndTime, Is.LessThanOrEqualTo(4000));
            Assert.That(lines[1].BackingVocals, Is.Empty);
        }

        [Test]
        public void AWholeLineBackingVocalBeforeEveryLineJoinsTheFirst()
        {
            var lines = LrcParser.Parse("[00:00.50] (ah)\n[00:01.00] real one\n[00:03.00]\n");

            Assert.That(lines.Single().RawText, Is.EqualTo("real one"));
            Assert.That(lines.Single().BackingVocals.Select(w => (w.Text, w.StartTime)), Is.EqualTo(new[] { ("ah", 1000.0) }), "held at the line's start");
        }

        [Test]
        public void EnhancedLrcStampsTimeTheBackingWords()
        {
            // A stamp inside the brackets, and the usual shape: a stamp ahead of the bracket.
            var inside = LrcParser.Parse("[00:01.00]<00:01.00>Ne<00:01.20>ver (<00:01.50>oh) <00:02.00>mind<00:02.50>").Single();
            Assert.That(inside.BackingVocals.Select(w => (w.Text, w.StartTime)), Is.EqualTo(new[] { ("oh", 1500.0) }));

            var ahead = LrcParser.Parse("[00:01.00]<00:01.00>let <00:01.40>it <00:01.80>(down) <00:02.20>go<00:02.60>").Single();
            Assert.That(ahead.RawText, Is.EqualTo("let it go"));
            Assert.That(ahead.BackingVocals.Select(w => (w.Text, w.StartTime)), Is.EqualTo(new[] { ("down", 1800.0) }));
        }

        [Test]
        public void ATimingJsonBracketKeepsItsOwnAlignerTimesAndTheTypedWordsKeepTheirs()
        {
            const string json = "{\"version\":2,\"song_end_ms\":9000,\"lines\":[" +
                                "{\"text\":\"you made (I) heart ache\",\"start_ms\":1000,\"end_ms\":4000,\"words\":[" +
                                "{\"text\":\"you\",\"start_ms\":1000,\"end_ms\":1400},{\"text\":\"made\",\"start_ms\":1400,\"end_ms\":1900}," +
                                "{\"text\":\"(I)\",\"start_ms\":1700,\"end_ms\":2100},{\"text\":\"heart\",\"start_ms\":2000,\"end_ms\":2900}," +
                                "{\"text\":\"ache\",\"start_ms\":2900,\"end_ms\":3700}]}]}";

            var line = timingJson(json).Single();

            Assert.That(line.RawText, Is.EqualTo("you made heart ache"));
            Assert.That(line.Units.Select(u => (u.Text, u.StartTime, u.EndTime)), Is.EqualTo(new[]
            {
                ("you", 1000.0, 1400.0), ("made", 1400.0, 1900.0), ("heart", 2000.0, 2900.0), ("ache", 2900.0, 3700.0),
            }), "the typed words keep the aligner's own times rather than falling back to interpolation");
            Assert.That(line.BackingVocals, Is.EqualTo(new[] { new BackingVocal("I", 1700, 2100) }));
        }

        [Test]
        public void ATimingJsonLineThatDoesNotPairIsPlacedByWhereTheBracketSat()
        {
            // The aligner's words do not pair with the tokens (a mixed token), so the line falls back
            // to interpolation as it always did, and the span is placed by its offset.
            const string json = "{\"version\":2,\"song_end_ms\":9000,\"lines\":[" +
                                "{\"text\":\"hold on(to) me\",\"start_ms\":1000,\"end_ms\":3000,\"words\":[" +
                                "{\"text\":\"hold\",\"start_ms\":1000,\"end_ms\":1500},{\"text\":\"on(to)\",\"start_ms\":1500,\"end_ms\":2200},{\"text\":\"me\",\"start_ms\":2200,\"end_ms\":3000}]}]}";

            var line = timingJson(json).Single();

            Assert.That(line.RawText, Is.EqualTo("hold on me"));
            Assert.That(line.BackingVocals.Select(w => w.Text), Is.EqualTo(new[] { "to" }));
            Assert.That(line.BackingVocals[0].StartTime, Is.GreaterThan(1000).And.LessThan(3000));
        }

        [Test]
        public void AWholeLineTimingJsonBackingVocalJoinsTheLineBeingSung()
        {
            const string json = "{\"version\":2,\"song_end_ms\":20000,\"lines\":[" +
                                "{\"text\":\"one two three\",\"start_ms\":1000,\"end_ms\":4000,\"words\":[{\"text\":\"one\",\"start_ms\":1000,\"end_ms\":2000}]}," +
                                "{\"text\":\"(backing only)\",\"start_ms\":4200,\"end_ms\":4600}," +
                                "{\"text\":\"aa bb\",\"start_ms\":5000,\"end_ms\":7000}]}";

            var lines = timingJson(json);

            Assert.That(lines.Select(l => l.RawText), Is.EqualTo(new[] { "one two three", "aa bb" }));
            Assert.That(lines[0].BackingVocals.Select(w => w.Text), Is.EqualTo(new[] { "backing", "only" }));
            Assert.That(lines[0].BackingVocals.All(w => w.StartTime >= lines[0].StartTime && w.EndTime <= lines[0].EndTime));
        }

        [Test]
        public void ADocumentWithoutBracketsIsUntouched()
        {
            const string json = "{\"version\":2,\"lines\":[{\"text\":\"plain line\",\"start_ms\":1000,\"end_ms\":2000}]}";

            Assert.That(BackingVocalImport.FromTimingJson(json), Is.SameAs(json));
            Assert.That(BackingVocalImport.FromTimingJson("not json ("), Is.EqualTo("not json ("));
        }

        [Test]
        public void TheOszWriterKeepsTheBackingVocalsInTheMap()
        {
            const string json = "{\"version\":2,\"song_end_ms\":9000,\"lines\":[" +
                                "{\"text\":\"i wanna let my hair down (down)\",\"start_ms\":1000,\"end_ms\":5000}]}";

            string osu = LyricOsuFormat.GenerateOsu("An Artist", "A Title", "audio.mp3", "tester", TimingJsonPostProcess.ExtractBackingVocalLines(json));
            var line = decodedLines(osu).Single();

            Assert.That(line.RawText, Is.EqualTo("i wanna let my hair down"));
            Assert.That(line.BackingVocals.Select(w => w.Text), Is.EqualTo(new[] { "down" }));
        }

        [Test]
        public void TtmlBackgroundVocalsBecomeBackingVocals()
        {
            const string ttml =
                """
                <tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata"><body><div>
                  <p begin="2:19.958" end="2:23.336"><span begin="2:19.958" end="2:20.489">You</span> <span begin="2:20.489" end="2:20.806">made</span> <span begin="2:20.806" end="2:21.838">heart</span> <span begin="2:21.838" end="2:22.686">ache</span> <span ttm:role="x-bg"><span begin="2:20.363" end="2:20.763">(I</span> <span begin="2:20.763" end="2:21.080">still</span> <span begin="2:21.080" end="2:22.336">see)</span></span></p>
                  <p begin="2:24.000" end="2:25.000"><span ttm:role="x-bg"><span begin="2:24.000" end="2:24.500">(ooh</span> <span begin="2:24.500" end="2:25.000">ooh)</span></span></p>
                  <p begin="2:26.000" end="2:27.000"><span begin="2:26.000" end="2:27.000">again</span></p>
                </div></body></tt>
                """;

            Assert.That(TtmlParser.TryParse(ttml, out IReadOnlyList<LyricLine> lines, out _), Is.True);
            Assert.That(lines.Select(l => l.RawText), Is.EqualTo(new[] { "You made heart ache", "again" }), "a paragraph of backing vocals alone is no line");
            Assert.That(lines[1].BackingVocals, Is.Empty);

            // The backing-only paragraph is sung inside the first line's window (it runs to the next
            // line), so its words join that line at their own times.
            var line = lines[0];
            Assert.That(line.RawText, Is.EqualTo("You made heart ache"));
            Assert.That(line.Units.Count, Is.EqualTo(4), "a backing vocal never becomes a word the player types");

            Assert.That(line.BackingVocals.Select(w => (w.Text, w.StartTime)), Is.EqualTo(new[]
            {
                ("I", 140363.0), ("still", 140763.0), ("see", 141080.0), ("ooh", 144000.0), ("ooh", 144500.0),
            }).Using<(string, double)>((a, b) => a.Item1 == b.Item1 && Math.Abs(a.Item2 - b.Item2) < 1 ? 0 : 1));
            Assert.That(line.BackingVocals.All(w => w.StartTime >= line.StartTime && w.EndTime <= line.EndTime), "fitted into the line's window");
        }

        #endregion

        #region The pop-up

        [Test]
        public void ABackingWordIsHiddenUntilItIsSungThenSettles()
        {
            Assert.That(LyricLineDisplay.BackingPop(-1).Alpha, Is.Zero);
            Assert.That(LyricLineDisplay.BackingPop(0).Alpha, Is.Zero);

            var early = LyricLineDisplay.BackingPop(LyricLineDisplay.BACKING_POP_DURATION_MS / 6);
            Assert.That(early.Alpha, Is.GreaterThan(0).And.LessThan(1));
            Assert.That(early.Scale, Is.LessThan(1));
            Assert.That(early.Lift, Is.GreaterThan(0), "still below its resting place");

            var settled = LyricLineDisplay.BackingPop(LyricLineDisplay.BACKING_POP_DURATION_MS);
            Assert.That(settled, Is.EqualTo(new LyricLineDisplay.BackingPose(1, 0, 1, 1)));
            Assert.That(LyricLineDisplay.BackingPop(60000), Is.EqualTo(settled), "and stays put");

            // The throw overshoots its resting place a little on the way.
            var poses = Enumerable.Range(1, 99).Select(i => LyricLineDisplay.BackingPop(LyricLineDisplay.BACKING_POP_DURATION_MS * i / 100)).ToList();
            Assert.That(poses.Min(p => p.Lift), Is.LessThan(0));
            Assert.That(poses.Select(p => p.Alpha), Is.Ordered);
        }

        [Test]
        public void TheTiltIsWithinTenDegreesAndTheSameEveryTime()
        {
            var tilts = Enumerable.Range(0, 500).Select(i => LyricLineDisplay.BackingTilt(new BackingVocal($"w{i}", i * 137.5, i * 137.5 + 200), i % 5)).ToList();

            Assert.That(tilts.All(t => Math.Abs(t) <= LyricLineDisplay.BACKING_MAX_TILT_DEGREES));
            Assert.That(tilts.Count(t => t > 0), Is.GreaterThan(150), "both ways");
            Assert.That(tilts.Count(t => t < 0), Is.GreaterThan(150), "both ways");
            Assert.That(tilts.Distinct().Count(), Is.GreaterThan(400), "and not a handful of values");

            var word = new BackingVocal("down", 2600, 3000);
            Assert.That(LyricLineDisplay.BackingTilt(word, 0), Is.EqualTo(LyricLineDisplay.BackingTilt(word with { Anchor = 0.3, EndTime = 5000 }, 0)),
                "derived from the word and its time, not drawn at random or from where it pops up");
        }

        #endregion
    }
}
