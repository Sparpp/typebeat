// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// THE IMPORT-TIME SYLLABIFICATION PASS (backlog 363, ImportSyllables). The engine no longer splits a
// word at gameplay, so an import writes the syllabifier's natural split ONCE, as authored
// subdivisions: boundaries at the cut cells' flat-ramp targets and split_chars always. These pins
// hold the pass to reproducing the stored era's groups exactly, show why split_chars is mandatory,
// run it over a real LRC and an aligner document, and pin that a stored map is NOT touched on load.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Edit;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Import;
using typebeat.Game.Rulesets.TypeBeat.Objects;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class ImportSyllablesTest
    {
        [SetUp]
        public void SetUp() => LyricBeatmapDecoder.Register();

        private static LyricLine lineOf(params TimedUnit[] units) => new LyricLine
        {
            RawText = string.Join(' ', units.Select(u => u.Text)),
            StartTime = units[0].StartTime,
            EndTime = units[^1].EndTime + 1000,
            SingEndTime = units[^1].EndTime,
            Units = units,
        };

        private static TimedUnit unit(string text, double start, double end) => new TimedUnit { Text = text, StartTime = start, EndTime = end };

        #region The rule

        /// <summary>
        /// "apple" 2|3 over [0, 1000]: the boundary is the cut cell's flat-ramp target, 400, the
        /// split is written as [2], and the targets stay 0/200/400/600/800. WITHOUT the split the
        /// boundary alone spreads the cells evenly by index (0/160/320/520/760), which puts cell 2
        /// 80 ms before its own group opens: the reason split_chars is always written.
        /// </summary>
        [Test]
        public void TheAppleCase()
        {
            var imported = ImportSyllables.Apply(unit("apple", 0, 1000));

            Assert.That(imported.SyllableBoundaries, Is.EqualTo(new[] { 400d }));
            Assert.That(imported.SyllableSplits, Is.EqualTo(new[] { 2 }));

            var line = TypingLine.FromLyricLine(lineOf(imported));
            Assert.That(line.Cells.Select(c => c.TargetTime), Is.EqualTo(new[] { 0d, 200, 400, 600, 800 }).Within(1e-9));
            Assert.That(line.Syllables, Is.EqualTo(new[] { new SyllableGroup(0, 2, 0, 400), new SyllableGroup(2, 5, 400, 1000) }));

            var withoutSplit = new TimedUnit { Text = "apple", StartTime = 0, EndTime = 1000, SyllableBoundaries = imported.SyllableBoundaries };
            var spread = TypingLine.FromLyricLine(lineOf(withoutSplit));
            Assert.That(spread.Cells.Select(c => c.TargetTime), Is.EqualTo(new[] { 0d, 160, 320, 520, 760 }).Within(1e-9), "the even index spread");
            Assert.That(spread.Cells[2].TargetTime, Is.LessThan(spread.Syllables[1].StartTime), "cell 2 sits before its own group");
        }

        [Test]
        public void WhatThePassLeavesAlone()
        {
            Assert.Multiple(() =>
            {
                Assert.That(ImportSyllables.NaturalSubdivision("go", 0, 1000), Is.Null, "one syllable");
                Assert.That(ImportSyllables.NaturalSubdivision("heyyyyy", 0, 1000), Is.Null, "a stylised spelling stays one ungrouped word (CHOICE A)");
                Assert.That(ImportSyllables.NaturalSubdivision("apple", 1000, 1000), Is.Null, "no span to cut");

                var subdivided = new TimedUnit { Text = "apple", StartTime = 0, EndTime = 1000, SyllableBoundaries = new[] { 300d } };
                Assert.That(ImportSyllables.Apply(subdivided), Is.SameAs(subdivided), "an aligner's or a pipe's subdivision is the source's own");

                var paused = new TimedUnit { Text = "apple", StartTime = 0, EndTime = 1000, Pauses = new[] { new WordPause(300, 500, 2) } };
                Assert.That(ImportSyllables.Apply(paused), Is.SameAs(paused), "and so is a pause");

                var monosyllables = lineOf(unit("go", 0, 500), unit("now", 500, 1000));
                Assert.That(ImportSyllables.Apply(new[] { monosyllables })[0], Is.SameAs(monosyllables), "a line with nothing to cut is the same object");
            });
        }

        #endregion

        #region It reproduces the stored era's groups

        /// <summary>
        /// The real LRC fixture through the PRODUCTION synthesis and loader: every imported line's live
        /// (authored) grouping equals the stored era's natural grouping of the same line as parsed
        /// without the pass, groups and cut cells EXACTLY, every other target within 1e-9 ms. So the
        /// import moves nothing a player was judged on; it only makes the cut visible and editable.
        /// </summary>
        [Test]
        public void ARealLrcImportReproducesTheNaturalGroupsExactly()
        {
            string lyrics = File.ReadAllText(StandaloneMaps.Require("Friday Pilots Club - Spectator", "lyrics.txt"));

            string? timing = LyricMapImporter.SynthesizeTimingJsonFromLrc(lyrics);
            Assert.That(timing, Is.Not.Null);
            Assert.That(TimingJsonLoader.TryParse(timing!, out IReadOnlyList<LyricLine> imported), Is.True);

            var parsed = LrcParser.Parse(lyrics);
            Assert.That(imported.Count, Is.EqualTo(parsed.Count));

            int subdividedWords = 0;

            for (int i = 0; i < parsed.Count; i++)
            {
                var before = TypingLine.FromLyricLine(parsed[i]);
                var after = TypingLine.FromLyricLine(imported[i]);

                Assert.That(after.DisplayText, Is.EqualTo(before.DisplayText), $"line {i}");
                Assert.That(after.Syllables, Is.EqualTo(before.NaturalGrouping.Groups), $"line {i}: the live groups are the stored era's, exactly");
                Assert.That(after.SyllableMarkerCells, Is.EqualTo(before.NaturalGrouping.MarkerCells), $"line {i}: marks");

                for (int c = 0; c < before.Cells.Count; c++)
                {
                    bool cut = before.NaturalGrouping.Groups.Any(g => g.StartCell == c);

                    if (cut)
                        Assert.That(after.Cells[c].TargetTime, Is.EqualTo(before.Cells[c].TargetTime), $"line {i} cut cell {c}: exact");
                    else
                        Assert.That(after.Cells[c].TargetTime, Is.EqualTo(before.Cells[c].TargetTime).Within(1e-9), $"line {i} cell {c}");
                }

                subdividedWords += imported[i].Units.Count(u => u.SyllableBoundaries.Count > 0);
            }

            Assert.That(subdividedWords, Is.GreaterThan(0), "the fixture has polysyllabic words to cut");
        }

        /// <summary>
        /// CHOICE B: a pipe-free LRC import comes out syllabified, which promotes the map from Line to
        /// Syllable granularity with explicit word units on the lines that carry a cut.
        /// </summary>
        [Test]
        public void APipeFreeLrcImportIsPromotedToSyllableGranularity()
        {
            string? timing = LyricMapImporter.SynthesizeTimingJsonFromLrc("[00:01.00]never gonna give you up\n[00:04.00]so we go\n");
            Assert.That(TimingJsonLoader.TryParse(timing!, out IReadOnlyList<LyricLine> lines), Is.True);

            Assert.That(TypeBeatEditorOperations.InferGranularity(lines), Is.EqualTo(TimingGranularity.Syllable));
            Assert.That(lines[0].Units[0].SyllableSplits, Is.EqualTo(Syllabifier.SplitPoints("never")));
            Assert.That(lines[0].Units.All(u => u.Source == TimingSource.Explicit), Is.True, "the cut line writes its words");
            Assert.That(lines[1].Units.All(u => u.SyllableBoundaries.Count == 0), Is.True, "a line of monosyllables is untouched");

            using var document = JsonDocument.Parse(timing!);
            var line0 = document.RootElement.GetProperty("lines")[0];
            Assert.That(line0.GetProperty("words")[0].GetProperty("split_chars").GetArrayLength(), Is.GreaterThan(0), "split_chars written");
            Assert.That(document.RootElement.GetProperty("lines")[1].TryGetProperty("words", out _), Is.False, "the plain line keeps the line shape");
        }

        /// <summary>
        /// The aligner document is edited in place: a monosyllable written as one syllable object
        /// loads as no boundary and is cut where it has more than one syllable, the aligner's own
        /// subdivision is kept, and everything else the document carries survives verbatim.
        /// </summary>
        [Test]
        public void TheAlignerDocumentPassKeepsWhatTheAlignerWrote()
        {
            const string json = "{\"version\":2,\"song_end_ms\":9000,\"lines\":[{\"text\":\"never gonna\",\"start_ms\":1000,\"end_ms\":3000,\"estimated\":false,\"words\":["
                                + "{\"text\":\"never\",\"start_ms\":1000,\"end_ms\":2000,\"score\":0.75,\"syllables\":[{\"text\":\"never\",\"start_ms\":1000,\"end_ms\":2000}]},"
                                + "{\"text\":\"gonna\",\"start_ms\":2000,\"end_ms\":3000,\"score\":0.5,\"syllables\":[{\"text\":\"go\",\"start_ms\":2000,\"end_ms\":2300},{\"text\":\"nna\",\"start_ms\":2300,\"end_ms\":3000}]}]}]}";

            string after = ImportSyllables.ApplyToTimingJson(json);
            Assert.That(TimingJsonLoader.TryParse(after, out IReadOnlyList<LyricLine> lines), Is.True);

            var never = lines[0].Units[0];
            var natural = ImportSyllables.NaturalSubdivision("never", 1000, 2000)!.Value;
            Assert.That(never.SyllableBoundaries, Is.EqualTo(natural.Boundaries));
            Assert.That(never.SyllableSplits, Is.EqualTo(natural.Splits));
            Assert.That(never.Confidence, Is.EqualTo(0.75));

            Assert.That(lines[0].Units[1].SyllableBoundaries, Is.EqualTo(new[] { 2300d }), "the aligner's own cut is kept");

            Assert.That(ImportSyllables.ApplyToTimingJson(after), Is.EqualTo(after), "idempotent");

            const string nothing = "{\"version\":2,\"lines\":[{\"text\":\"go\",\"start_ms\":0,\"end_ms\":500,\"words\":[{\"text\":\"go\",\"start_ms\":0,\"end_ms\":500}]}]}";
            Assert.That(ImportSyllables.ApplyToTimingJson(nothing), Is.SameAs(nothing), "nothing to cut: returned verbatim");
            Assert.That(ImportSyllables.ApplyToTimingJson("not json"), Is.EqualTo("not json"));
        }

        #endregion

        #region A stored map is never rewritten

        /// <summary>
        /// A PACKAGE map (an .osz/.osu import, an online download: neither goes near the import
        /// synthesis) keeps every unsubdivided word exactly as stored. Its .osu decodes with no
        /// subdivision invented, and re-encodes byte for byte, so its gameplay fingerprint and a
        /// ranked set's status are untouched; only the engine's grouping of it changed.
        /// </summary>
        [Test]
        public void APackageMapLoadsAndReEncodesByteForByte()
        {
            var lines = new[]
            {
                new LyricLine
                {
                    RawText = "never gonna give",
                    StartTime = 1000,
                    EndTime = 5000,
                    SingEndTime = 3600,
                    Units = new[]
                    {
                        new TimedUnit { Text = "never", StartTime = 1000, EndTime = 2000, Source = TimingSource.Explicit },
                        new TimedUnit { Text = "gonna", StartTime = 2000, EndTime = 3000, Source = TimingSource.Explicit },
                        new TimedUnit { Text = "give", StartTime = 3000, EndTime = 3600, Source = TimingSource.Explicit },
                    },
                },
            };

            string stored = OriginalTextFormatTest.Encode(OriginalTextFormatTest.BuildBeatmap(lines));
            var decoded = OriginalTextFormatTest.Decode(stored);
            var line = decoded.HitObjects.OfType<TypeBeatHitObject>().Single().Line;

            Assert.That(line.Units.All(u => u.SyllableBoundaries.Count == 0 && u.SyllableSplits.Count == 0), Is.True, "nothing is syllabified on load");
            Assert.That(OriginalTextFormatTest.Encode(decoded), Is.EqualTo(stored), "byte for byte");
            Assert.That(stored, Does.Not.Contain("split_chars"));
        }

        #endregion
    }
}
