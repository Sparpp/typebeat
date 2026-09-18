// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// The port of the Star Rating Sandbox's CHUNKED ENDURANCE AXIS, pinned against the sandbox's own
// reading of the whole bundled catalogue.
//
// The arbiter is NonVisual/fixtures/chunked-catalogue.json, emitted by
// tools/star-rating-sandbox/emit-chunk-fixture.mjs: 89 maps, each with the chunk layout, the
// per-map reading, the merge and the star figure, plus the settings block it was taken at. This
// file compares the SETTINGS FIRST, because a fixture taken at other dials says nothing about the
// port; only then does it compare every map field.
//
// Two things about the snapshot are worth stating, because they are what makes the comparison
// possible rather than a matter of taste:
//
//   * songs.json carries NO typability block, so the sandbox's typabilityFor() returned null and the
//     fixture was emitted with every line read at z = 0. This port therefore hands the axis
//     LyricDifficulty.NoScores, which is that same snapshot. The in-client index is a later step;
//     until then the axis is only ever run with the shipped index behind the non-default axis switch,
//     which nothing in the game passes.
//   * the sandbox's `span` (the floor on a word's sung span) is 30 ms at these settings, not the
//     envelope model's 50 ms. That is a dial of the axis, and the maps that carry sub-50 ms words
//     (Hardware Store, Guns and Ships, My Regards, Twilight Learned to Fly) are what pin it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class ChunkedEnduranceTest
    {
        /// <summary>The maps the fixture and the catalogue must agree on.</summary>
        private const int catalogue_size = 89;

        /// <summary>
        /// How much slack a float comparison against the sandbox's own double is allowed. The two
        /// implementations run the same arithmetic but not the same transcendentals (V8's Math.exp and
        /// Math.pow against .NET's), so this is a ULP-scale allowance rather than a modelling one:
        /// measured over the whole catalogue the largest disagreement is ONE ULP (1.1e-16, on one
        /// map's score), and this pin leaves four orders of headroom on that while staying six orders
        /// tighter than any change to the model itself.
        /// </summary>
        private const double float_slack = 1e-12;

        private static string repoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "typebeat.sln")))
                dir = dir.Parent;

            Assert.That(dir, Is.Not.Null, "could not locate the repository root (no typebeat.sln above the test output directory)");
            return dir!.FullName;
        }

        private static string fixturePath() => Path.Combine(repoRoot(), "typebeat.Game.Rulesets.TypeBeat.Tests", "NonVisual", "fixtures", "chunked-catalogue.json");

        private static string cataloguePath()
            => SandboxFixtures.RequireSandbox(Path.Combine(repoRoot(), "tools", "star-rating-sandbox"), "songs.json");

        private static string configPath()
            => SandboxFixtures.RequireSandbox(Path.Combine(repoRoot(), "tools", "star-rating-sandbox"), "sr-config.json");

        private static JsonDocument fixture() => JsonDocument.Parse(File.ReadAllText(fixturePath()));

        private static JsonDocument catalogue() => JsonDocument.Parse(File.ReadAllText(cataloguePath()));

        /// <summary>
        /// The maps of the bundled catalogue, as the difficulty model reads them. The units are the
        /// sandbox's own shape: one per whitespace token, with its syllables' own spans when the
        /// importer kept them (the game stores the INTERNAL boundaries, i.e. the start of every
        /// syllable but the first).
        /// </summary>
        private static IReadOnlyList<LyricLine> lyricLines(JsonElement map)
        {
            var lines = new List<LyricLine>();

            foreach (JsonElement line in map.GetProperty("lines").EnumerateArray())
            {
                string text = line.GetProperty("text").GetString() ?? string.Empty;
                string[] tokens = text.Split(' ');
                var units = new List<TimedUnit>();
                int index = 0;

                foreach (JsonElement unit in line.GetProperty("units").EnumerateArray())
                {
                    var boundaries = new List<double>();

                    if (unit.TryGetProperty("syllables", out JsonElement syllables))
                    {
                        int syllable = 0;

                        foreach (JsonElement span in syllables.EnumerateArray())
                        {
                            if (syllable > 0)
                                boundaries.Add(span.GetProperty("start").GetDouble());

                            syllable++;
                        }
                    }

                    units.Add(new TimedUnit
                    {
                        Text = index < tokens.Length ? tokens[index] : string.Empty,
                        StartTime = unit.GetProperty("start").GetDouble(),
                        EndTime = unit.GetProperty("end").GetDouble(),
                        SyllableBoundaries = boundaries,
                    });
                    index++;
                }

                double start = line.GetProperty("start").GetDouble();
                double end = line.GetProperty("end").GetDouble();

                lines.Add(new LyricLine
                {
                    RawText = text,
                    StartTime = start,
                    EndTime = end,
                    SingEndTime = line.TryGetProperty("singEnd", out JsonElement singEnd) ? singEnd.GetDouble() : end,
                    Units = units,
                    Estimated = line.TryGetProperty("estimated", out JsonElement estimated) && estimated.GetBoolean(),
                });
            }

            return lines;
        }

        private static ChunkedEndurance.Report rate(JsonElement map, ChunkedEndurance.Settings settings)
            => LyricDifficulty.RateChunked(lyricLines(map), 1, false, LyricDifficulty.NoScores, settings).Report;

        private static void close(double actual, JsonElement expected, string key, string name)
        {
            double want = expected.GetProperty(key).GetDouble();
            double slack = float_slack * Math.Max(1, Math.Abs(want));

            Assert.That(actual, Is.EqualTo(want).Within(slack), $"{name}: {key} disagrees with the sandbox (wanted {want:R}, got {actual:R})");
        }

        private static void exact(double actual, JsonElement expected, string key, string name)
            => Assert.That(actual, Is.EqualTo(expected.GetProperty(key).GetInt32()), $"{name}: {key}");

        /// <summary>
        /// THE SETTINGS BLOCK FIRST. Every dial the fixture records and this axis reads has to be the
        /// live one, and the dials the fixture does NOT record (the word-span floor, the freestyle
        /// cost, the scan's tolerance and horizon, the boost's saturation) are checked against
        /// sr-config.json instead. A failure here means the fixture is stale and wants regenerating,
        /// not that the port is wrong.
        /// </summary>
        [Test]
        public void FixtureWasTakenAtTheDialsThisAxisRuns()
        {
            ChunkedEndurance.Settings live = ChunkedEndurance.Live;
            using JsonDocument fixtureDocument = fixture();
            JsonElement settings = fixtureDocument.RootElement.GetProperty("settings");

            var dials = new (string Key, double Value)[]
            {
                ("anchor", live.anchor),
                ("base", live.capability_base_wpm),
                ("burst", live.capability_burst_wpm),
                ("reference", live.capability_ref_seconds),
                ("exponent", live.capability_exponent),
                ("chars", live.chars),
                ("bin", live.bin_ms),
                ("windowDensityBonus", live.window_density_bonus),
                ("typabilityStrength", live.typability_strength),
                ("complexityStrength", live.complexity_strength),
                ("chunkSeconds", live.chunk_seconds),
                ("chunkAdaptiveRange", live.chunk_adaptive_range),
                ("chunkDecay", live.chunk_decay),
                ("chunkDecayPower", live.chunk_decay_power),
                ("chunkLengthBonus", live.chunk_length_bonus),
                ("chunkLengthFalloff", live.chunk_length_falloff),
                ("chunkLengthFloor", live.chunk_length_floor),
                ("chunkLengthScale", live.chunk_length_scale),
            };

            foreach ((string key, double value) in dials)
                Assert.That(settings.GetProperty(key).GetDouble(), Is.EqualTo(value), $"the fixture's {key} is not the dial the chunked axis runs");

            Assert.That(settings.GetProperty("enduranceAxis").GetString(), Is.EqualTo("chunked"), "the fixture was not taken on the chunked axis");
            Assert.That(settings.GetProperty("modelMode").GetString(), Is.EqualTo("combined"), "the fixture was not taken on the combined model");
            Assert.That(settings.GetProperty("chunkSizing").GetString(), Is.EqualTo(live.chunk_sizing));
            Assert.That(settings.GetProperty("chunkMergeMode").GetString(), Is.EqualTo(live.chunk_merge_mode));
            Assert.That(settings.GetProperty("rate").GetDouble(), Is.EqualTo(1), "the fixture was taken at a rate mod");
            Assert.That(settings.GetProperty("literate").GetBoolean(), Is.False, "the fixture was taken under the Literate mod");

            // `range`, `minimum`, `minimumChars`, `speedMode` and `complexityMethod` are the envelope
            // path's own dials and this axis reads none of them. `range` is recorded rather than
            // asserted against a value here because the chunked star scale is the ANCHOR and never
            // applies a range at all; it is checked only so a fixture taken at a nonzero range is
            // noticed.
            Assert.That(settings.GetProperty("range").GetDouble(), Is.EqualTo(0), "the fixture's range moved - this axis ignores it, so say so explicitly if that is intended");

            // The dials the fixture's block does not carry, against the sandbox's own live config.
            using JsonDocument config = JsonDocument.Parse(File.ReadAllText(configPath()));
            JsonElement model = config.RootElement.GetProperty("model");
            JsonElement parameters = config.RootElement.GetProperty("parameters");
            JsonElement complexity = config.RootElement.GetProperty("complexity");
            JsonElement typability = config.RootElement.GetProperty("typability");

            Assert.That(model.GetProperty("enduranceAxis").GetString(), Is.EqualTo("chunked"));
            Assert.That(parameters.GetProperty("span").GetDouble(), Is.EqualTo(live.span_ms), "the word-span floor is a dial of this axis");
            Assert.That(parameters.GetProperty("freestyle").GetDouble(), Is.EqualTo(live.freestyle));
            Assert.That(model.GetProperty("boostScale").GetDouble(), Is.EqualTo(live.boost_scale), "the rhythm boost's saturation moved");
            Assert.That(complexity.GetProperty("complexityTolerance").GetDouble(), Is.EqualTo(live.complexity_tolerance), "the scan's Great window moved");
            Assert.That(complexity.GetProperty("complexityHorizon").GetInt32(), Is.EqualTo(live.complexity_horizon), "the scan's rolling distance moved");
            Assert.That(complexity.GetProperty("complexityMod").GetString(), Is.EqualTo("none"), "the scan reads the live syllable arm, so the config's judgement mod has to be None");
            Assert.That(typability.GetProperty("typabilityMinScoredFraction").GetDouble() / 100.0, Is.EqualTo(live.typability_min_scored_fraction));
            Assert.That(typability.GetProperty("typabilityMinCoverage").GetDouble(), Is.EqualTo(TypabilityIndex.MinCoverage));
        }

        /// <summary>
        /// EVERY MAP, EVERY FIELD the fixture records. The layout, the reading, the merge and the star
        /// figure are compared to the sandbox's own doubles; a mismatch in any of them is the two
        /// implementations having drifted.
        /// </summary>
        [Test]
        public void EveryCatalogueMapMatchesTheSandboxReading()
        {
            using JsonDocument fixtureDocument = fixture();
            var expected = new Dictionary<string, JsonElement>();

            foreach (JsonElement map in fixtureDocument.RootElement.GetProperty("maps").EnumerateArray())
                expected[map.GetProperty("id").GetString()!] = map;

            int compared = 0;

            using JsonDocument catalogueDocument = catalogue();

            foreach (JsonElement map in catalogueDocument.RootElement.GetProperty("maps").EnumerateArray())
            {
                string id = map.GetProperty("id").GetString()!;
                string name = map.GetProperty("name").GetString()!;

                if (!expected.TryGetValue(id, out JsonElement want))
                    Assert.Fail($"{id} is in the catalogue but not in the fixture - regenerate it with emit-chunk-fixture.mjs");

                ChunkedEndurance.Report report = rate(map, ChunkedEndurance.Live);

                exact(report.Count, want, "chunks", name);
                exact(report.Segments, want, "segments", name);
                exact(report.Pauses, want, "pauses", name);
                exact(report.Sealed, want, "sealed", name);
                close(report.ShortestSeconds, want, "shortestSeconds", name);
                close(report.LongestSeconds, want, "longestSeconds", name);
                close(report.Hardest, want, "hardest", name);
                close(report.Mean, want, "mean", name);
                close(report.Score, want, "score", name);
                close(report.ScoreNoRhythm, want, "scoreNoRhythm", name);
                close(report.LengthCharacters, want, "lengthCharacters", name);
                close(report.CountedCharacters, want, "countedCharacters", name);
                close(report.LengthBonusApplied, want, "lengthBonusApplied", name);
                close(report.Stars, want, "stars", name);
                close(report.RhythmMultiplier, want, "rhythmMultiplier", name);
                exact(report.Merged, want, "merged", name);
                exact(report.MergeRuns, want, "mergeRuns", name);
                exact(report.MergeLongestRun, want, "mergeLongestRun", name);

                compared++;
            }

            Assert.That(compared, Is.EqualTo(catalogue_size), "the catalogue moved under the fixture - regenerate the fixture");
            Assert.That(expected.Count, Is.EqualTo(catalogue_size), "the fixture carries maps the catalogue no longer has");
        }

        /// <summary>
        /// The axis is actually WIRED behind the switch: <see cref="LyricDifficulty.ComputeDetail"/>
        /// reads the chunked arm's own figure on the chunked axis, and the envelope's on the default
        /// one. The map is nonsense text so the shipped index cannot score it, which is the state the
        /// fixture's own snapshot is in - so the two paths are directly comparable.
        /// </summary>
        [Test]
        public void TheChunkedAxisIsWhatTheAxisSwitchSelects()
        {
            var lines = new[]
            {
                line(0, 2200, ("zzqx", 0, 700), ("wvvt", 700, 2200)),
                line(2200, 4600, ("qqzz", 2200, 3300), ("xwvv", 3300, 4600)),
            };

            Assert.That(TypabilityIndex.TryScore("zzqx wvvt", out _), Is.False, "the fixture text has to be outside the shipped index");

            LyricDifficulty.ModelResult shipped = LyricDifficulty.ComputeDetail(lines, 1, false);
            LyricDifficulty.ModelResult chunked = LyricDifficulty.ComputeDetail(lines, 1, false, LyricDifficulty.EnduranceAxis.Chunked);
            LyricDifficulty.ModelResult envelope = LyricDifficulty.ComputeDetail(lines, 1, false, LyricDifficulty.EnduranceAxis.Envelope);
            ChunkedEndurance.Report direct = LyricDifficulty.RateChunked(lines, 1, false, LyricDifficulty.ShippedScores, ChunkedEndurance.Live).Report;

            Assert.That(shipped.Stars, Is.EqualTo(chunked.Stars).Within(1e-12), "the DEFAULT has to be the axis the game ships");
            Assert.That(chunked.Stars, Is.EqualTo(direct.Stars).Within(1e-9), "the axis switch has to return the chunked arm's own figure");
            Assert.That(chunked.DifficultCharacters, Is.EqualTo(direct.LengthCharacters).Within(1e-9), "a miss on this axis is priced against the weighted length count");
            Assert.That(chunked.ComplexityMultiplier, Is.EqualTo(direct.RhythmMultiplier).Within(1e-12));
            Assert.That(chunked.ComplexityLoad, Is.EqualTo(direct.Load).Within(1e-12));
            Assert.That(Math.Abs(chunked.Stars - envelope.Stars), Is.GreaterThan(1e-6), "the chunked axis is a different reading, so the two must not agree by accident");
            Assert.That(envelope.Stars, Is.EqualTo(LyricDifficulty.ComputeDetail(lines, 1, false, LyricDifficulty.EnduranceAxis.Envelope).Stars).Within(1e-12));
            Assert.That(LyricDifficulty.Live, Is.EqualTo(LyricDifficulty.EnduranceAxis.Chunked), "the sandbox's live enduranceAxis is the chunked one");
        }

        /// <summary>
        /// The invariants the sandbox's own suite holds over the catalogue, over the maps this port can
        /// afford to rate three times each: one sealed chunk per pause, chunks that never overlap or
        /// run backwards, no empty chunk, every chunk EDGE on a character (the rounding sizing's own
        /// promise), and the merge's raise-only ordering <c>off &lt;= runs-gated &lt;= runs</c> with the
        /// step gate never producing a longer run than the ungated walk.
        /// </summary>
        [Test]
        public void TheLayoutAndTheMergeKeepTheirOwnInvariants()
        {
            const int maps_checked = 24;
            ChunkedEndurance.Settings live = ChunkedEndurance.Live;

            using JsonDocument catalogueDocument = catalogue();
            int checkedMaps = 0, gatedDiffers = 0;

            foreach (JsonElement map in catalogueDocument.RootElement.GetProperty("maps").EnumerateArray().Take(maps_checked))
            {
                string name = map.GetProperty("name").GetString()!;
                IReadOnlyList<LyricLine> lines = lyricLines(map);

                ChunkedEndurance.Report off = rate(map, live with { chunk_merge_mode = ChunkedEndurance.merge_off });
                ChunkedEndurance.Report gated = rate(map, live with { chunk_merge_mode = ChunkedEndurance.merge_runs_gated });
                ChunkedEndurance.Report runs = rate(map, live);
                ChunkedEndurance.Report pairs = rate(map, live with { chunk_merge_mode = ChunkedEndurance.merge_pairs });

                Assert.That(off.Sealed, Is.EqualTo(off.Pauses), $"{name}: one sealed chunk per pause");
                Assert.That(off.Segments, Is.EqualTo(off.Pauses + 1), $"{name}: segments are pauses plus one");
                Assert.That(off.Merged, Is.Zero, $"{name}: the off mode merges nothing");
                Assert.That(off.MergeMode, Is.EqualTo(ChunkedEndurance.merge_off));
                Assert.That(gated.MergeMode, Is.EqualTo(ChunkedEndurance.merge_runs_gated));

                Assert.That(off.Stars, Is.LessThanOrEqualTo(gated.Stars + 1e-9), $"{name}: the gate fell below no merge at all");
                Assert.That(gated.Stars, Is.LessThanOrEqualTo(runs.Stars + 1e-9), $"{name}: the gate beat the ungated walk");
                Assert.That(pairs.Stars, Is.GreaterThanOrEqualTo(off.Stars - 1e-9), $"{name}: pairs fell below no merge at all");
                Assert.That(gated.MergeLongestRun, Is.LessThanOrEqualTo(runs.MergeLongestRun), $"{name}: the gate produced a longer run");
                Assert.That(runs.Stars, Is.GreaterThanOrEqualTo(off.Stars - 1e-9), $"{name}: a merge may only ever raise the reading");

                if (Math.Abs(gated.Stars - runs.Stars) > 1e-9)
                    gatedDiffers++;

                // Every chunk is a real run of the map, in order, and the rounding sizing lands on the
                // map's own character boundaries (the same set the exact-peak refinement sweeps).
                var words = LyricDifficulty.BuildWords(lines, 1, false, LyricDifficulty.NoScores, live.span_ms);
                double[] edges = LyricDifficulty.CharacterBoundaries(words, false);
                double previousEnd = double.NegativeInfinity;

                foreach (ChunkedEndurance.Chunk chunk in runs.Chunks)
                {
                    Assert.That(chunk.Seconds, Is.GreaterThan(0), $"{name}: a chunk has no length");
                    Assert.That(chunk.StartMs, Is.GreaterThanOrEqualTo(previousEnd - 1e-6), $"{name}: chunks overlap or run backwards");
                    Assert.That(onEdge(edges, chunk.StartMs), Is.True, $"{name}: a chunk starts off-character at {chunk.StartMs:R}");
                    Assert.That(onEdge(edges, chunk.EndMs), Is.True, $"{name}: a chunk ends off-character at {chunk.EndMs:R}");
                    previousEnd = chunk.EndMs;
                }

                checkedMaps++;
            }

            Assert.That(checkedMaps, Is.EqualTo(maps_checked));
            Assert.That(gatedDiffers, Is.GreaterThan(maps_checked / 2), $"the step gate has to bite (moved {gatedDiffers} of {checkedMaps} maps)");
        }

        /// <summary>
        /// THE JUDGEMENT ARMS, and the stream Literate types. Easy and Hard Rock do not change the
        /// map; they change the WINDOWS it is judged in, and the rhythm arm prices exactly those
        /// windows - so the sandbox moves a star rating when either is selected, and so must the
        /// game. Literate moves it twice over: the converted map has a cell per punctuation mark, and
        /// the typability index scores the AUTHORED sentence rather than the stripped one.
        ///
        /// <para>The numbers below are the sandbox's own at its live settings, for three catalogue
        /// maps, re-derived whenever the live dials move; the bundled typability table is what the
        /// sandbox is fed, so the two implementations agree to better than 1e-5 and the pin carries
        /// 4e-3 only as headroom for a model retune. The literate column is
        /// pinned as a SHAPE rather than a value (up to about 1% on one map), so
        /// what it holds is the mechanism: Easy below the live arm, Hard Rock above it, and Literate
        /// above the plain stream, on every map in both streams.</para>
        /// </summary>
        [Test]
        public void TheJudgementArmsAndLiterateMoveTheRatingLikeTheSandbox()
        {
            var expected = new (string Map, string Arm, double Stars)[]
            {
                ("hand crushed by a mallet · Hand", ChunkedEndurance.mod_none, 4.499320),
                ("hand crushed by a mallet · Hand", ChunkedEndurance.mod_easy, 4.454188),
                ("hand crushed by a mallet · Hand", ChunkedEndurance.mod_hardrock, 4.730202),
                ("Locked Out Of Heaven · Insane", ChunkedEndurance.mod_none, 6.130479),
                ("Locked Out Of Heaven · Insane", ChunkedEndurance.mod_easy, 5.709115),
                ("Locked Out Of Heaven · Insane", ChunkedEndurance.mod_hardrock, 6.583266),
                ("Lose You Now (feat. Mako) · Insane", ChunkedEndurance.mod_none, 5.737951),
                ("Lose You Now (feat. Mako) · Insane", ChunkedEndurance.mod_easy, 5.476518),
                ("Lose You Now (feat. Mako) · Insane", ChunkedEndurance.mod_hardrock, 6.247117),
            };

            using JsonDocument catalogueDocument = catalogue();
            var maps = catalogueDocument.RootElement.GetProperty("maps").EnumerateArray().ToDictionary(m => m.GetProperty("name").GetString()!);

            foreach ((string mapName, string arm, double want) in expected)
            {
                IReadOnlyList<LyricLine> lines = lyricLines(maps[mapName]);
                ChunkedEndurance.Report report = LyricDifficulty.RateChunked(lines, 1, false, LyricDifficulty.ShippedScores, ChunkedEndurance.Live with { complexity_mod = arm }).Report;

                Assert.That(report.Stars, Is.EqualTo(want).Within(4e-3), $"{mapName} in the {arm} arm");
            }

            // THE SHAPE, on the three pinned maps: Easy widens the windows and reads BELOW the live
            // arm, Hard Rock points at every cell and reads ABOVE it, and Literate's authored text
            // moves the rating - in both streams, where the sandbox shows the same.
            foreach ((string mapName, _, _) in expected)
            {
                IReadOnlyList<LyricLine> lines = lyricLines(maps[mapName]);

                double rate(string arm, bool literate) => LyricDifficulty
                    .RateChunked(lines, 1, literate, LyricDifficulty.ShippedScores, ChunkedEndurance.Live with { complexity_mod = arm })
                    .Report.Stars;

                foreach (bool literate in new[] { false, true })
                {
                    double live = rate(ChunkedEndurance.mod_none, literate);
                    double easy = rate(ChunkedEndurance.mod_easy, literate);
                    double hardRock = rate(ChunkedEndurance.mod_hardrock, literate);

                    Assert.That(easy, Is.LessThan(live), $"{mapName} literate={literate}: Easy has to read below the live arm");
                    Assert.That(hardRock, Is.GreaterThan(live), $"{mapName} literate={literate}: Hard Rock has to read above it");
                }

                Assert.That(rate(ChunkedEndurance.mod_none, true), Is.GreaterThan(rate(ChunkedEndurance.mod_none, false)),
                    $"{mapName}: Literate has to move the rating");
            }

            // AND THE MODS REACH IT. StarsFor is what the difficulty attributes and the pp formula
            // read, so the arm has to arrive through the MOD LIST and not only through the model's own
            // argument: a player selecting Easy, Hard Rock or Literate in the game must get the rating
            // the sandbox shows with the same mod set.
            IReadOnlyList<LyricLine> mallet = lyricLines(maps["hand crushed by a mallet \u00b7 Hand"]);
            double plainStars = PerformancePoints.StarsFor(mallet, new Mod[] { })!.Value;
            double easyStars = PerformancePoints.StarsFor(mallet, new Mod[] { new TypeBeatModEasy() })!.Value;
            double hardRockStars = PerformancePoints.StarsFor(mallet, new Mod[] { new TypeBeatModHardRock() })!.Value;
            double literateStars = PerformancePoints.StarsFor(mallet, new Mod[] { new TypeBeatModLiterate() })!.Value;

            Assert.That(easyStars, Is.LessThan(plainStars), "Easy has to reach the rating through the mod list");
            Assert.That(hardRockStars, Is.GreaterThan(plainStars), "Hard Rock has to reach it the same way");
            Assert.That(literateStars, Is.Not.EqualTo(plainStars).Within(1e-6), "Literate has to reach it too");

            // AND THE DIAL BITES ACROSS THE POOL, so a future change cannot quietly neuter it: the
            // Hard Rock arm has to move most of the catalogue, not only the three maps above.
            int moved = 0;

            foreach (JsonElement map in maps.Values)
            {
                IReadOnlyList<LyricLine> lines = lyricLines(map);
                double live = LyricDifficulty.RateChunked(lines, 1, false, LyricDifficulty.ShippedScores, ChunkedEndurance.Live).Report.Stars;
                double hardRock = LyricDifficulty.RateChunked(lines, 1, false, LyricDifficulty.ShippedScores, ChunkedEndurance.Live with { complexity_mod = ChunkedEndurance.mod_hardrock }).Report.Stars;

                if (Math.Abs(hardRock - live) > 1e-6)
                    moved++;
            }

            TestContext.WriteLine($"the Hard Rock arm moved {moved} of {maps.Count} maps");
            Assert.That(moved, Is.GreaterThan(maps.Count / 2), "the judgement arm has to move most of the pool");
        }

        private static bool onEdge(double[] edges, double value)
        {
            int index = Array.BinarySearch(edges, value);

            if (index >= 0)
                return true;

            int next = ~index;

            return (next < edges.Length && Math.Abs(edges[next] - value) < 1e-6)
                   || (next > 0 && Math.Abs(edges[next - 1] - value) < 1e-6);
        }

        /// <summary>One line of a synthetic map, the same helper the envelope model's own pins use.</summary>
        private static LyricLine line(double start, double end, params (string Text, double Start, double End)[] units) => new LyricLine
        {
            RawText = string.Join(" ", units.Select(u => u.Text)),
            StartTime = start,
            EndTime = end,
            SingEndTime = end,
            Units = units.Select(u => new TimedUnit { Text = u.Text, StartTime = u.Start, EndTime = u.End }).ToArray(),
        };
    }
}
