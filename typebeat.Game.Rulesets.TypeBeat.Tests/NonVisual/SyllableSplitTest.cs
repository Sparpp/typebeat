// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The AUTHORED syllable split (backlog 181): a per-word character cut ("ap|ple") that a mapper
    /// can hand-author beside the subdivision TIMES, persisted as the word-level
    /// <c>split_chars</c> array.
    ///
    /// <para>Two pins run through this fixture and matter more than any single case:</para>
    /// <list type="number">
    /// <item>A map carrying NO authored split flattens EXACTLY as it did before the feature: same
    /// per-char targets, same groups. The expected numbers below are written out longhand for that
    /// reason, so a change to the derived arm fails here rather than silently retiming every
    /// existing subtimed map.</item>
    /// <item>On an AUTHORED word the two readings agree by construction: the same cut feeds the
    /// per-char targets and the judgement groups, so every cell's target lies inside its own
    /// syllable's span. The derived arm does NOT promise that, and one case here shows it failing,
    /// which is the whole reason authoring is worth having.</item>
    /// </list>
    /// </summary>
    [TestFixture]
    public class SyllableSplitTest
    {
        #region SyllableSegments: the shared derivation both gameplay and the editor read

        [TestCase("apple", 2, new[] { 2 }, ExpectedResult = true)]
        [TestCase("apple", 2, new[] { 3 }, ExpectedResult = true, Description = "any interior index is authorable")]
        [TestCase("apple", 2, new int[0], ExpectedResult = false, Description = "empty IS derived, not authored")]
        [TestCase("apple", 2, new[] { 2, 3 }, ExpectedResult = false, Description = "one boundary wants one split")]
        [TestCase("apple", 3, new[] { 2 }, ExpectedResult = false, Description = "two boundaries want two splits")]
        [TestCase("apple", 2, new[] { 0 }, ExpectedResult = false, Description = "would empty the first segment")]
        [TestCase("apple", 2, new[] { 5 }, ExpectedResult = false, Description = "would empty the last segment")]
        [TestCase("apple", 2, new[] { 6 }, ExpectedResult = false, Description = "past the end of the word")]
        [TestCase("apple", 2, new[] { -1 }, ExpectedResult = false)]
        [TestCase("banana", 3, new[] { 4, 2 }, ExpectedResult = false, Description = "not ascending")]
        [TestCase("banana", 3, new[] { 2, 2 }, ExpectedResult = false, Description = "duplicate would empty a segment")]
        [TestCase("banana", 3, new[] { 2, 4 }, ExpectedResult = true)]
        [TestCase("", 2, new[] { 1 }, ExpectedResult = false)]
        [TestCase("ab", 1, new[] { 1 }, ExpectedResult = false, Description = "an undivided word has no split")]
        public bool AuthoredValidity(string token, int segments, int[] authored)
            => SyllableSegments.IsAuthoredValid(token, segments, authored);

        [Test]
        public void SplitsForPrefersAuthoredAndFallsBackToDerived()
        {
            // "apple" happens to derive the split the feature is named after, so the fallback is
            // invisible on it; the authored value is what proves which arm ran.
            Assert.That(SyllableSegments.Derived("apple", 2), Is.EqualTo(new[] { 2 }));

            Assert.That(SyllableSegments.SplitsFor("apple", 2, new[] { 3 }), Is.EqualTo(new[] { 3 }), "authored wins");
            Assert.That(SyllableSegments.SplitsFor("apple", 2, Array.Empty<int>()), Is.EqualTo(new[] { 2 }), "empty derives");
            Assert.That(SyllableSegments.SplitsFor("apple", 2, new[] { 9 }), Is.EqualTo(new[] { 2 }), "invalid derives, never throws");
            Assert.That(SyllableSegments.SplitsFor("apple", 2, new[] { 1, 3 }), Is.EqualTo(new[] { 2 }), "wrong count derives");
        }

        [Test]
        public void SegmentTextsCutTheWordAndConcatenateBack()
        {
            Assert.That(SyllableSegments.SegmentTexts("apple", new[] { 2 }), Is.EqualTo(new[] { "ap", "ple" }));
            Assert.That(SyllableSegments.SegmentTexts("apple", new[] { 3 }), Is.EqualTo(new[] { "app", "le" }));
            Assert.That(SyllableSegments.SegmentTexts("banana", new[] { 2, 4 }), Is.EqualTo(new[] { "ba", "na", "na" }));
            Assert.That(SyllableSegments.SegmentTexts("apple", Array.Empty<int>()), Is.EqualTo(new[] { "apple" }));

            foreach (string word in new[] { "apple", "banana", "don't", "b2b" })
            {
                var splits = SyllableSegments.Derived(word, 2);
                Assert.That(string.Concat(SyllableSegments.SegmentTexts(word, splits)), Is.EqualTo(word), word);
            }
        }

        [Test]
        public void CellCutsCountCellsNotCharacters()
        {
            // Punctuation is not a CELL, so it takes no slot in the per-word char spread; the cut
            // has to be expressed in the same units or an apostrophe would shift every target.
            Assert.That(SyllableSegments.CellCuts("apple", new[] { 2 }), Is.EqualTo(new[] { 0, 2, 5 }));
            Assert.That(SyllableSegments.CellCuts("don't", new[] { 3 }), Is.EqualTo(new[] { 0, 3, 4 }));
            Assert.That(SyllableSegments.CellCuts("apple", Array.Empty<int>()), Is.EqualTo(new[] { 0, 5 }));

            // A segment holding only a hyphen owns no cell at all; its cut repeats, and SegmentOf
            // then hands the next cell to the LATER segment, exactly as the group walk does.
            int[] cuts = SyllableSegments.CellCuts("a-b", new[] { 1, 2 });
            Assert.That(cuts, Is.EqualTo(new[] { 0, 1, 1, 2 }));
            Assert.That(SyllableSegments.SegmentOf(cuts, 0), Is.EqualTo(0));
            Assert.That(SyllableSegments.SegmentOf(cuts, 1), Is.EqualTo(2));
        }

        #endregion

        #region Gameplay: the un-authored pin, then the authored one

        /// <summary>
        /// THE regression pin. A subtimed word with no authored split keeps the index-even char
        /// spread and the syllabifier's forced grouping, both written out longhand: "banana" over
        /// 1000..1600 with boundaries at 1200 and 1400 is 6 chars across 3 equal segments.
        /// </summary>
        [Test]
        public void UnauthoredWordIsUnchanged()
        {
            var line = subtimedLine("banana", 1000, 1600, Array.Empty<int>(), 1200, 1400);

            Assert.That(targetsOf(line), Is.EqualTo(new[] { 1000d, 1100, 1200, 1300, 1400, 1500 }).Within(1e-9));
            Assert.That(groupsOf(line), Is.EqualTo(new[]
            {
                (0, 2, 1000d, 1200d),
                (2, 4, 1200d, 1400d),
                (4, 6, 1400d, 1600d),
            }));
        }

        /// <summary>
        /// An authored split moves BOTH readings together: "ban|a|na" gives the first syllable
        /// three chars and the second one, and the groups agree.
        /// </summary>
        [Test]
        public void AuthoredSplitDrivesTargetsAndGroupsTogether()
        {
            var line = subtimedLine("banana", 1000, 1600, new[] { 3, 4 }, 1200, 1400);

            Assert.That(targetsOf(line), Is.EqualTo(new[]
            {
                1000d,
                1000 + 200 / 3d,
                1000 + 400 / 3d,
                1200,
                1400,
                1500,
            }).Within(1e-9));

            Assert.That(groupsOf(line), Is.EqualTo(new[]
            {
                (0, 3, 1000d, 1200d),
                (3, 4, 1200d, 1400d),
                (4, 6, 1400d, 1600d),
            }));
        }

        /// <summary>
        /// Authoring a split that HAPPENS to equal the even cut changes nothing, which is what lets
        /// the editor canonicalise such a split away without moving the map.
        /// </summary>
        [Test]
        public void AuthoringTheEvenCutIsANoOp()
        {
            var authored = subtimedLine("banana", 1000, 1600, new[] { 2, 4 }, 1200, 1400);
            var derived = subtimedLine("banana", 1000, 1600, Array.Empty<int>(), 1200, 1400);

            Assert.That(targetsOf(authored), Is.EqualTo(targetsOf(derived)).Within(1e-9));
            Assert.That(groupsOf(authored), Is.EqualTo(groupsOf(derived)));
        }

        /// <summary>
        /// Every cell of an AUTHORED word is judged by a group whose span contains its own target.
        /// The derived arm makes no such promise and "beautiful" is a live counter-example: split
        /// "beau|tiful" groups the 't' with the second syllable while the even spread times it
        /// before the boundary. Authoring the SAME cut is what closes the gap.
        /// </summary>
        [Test]
        public void AuthoredWordPutsEveryTargetInsideItsOwnSyllable()
        {
            Assert.That(SyllableSegments.Derived("beautiful", 2), Is.EqualTo(new[] { 4 }), "the split both arms group on");

            var derived = subtimedLine("beautiful", 1000, 1900, Array.Empty<int>(), 1450);
            var authored = subtimedLine("beautiful", 1000, 1900, new[] { 4 }, 1450);

            // 't' (cell 4) opens the second syllable in BOTH, but only the authored arm times it there.
            Assert.That(derived.Cells[4].TargetTime, Is.EqualTo(1400).Within(1e-9));
            Assert.That(authored.Cells[4].TargetTime, Is.EqualTo(1450).Within(1e-9));
            Assert.That(derived.SyllableIndexOf(4), Is.EqualTo(1));
            Assert.That(authored.SyllableIndexOf(4), Is.EqualTo(1));

            Assert.That(outsideOwnSyllable(derived), Is.Not.Empty, "the derived arm can and does miss");
            Assert.That(outsideOwnSyllable(authored), Is.Empty);
        }

        [Test]
        public void AuthoredPinHoldsThroughPunctuationAndLiterate()
        {
            // "singin'" derives one split; author the other side of the 'n' so the apostrophe has
            // to ride inside a segment that the cell cut never counted.
            var line = LyricLineOf("singin'", 1000, 2000, unit("singin'", 1000, 1700, new[] { 4 }, 1350));

            foreach (bool literate in new[] { false, true })
            {
                var typing = TypingLine.FromLyricLine(line, TimingGranularity.Syllable, literate);
                Assert.That(outsideOwnSyllable(typing), Is.Empty, literate ? "literate" : "default");
            }
        }

        #endregion

        #region timing.json: the additive field and its validation matrix

        [Test]
        public void SplitCharsRoundTripThroughTheLoader()
        {
            var units = load(oneLine("banana", 1000, 1900, word("banana", 1000, 1600, new[] { 1200d, 1400 }, new[] { 3, 4 })));

            Assert.That(units[0].SyllableBoundaries, Is.EqualTo(new[] { 1200d, 1400 }));
            Assert.That(units[0].SyllableSplits, Is.EqualTo(new[] { 3, 4 }));
        }

        [Test]
        public void EveryMapWithoutTheFieldStaysDerived()
        {
            var units = load(oneLine("banana", 1000, 1900, word("banana", 1000, 1600, new[] { 1200d, 1400 }, null)));

            Assert.That(units[0].SyllableBoundaries, Is.EqualTo(new[] { 1200d, 1400 }));
            Assert.That(units[0].SyllableSplits, Is.Empty);
        }

        [TestCase(new[] { 3 }, TestName = "SplitCharsRejected_TooFew")]
        [TestCase(new[] { 2, 3, 4 }, TestName = "SplitCharsRejected_TooMany")]
        [TestCase(new[] { 4, 2 }, TestName = "SplitCharsRejected_NotAscending")]
        [TestCase(new[] { 2, 2 }, TestName = "SplitCharsRejected_Duplicate")]
        [TestCase(new[] { 0, 4 }, TestName = "SplitCharsRejected_EmptiesFirstSegment")]
        [TestCase(new[] { 2, 6 }, TestName = "SplitCharsRejected_EmptiesLastSegment")]
        [TestCase(new[] { 2, 99 }, TestName = "SplitCharsRejected_OutOfRange")]
        [TestCase(new[] { -1, 4 }, TestName = "SplitCharsRejected_Negative")]
        public void SplitCharsRejected(int[] splits)
        {
            var units = load(oneLine("banana", 1000, 1900, word("banana", 1000, 1600, new[] { 1200d, 1400 }, splits)));

            Assert.That(units[0].SyllableBoundaries, Is.EqualTo(new[] { 1200d, 1400 }), "the TIMES are unaffected either way");
            Assert.That(units[0].SyllableSplits, Is.Empty);
        }

        /// <summary>
        /// The subtle one: clamping can DROP a boundary, and a split list that still matches the
        /// SURVIVING count would then pair every split with the wrong segment. Word 2 starts before
        /// word 1 ends, so its start is pushed to 1600 and its 1250 subdivision dies with it; the
        /// one remaining boundary must not adopt the authored split written for two.
        /// </summary>
        [Test]
        public void SplitCharsDroppedWhenClampingLostABoundary()
        {
            string json = oneLine("banana orange", 1000, 2000,
                word("banana", 1000, 1600, new[] { 1300d }, null) + "," +
                word("orange", 1200, 1800, new[] { 1250d, 1700 }, new[] { 3 }));

            var units = load(json);

            Assert.That(units[1].StartTime, Is.EqualTo(1600), "word 2 was pushed off word 1");
            Assert.That(units[1].SyllableBoundaries, Is.EqualTo(new[] { 1700d }), "one boundary survived");
            Assert.That(units[1].SyllableSplits, Is.Empty, "count matches the survivor, but a boundary was lost");
        }

        [Test]
        public void MalformedSplitCharsAreIgnoredNotFatal()
        {
            foreach (string raw in new[] { "\"split_chars\":\"nope\"", "\"split_chars\":[\"a\"]", "\"split_chars\":[]", "\"split_chars\":null" })
            {
                string json = "{\"version\":2,\"song_end_ms\":30000,\"lines\":[{\"text\":\"banana\",\"start_ms\":1000,\"end_ms\":1900,"
                              + "\"words\":[{\"text\":\"banana\",\"start_ms\":1000,\"end_ms\":1600,\"syllables\":[{\"start_ms\":1200}],"
                              + raw + "}]}]}";

                Assert.That(TimingJsonLoader.TryParse(json, out var lines), Is.True, raw);
                Assert.That(lines[0].Units[0].SyllableBoundaries, Is.EqualTo(new[] { 1200d }), raw);
                Assert.That(lines[0].Units[0].SyllableSplits, Is.Empty, raw);
            }
        }

        [Test]
        public void SplitCharsSurviveEncodeThenDecode()
        {
            var line = LyricLineOf("banana orange", 1000, 2000,
                unit("banana", 1000, 1600, new[] { 3, 4 }, 1200, 1400),
                unit("orange", 1600, 1900, Array.Empty<int>(), 1750));

            string osu = EncodeOsu(line);

            Assert.That(osu, Does.Contain("\"split_chars\":[3,4]"));
            Assert.That(osu, Does.Contain("\"ban\""), "the cosmetic syllable text follows the authored cut");
            Assert.That(osu, Does.Not.Contain("\"split_chars\":[]"), "a derived word writes no field at all");

            var reloaded = DecodeOsu(osu)[0].Line.Units;
            Assert.That(reloaded[0].SyllableBoundaries, Is.EqualTo(new[] { 1200d, 1400 }));
            Assert.That(reloaded[0].SyllableSplits, Is.EqualTo(new[] { 3, 4 }));
            Assert.That(reloaded[1].SyllableSplits, Is.Empty);
        }

        /// <summary>
        /// The cosmetic per-syllable <c>text</c> of a DERIVED word keeps the even-halves default it
        /// has always had, so re-saving a pre-181 map does not move a single byte of its words[].
        /// </summary>
        [Test]
        public void DerivedWordKeepsTheEvenCosmeticText()
        {
            string osu = EncodeOsu(LyricLineOf("banana", 1000, 2000, unit("banana", 1000, 1600, Array.Empty<int>(), 1200, 1400)));

            Assert.That(osu, Does.Contain("\"ba\""));
            Assert.That(osu, Does.Contain("\"na\""));
            Assert.That(osu, Does.Not.Contain("split_chars"));
        }

        #endregion

        #region helpers

        private static TimedUnit unit(string text, double start, double end, IReadOnlyList<int> splits, params double[] boundaries)
            => new TimedUnit
            {
                Text = text,
                StartTime = start,
                EndTime = end,
                Source = TimingSource.Explicit,
                SyllableBoundaries = boundaries,
                SyllableSplits = splits,
            };

        internal static LyricLine LyricLineOf(string text, double start, double end, params TimedUnit[] units)
            => new LyricLine
            {
                RawText = text,
                StartTime = start,
                EndTime = end,
                SingEndTime = units.Length > 0 ? units[^1].EndTime : end,
                Units = units,
            };

        private static TypingLine subtimedLine(string word, double start, double end, IReadOnlyList<int> splits, params double[] boundaries)
            => TypingLine.FromLyricLine(LyricLineOf(word, start, end + 400, unit(word, start, end, splits, boundaries)), TimingGranularity.Syllable);

        private static double[] targetsOf(TypingLine line) => line.Cells.Select(c => c.TargetTime).ToArray();

        private static (int, int, double, double)[] groupsOf(TypingLine line)
            => line.Syllables.Select(g => (g.StartCell, g.EndCellExclusive, g.StartTime, g.EndTime)).ToArray();

        /// <summary>Cells whose target falls outside the span of the group that judges them.</summary>
        private static List<int> outsideOwnSyllable(TypingLine line)
        {
            var bad = new List<int>();

            for (int i = 0; i < line.Cells.Count; i++)
            {
                int g = line.SyllableIndexOf(i);

                if (g < 0)
                    continue;

                var group = line.Syllables[g];

                if (line.Cells[i].TargetTime < group.StartTime - 1e-9 || line.Cells[i].TargetTime > group.EndTime + 1e-9)
                    bad.Add(i);
            }

            return bad;
        }

        private static string num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        private static string word(string text, double start, double end, double[] syllableStarts, int[]? splits)
        {
            string syllables = syllableStarts.Length == 0
                ? string.Empty
                : ",\"syllables\":[" + string.Join(",", syllableStarts.Select(s => $"{{\"start_ms\":{num(s)}}}")) + "]";

            string splitChars = splits == null ? string.Empty : ",\"split_chars\":[" + string.Join(",", splits) + "]";

            return $"{{\"text\":\"{text}\",\"start_ms\":{num(start)},\"end_ms\":{num(end)}{syllables}{splitChars}}}";
        }

        private static string oneLine(string text, double start, double end, string words)
            => $"{{\"version\":2,\"song_end_ms\":30000,\"lines\":[{{\"text\":\"{text}\",\"start_ms\":{num(start)},\"end_ms\":{num(end)},\"words\":[{words}]}}]}}";

        private static IReadOnlyList<TimedUnit> load(string json)
        {
            Assert.That(TimingJsonLoader.TryParse(json, out var lines), Is.True, "the fixture must parse");
            return lines[0].Units;
        }

        /// <summary>The .osu the editor's encoder writes for a beatmap of one line.</summary>
        internal static string EncodeOsu(params LyricLine[] lines)
        {
            var beatmap = new typebeat.Game.Beatmaps.Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.Metadata.Artist = "a";
            beatmap.Metadata.Title = "t";
            beatmap.Metadata.AudioFile = "a.mp3";

            for (int i = 0; i < lines.Length; i++)
            {
                beatmap.HitObjects.Add(new TypeBeatHitObject
                {
                    StartTime = lines[i].StartTime,
                    LineIndex = i,
                    Line = lines[i],
                    Granularity = TimingGranularity.Syllable,
                });
            }

            var sb = new System.Text.StringBuilder();

            using (var writer = new System.IO.StringWriter(sb))
                TypeBeatBeatmapEncoder.Encode(beatmap, writer);

            return sb.ToString();
        }

        /// <summary>Decodes an encoded .osu back through the production decoder.</summary>
        internal static List<TypeBeatHitObject> DecodeOsu(string osu)
        {
            LyricBeatmapDecoder.Register();

            using var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(osu));
            using var reader = new typebeat.Game.IO.LineBufferedReader(stream);
            var decoded = typebeat.Game.Beatmaps.Formats.Decoder.GetDecoder<typebeat.Game.Beatmaps.Beatmap>(reader).Decode(reader);

            return decoded.HitObjects.OfType<TypeBeatHitObject>().OrderBy(o => o.LineIndex).ToList();
        }

        #endregion
    }
}
