// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.IO;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Backlog 330: the map format keeps the ORIGINAL text. Pins the format's shape (an optional
    /// <c>original</c> per line and per word, written only when it differs), the byte identity of a
    /// map without originals, the round trip with them, the unromanised word and line, and that none
    /// of it reaches a cell, a target time or a rating.
    /// </summary>
    [TestFixture]
    public class OriginalTextFormatTest
    {
        [SetUp]
        public void SetUp() => LyricBeatmapDecoder.Register();

        #region Fixtures

        private static TimedUnit unit(string text, double start, double end, string? original = null) => new TimedUnit
        {
            Text = text,
            StartTime = start,
            EndTime = end,
            Source = TimingSource.Explicit,
            Original = original,
        };

        /// <summary>A Russian line with its originals, a plain English line, and a Japanese line with one kanji left unromanised.</summary>
        private static List<LyricLine> linesWithOriginals() => new List<LyricLine>
        {
            new LyricLine
            {
                RawText = "Privet mir",
                Original = "Привет мир",
                StartTime = 1000,
                EndTime = 3000,
                SingEndTime = 2800,
                Units = new[] { unit("Privet", 1000, 1900, "Привет"), unit("mir", 1900, 2800, "мир") },
            },
            new LyricLine
            {
                RawText = "hello world",
                StartTime = 3000,
                EndTime = 5000,
                SingEndTime = 4800,
                Units = new[] { unit("hello", 3000, 3900), unit("world", 3900, 4800) },
            },
            new LyricLine
            {
                RawText = "kimi no",
                Original = "君 きみ の",
                StartTime = 5000,
                EndTime = 8000,
                SingEndTime = 7500,
                Units = new[] { unit("kimi", 5600, 6400, "きみ"), unit("no", 6400, 7500, "の") },
                UnromanisedWords = new[] { new UnromanisedWord(0, "君", 5000, 5600) },
            },
        };

        private static List<LyricLine> asciiLines() => new List<LyricLine>
        {
            new LyricLine
            {
                RawText = "hello world",
                StartTime = 1000,
                EndTime = 3000,
                SingEndTime = 2800,
                Units = new[] { unit("hello", 1000, 1900), unit("world", 1900, 2800) },
            },
            new LyricLine
            {
                RawText = "yeah",
                StartTime = 3000,
                EndTime = 6000,
                SingEndTime = 5500,
                Units = new[] { unit("yeah", 3000, 5500) },
            },
        };

        internal static Beatmap BuildBeatmap(IReadOnlyList<LyricLine> lines, TimingGranularity granularity = TimingGranularity.Word)
        {
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.Metadata.Artist = "Artist";
            beatmap.Metadata.Title = "Title";
            beatmap.Metadata.AudioFile = "audio.mp3";

            for (int i = 0; i < lines.Count; i++)
            {
                beatmap.HitObjects.Add(new TypeBeatHitObject
                {
                    StartTime = lines[i].StartTime,
                    LineIndex = i,
                    Line = lines[i],
                    Granularity = granularity,
                });
            }

            return beatmap;
        }

        internal static string Encode(IBeatmap beatmap)
        {
            var sb = new StringBuilder();
            using (var sw = new StringWriter(sb))
                TypeBeatBeatmapEncoder.Encode(beatmap, sw);

            return sb.ToString();
        }

        internal static Beatmap Decode(string text)
        {
            LyricBeatmapDecoder.Register();

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
            using var reader = new LineBufferedReader(stream);
            return (Beatmap)typebeat.Game.Beatmaps.Formats.Decoder.GetDecoder<Beatmap>(reader).Decode(reader);
        }

        private static List<LyricLine> linesOf(Beatmap beatmap)
            => beatmap.HitObjects.OfType<TypeBeatHitObject>().OrderBy(h => h.LineIndex).Select(h => h.Line).ToList();

        #endregion

        /// <summary>
        /// THE BYTE-IDENTITY PIN. A map with no originals must encode exactly as it did before the
        /// field existed: this literal is the encoder's output for <see cref="asciiLines"/> as the
        /// pre-330 encoder wrote it, and a single stray key (an empty original, a null written out)
        /// moves it, which would re-hash every installed map.
        /// </summary>
        [Test]
        public void AMapWithoutOriginalsEncodesByteForByteAsBefore()
        {
            string encoded = Encode(BuildBeatmap(asciiLines()));
            string lyrics = encoded.Substring(encoded.IndexOf("[Lyrics]", System.StringComparison.Ordinal));

            Assert.That(lyrics.Replace("\r\n", "\n"), Is.EqualTo(
                "[Lyrics]\n"
                + "{\"version\":2,\"song_end_ms\":6000,\"granularity\":\"Word\"}\n"
                + "{\"text\":\"hello world\",\"start_ms\":1000,\"end_ms\":2800,\"words\":[{\"text\":\"hello\",\"start_ms\":1000,\"end_ms\":1900,\"score\":1},{\"text\":\"world\",\"start_ms\":1900,\"end_ms\":2800,\"score\":1}]}\n"
                + "{\"text\":\"yeah\",\"start_ms\":3000,\"end_ms\":5500,\"words\":[{\"text\":\"yeah\",\"start_ms\":3000,\"end_ms\":5500,\"score\":1}]}\n"));

            Assert.That(encoded, Does.Not.Contain("original"));
        }

        [Test]
        public void OriginalsRoundTripThroughSaveAndReload()
        {
            var source = linesWithOriginals();
            string first = Encode(BuildBeatmap(source));
            var reloaded = linesOf(Decode(first));

            Assert.That(reloaded.Count, Is.EqualTo(3));

            Assert.That(reloaded[0].RawText, Is.EqualTo("Privet mir"));
            Assert.That(reloaded[0].Original, Is.EqualTo("Привет мир"));
            Assert.That(reloaded[0].Units.Select(u => u.Original), Is.EqualTo(new[] { "Привет", "мир" }));

            Assert.That(reloaded[1].Original, Is.Null);
            Assert.That(reloaded[1].Units.Select(u => u.Original), Is.All.Null);

            Assert.That(reloaded[2].RawText, Is.EqualTo("kimi no"));
            Assert.That(reloaded[2].Original, Is.EqualTo("君 きみ の"));
            Assert.That(reloaded[2].Units.Select(u => u.Text), Is.EqualTo(new[] { "kimi", "no" }));
            Assert.That(reloaded[2].Units.Select(u => u.StartTime), Is.EqualTo(new[] { 5600.0, 6400.0 }), "the paired words keep their own times");
            Assert.That(reloaded[2].UnromanisedWords, Is.EqualTo(new[] { new UnromanisedWord(0, "君", 5000, 5600) }));

            // A second save is byte-stable (the editor's undo stack diffs encodings).
            Assert.That(Encode(BuildBeatmap(reloaded)), Is.EqualTo(first));
        }

        [Test]
        public void TheOriginalKeySitsAfterTheTextAndOnlyWhereItDiffers()
        {
            string encoded = Encode(BuildBeatmap(linesWithOriginals()));

            Assert.That(encoded, Does.Contain("{\"text\":\"Privet mir\",\"original\":\""));
            Assert.That(encoded, Does.Contain("{\"text\":\"\",\"original\":\""), "the unromanised word: an empty text beside its original");
            Assert.That(encoded, Does.Contain("{\"text\":\"hello world\",\"start_ms\":3000"), "the English line carries none");

            // A unit whose "original" says what its text says writes nothing.
            var same = new List<LyricLine>
            {
                new LyricLine
                {
                    RawText = "hello",
                    Original = "hello",
                    StartTime = 0,
                    EndTime = 1000,
                    SingEndTime = 1000,
                    Units = new[] { unit("hello", 0, 1000, "hello") },
                },
            };

            Assert.That(Encode(BuildBeatmap(same)), Does.Not.Contain("original"));
        }

        /// <summary>
        /// An original is DISPLAY data: the same lyric with and without originals has identical cells,
        /// target times, difficulty and pace, and the unromanised word contributes nothing at all.
        /// </summary>
        [Test]
        public void OriginalsChangeNoCellAndNoRating()
        {
            var with = linesOf(Decode(Encode(BuildBeatmap(linesWithOriginals()))));
            string stripped = LyricOsuFormat.StripOriginals(Encode(BuildBeatmap(linesWithOriginals())));
            var without = linesOf(Decode(stripped));

            Assert.That(stripped, Does.Not.Contain("original"));

            // Stripped, the unromanised word's empty text is an ordinary (empty) aligner word again,
            // which breaks the pairing of its line: so the comparison is over the lines where the
            // original is only display data.
            for (int i = 0; i < 2; i++)
            {
                var a = TypingLine.FromLyricLine(with[i]);
                var b = TypingLine.FromLyricLine(without[i]);

                Assert.That(a.Cells.Select(c => c.Expected), Is.EqualTo(b.Cells.Select(c => c.Expected)));
                Assert.That(a.Cells.Select(c => c.TargetTime), Is.EqualTo(b.Cells.Select(c => c.TargetTime)));
            }

            Assert.That(LyricDifficulty.Compute(with.Take(2)), Is.EqualTo(LyricDifficulty.Compute(without.Take(2))));
            Assert.That(LyricPaceStatistics.Compute(with.Take(2)).AverageWpm, Is.EqualTo(LyricPaceStatistics.Compute(without.Take(2)).AverageWpm));

            // The unromanised word has no cell: the Japanese line types "kimi no" and nothing else.
            Assert.That(new string(TypingLine.FromLyricLine(with[2]).Cells.Select(c => c.Expected).ToArray()), Is.EqualTo("kimi no"));
        }

        /// <summary>
        /// A line made of nothing but unromanised words (an import of a script the romaniser cannot
        /// read yet) survives the decode so the editor can romanise it, types nothing, and every
        /// consumer of the map tolerates it: the difficulty model, the pace figures, the WPM curve,
        /// the instrumental gaps, the autoplay generator and the engine.
        /// </summary>
        [Test]
        public void ALineOfOnlyUnromanisedWordsSurvivesAndPlaysAsNothing()
        {
            var lines = new List<LyricLine>
            {
                asciiLines()[0],
                new LyricLine
                {
                    RawText = string.Empty,
                    Original = "你好",
                    StartTime = 3000,
                    EndTime = 5000,
                    SingEndTime = 4500,
                    Units = System.Array.Empty<TimedUnit>(),
                    UnromanisedWords = new[] { new UnromanisedWord(0, "你好", 3000, 4500) },
                },
                new LyricLine
                {
                    RawText = "yeah",
                    StartTime = 5000,
                    EndTime = 8000,
                    SingEndTime = 7500,
                    Units = new[] { unit("yeah", 5000, 7500) },
                },
            };

            var decoded = Decode(Encode(BuildBeatmap(lines)));
            var reloaded = linesOf(decoded);

            Assert.That(reloaded.Count, Is.EqualTo(3), "the pending line is kept");
            Assert.That(reloaded[1].RawText, Is.Empty);
            Assert.That(reloaded[1].Units, Is.Empty);
            Assert.That(reloaded[1].UnromanisedWords.Single().Original, Is.EqualTo("你好"));

            Assert.That(TypingLine.FromLyricLine(reloaded[1]).Cells.Count(c => c.IsTypeable), Is.Zero);
            Assert.DoesNotThrow(() => LyricDifficulty.Compute(reloaded));
            Assert.DoesNotThrow(() => LyricPaceStatistics.Compute(reloaded));
            Assert.DoesNotThrow(() => LyricWpmCurve.Compute(reloaded));
            Assert.DoesNotThrow(() => InstrumentalGaps.Compute(reloaded.Select(l => TypingLine.FromLyricLine(l)).ToList()));

            // Autoplay across it perfects the map, exactly as AutoplayRealMapTest drives the engine.
            var converted = new TypeBeatBeatmapConverter(decoded, new TypeBeatRuleset()).Convert(CancellationToken.None);
            var map = new TypeBeatBeatmap();

            foreach (var h in converted.HitObjects.OfType<TypeBeatHitObject>().OrderBy(h => h.LineIndex))
                map.HitObjects.Add(h);

            var frames = new TypeBeatAutoGenerator(map).Generate().Frames.Cast<TypeBeatReplayFrame>().ToList();
            Assert.That(new string(frames.Select(f => f.Character).ToArray()), Is.EqualTo("hello worldyeah"));

            var engine = new TypingEngine(new LyricBeatmap
            {
                Metadata = new LyricBeatmapMetadata { Artist = "a", Title = "t", FolderPath = string.Empty, AudioFileName = "a.mp3" },
                Lines = reloaded,
                Granularity = TimingGranularity.Word,
            }) { FletcherEnabled = true, FlexibleLineSnap = true, BoundedRush = true };

            int next = 0;

            for (double now = 0; now <= 20000; now += 1000.0 / 60)
            {
                while (next < frames.Count && frames[next].Time <= now)
                {
                    engine.Update(frames[next].Time);
                    Assert.That(engine.ProcessKey(frames[next].Character, frames[next].Time), Is.True);
                    next++;
                }

                engine.Update(now);
            }

            Assert.That(next, Is.EqualTo(frames.Count));
            Assert.That(engine.IsFinished, Is.True);
            Assert.That(engine.Lines.Sum(l => l.Cells.Count(c => c.State == CellState.Missed)), Is.Zero);
        }

        /// <summary>
        /// Without its original a no-cell line is dropped exactly as it always was: the exception is
        /// the original, nothing else.
        /// </summary>
        [Test]
        public void ANoCellLineWithoutAnOriginalIsStillDropped()
        {
            string json = "{\"version\":2,\"lines\":["
                          + "{\"text\":\"hello\",\"start_ms\":0,\"end_ms\":900},"
                          + "{\"text\":\"你好\",\"start_ms\":1000,\"end_ms\":1900},"
                          + "{\"text\":\"\",\"original\":\"你好\",\"start_ms\":2000,\"end_ms\":2900},"
                          + "{\"text\":\"yeah\",\"start_ms\":3000,\"end_ms\":3900}]}";

            Assert.That(TimingJsonLoader.TryParse(json, out var lines), Is.True);
            Assert.That(lines.Select(l => l.RawText), Is.EqualTo(new[] { "hello", string.Empty, "yeah" }));
            Assert.That(lines[1].Original, Is.EqualTo("你好"));
            Assert.That(lines[0].EndTime, Is.EqualTo(2000), "the dropped line's span went to the line before it");
        }

        /// <summary>
        /// The HAS-ORIGINALS flag is derived at decode and never stored: any line or word with an
        /// original (an unromanised word included) sets it, and a map without any has it clear.
        /// </summary>
        [Test]
        public void TheHasOriginalsFlagIsDerivedAtDecode()
        {
            Assert.That(LyricOriginals.HasOriginals(Decode(Encode(BuildBeatmap(linesWithOriginals())))), Is.True);
            Assert.That(LyricOriginals.HasOriginals(Decode(Encode(BuildBeatmap(asciiLines())))), Is.False);
        }

        /// <summary>
        /// A LINE-granularity map writes no words[], so its words' originals ride the LINE original
        /// and are derived back at decode when the two token counts agree.
        /// </summary>
        [Test]
        public void ALineGranularityMapDerivesWordOriginalsFromTheLine()
        {
            var lines = new List<LyricLine>
            {
                new LyricLine
                {
                    RawText = "Privet mir",
                    Original = "Привет мир",
                    StartTime = 1000,
                    EndTime = 3000,
                    SingEndTime = 2800,
                    Units = LrcParser.InterpolateUnits("Privet mir", 1000, 2800),
                },
            };

            string encoded = Encode(BuildBeatmap(lines, TimingGranularity.Line));
            Assert.That(encoded, Does.Not.Contain("\"words\""));

            var reloaded = linesOf(Decode(encoded)).Single();
            Assert.That(reloaded.Original, Is.EqualTo("Привет мир"));
            Assert.That(reloaded.Units.Select(u => u.Original), Is.EqualTo(new[] { "Привет", "мир" }));
        }

        /// <summary>
        /// The local ranked-status check ignores originals, as the server's gameplay fingerprint does:
        /// adding a map's source script is not a gameplay edit and must not mark a ranked map as
        /// locally modified. Changing a word's TEXT still is.
        /// </summary>
        [Test]
        public void TheLocalStatusCheckIgnoresOriginals()
        {
            var ruleset = new TypeBeatRuleset();

            var bare = linesWithOriginals().Take(2).Select(l => new LyricLine
            {
                RawText = l.RawText,
                StartTime = l.StartTime,
                EndTime = l.EndTime,
                SingEndTime = l.SingEndTime,
                Units = l.Units.Select(u => unit(u.Text, u.StartTime, u.EndTime)).ToArray(),
            }).ToList();

            string withOriginals = Encode(BuildBeatmap(linesWithOriginals().Take(2).ToList()));
            string withoutOriginals = Encode(BuildBeatmap(bare));

            Assert.That(withOriginals, Is.Not.EqualTo(withoutOriginals));
            Assert.That(ruleset.NativeEncodingsEquivalentForStatus(withOriginals, withoutOriginals), Is.True);
            Assert.That(ruleset.NativeEncodingsEquivalentForStatus(withOriginals, withoutOriginals.Replace("Privet", "Privyet")), Is.False);
        }
    }
}
