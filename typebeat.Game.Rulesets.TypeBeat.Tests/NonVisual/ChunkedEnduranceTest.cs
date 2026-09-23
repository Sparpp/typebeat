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
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class ChunkedEnduranceTest
    {

        /// <summary>
        /// The axis is actually WIRED behind the switch: <c>LyricDifficulty.ComputeDetail</c>
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

        /// <summary>One line of a synthetic map, the same helper the envelope model's own pins use.</summary>
        private static LyricLine line(double start, double end, params (string Text, double Start, double End)[] units) => new LyricLine
        {
            RawText = string.Join(" ", units.Select(u => u.Text)),
            StartTime = start,
            EndTime = end,
            SingEndTime = end,
            Units = units.Select(u => new TimedUnit { Text = u.Text, StartTime = u.Start, EndTime = u.End }).ToArray(),
        };

        /// <summary>
        /// The sandbox's <c>buildMap(count, words, ms, start)</c>: that many equal words of five
        /// letters, each unit owning an equal share of the line's span. The numbers
        /// pinned below were read off the sandbox on a map built exactly this way, so the two sides
        /// can be compared entry for entry.
        /// </summary>
        private static LyricLine words(int count, double start, double span)
        {
            var units = new TimedUnit[count];

            for (int i = 0; i < count; i++)
                units[i] = new TimedUnit { Text = "flame", StartTime = start + i * span / count, EndTime = start + (i + 1) * span / count };

            return new LyricLine
            {
                RawText = string.Join(" ", units.Select(u => u.Text)),
                StartTime = start,
                EndTime = start + span,
                SingEndTime = start + span,
                Units = units,
            };
        }

        /// <summary>
        /// THE OVERLAPPING LAYOUT, pinned against the sandbox's own reading of the same map.
        ///
        /// <para>sr-config.json now selects <c>chunkProfile: overlapping</c> with the scale dividing by
        /// the map's own weight sum, so this is the layout the game ships: the timeline is sampled
        /// every base-window/subdivisions milliseconds, each sample keeps the strongest window of the
        /// doubling family tried at its start, centre and end, a window holding fewer than
        /// <c>minimumChars</c> characters is refused, and the score is the rank-weighted mean where a
        /// sample consumes rank in proportion to its own support. Four slow lines, a five-word burst
        /// between rests and four slow lines again - the burst is what the floor and the window
        /// family are for, and the two rest gaps are what stop a window borrowing cells from its
        /// neighbours to clear the floor.</para>
        ///
        /// <para>Expected values are the sandbox's, from the same map at the same dials - the ACTIVE
        /// snapshot in <c>sr-config.json</c> that <see cref="ChunkedEndurance.Live"/> mirrors (anchor
        /// 11.1, burst 200, reference 1.5 s, exponent 0.31, base 1.5 s, samples 4, horizon 60.75 s,
        /// decay 0.9^rank^1.07, floor 16 characters, bonus 0.15 at 1500 characters).</para>
        /// </summary>
        [Test]
        public void TheOverlappingAxisMatchesTheSandboxOnASyntheticMap()
        {
            var lines = new List<LyricLine>();

            for (int i = 0; i < 4; i++)
                lines.Add(words(4, i * 2000, 2000));

            lines.Add(words(5, 9000, 1200));

            for (int i = 0; i < 4; i++)
                lines.Add(words(4, 12000 + i * 2000, 2000));

            ChunkedEndurance.Report report = LyricDifficulty.RateChunked(lines, 1, false, LyricDifficulty.NoScores, ChunkedEndurance.Live).Report;

            Assert.That(report.Count, Is.EqualTo(54), "sample count");
            Assert.That(report.Stars, Is.EqualTo(4.80103066754845).Within(1e-9), "star figure");
            Assert.That(report.Score, Is.EqualTo(0.4258252376258951).Within(1e-12), "rank-weighted mean");
            Assert.That(report.ScoreNoRhythm, Is.EqualTo(0.42386494300854843).Within(1e-12), "the same with the boost neutralised");
            Assert.That(report.Hardest, Is.EqualTo(0.5670435657205507).Within(1e-12), "hardest sample");
            Assert.That(report.Mean, Is.EqualTo(0.4108565085856843).Within(1e-12), "plain mean of the samples");
            Assert.That(report.CountedCharacters, Is.EqualTo(213).Within(1e-9), "every character counted once");
            Assert.That(report.LengthCharacters, Is.EqualTo(157.34266423908875).Within(1e-9), "weighted characters above the floor");
            Assert.That(report.LengthBonusApplied, Is.EqualTo(0.015734266423908874).Within(1e-12), "length bonus");
            Assert.That(report.ShortestSeconds, Is.EqualTo(0.125).Within(1e-12), "shortest sample support");
            Assert.That(report.LongestSeconds, Is.EqualTo(0.375).Within(1e-12), "longest sample support");
            Assert.That(report.Sizing, Is.EqualTo("regular"), "the overlapping layout has no sizing search");
            Assert.That(report.MergeMode, Is.EqualTo(ChunkedEndurance.merge_off), "and no merge");
            Assert.That(report.MergeRuns, Is.Zero, "no runs");
            Assert.That(report.Chunks.Count, Is.EqualTo(54), "the series is the samples");
        }

        /// <summary>
        /// The OTHER layout still reads. The sandbox keeps both profiles on its panel and so does
        /// this class, so the chunk grid has to keep working when it is selected even though the
        /// live configuration no longer picks it: a layout that silently rots is worse than one
        /// that was deleted, because the dial is still there to pick.
        /// </summary>
        [Test]
        public void TheChunkLayoutStillReadsWhenItIsSelected()
        {
            var lines = new List<LyricLine>();

            for (int i = 0; i < 8; i++)
                lines.Add(words(4, i * 2000, 2000));

            ChunkedEndurance.Settings chunked = ChunkedEndurance.Live with { chunk_profile = ChunkedEndurance.profile_chunks };
            ChunkedEndurance.Report report = LyricDifficulty.RateChunked(lines, 1, false, LyricDifficulty.NoScores, chunked).Report;

            Assert.That(report.Stars, Is.GreaterThan(0), "the chunk grid still rates");
            Assert.That(report.Sizing, Is.EqualTo(ChunkedEndurance.sizing_nearest), "and still reports its own sizing");
            Assert.That(report.MergeMode, Is.EqualTo(ChunkedEndurance.merge_runs), "and still runs its merges");
        }

        /// <summary>
        /// A PLAIN PLAY IS THE LIVE PATH, and this pins it. The grid and the merge are frozen against
        /// the non-rate mods (Literate and the judgement arms) so those cannot reshape a map's runs;
        /// that machinery must be INERT for a play carrying neither, or every published figure moves
        /// with it. That is not hypothetical: the port's first cut read the grid off the character
        /// mass for EVERY play, which left every plain rating off the live value (checked 2026-09-19
        /// against the live server's stored ratings for six real beatmap sets: 9 of 10 difficulties
        /// bit-identical after the gate, and none of them before it).
        ///
        /// <para>The fixture has enough material, and enough typability behind it, for the strain's
        /// grid and the character mass's grid to land on different words, which is the whole of the
        /// regression this catches.</para>
        /// </summary>
        [Test]
        public void APlainPlayKeepsTheShippedGridAndRuns()
        {
            var lines = new List<LyricLine>();

            for (int i = 0; i < 8; i++)
            {
                double at = i * 2400;

                lines.Add(line(at, at + 2200,
                    ("the", at + 60, at + 300),
                    ("quick", at + 300, at + 700),
                    ("brown", at + 700, at + 1100),
                    ("fox", at + 1100, at + 1500),
                    ("jumps", at + 1500, at + 1900),
                    ("over", at + 1900, at + 2200)));
            }

            // The figure the shipped model reads on this fixture: anything that moves it has moved a
            // plain play's rating. It moved on 2026-09-20 with the LAYOUT (sr-config.json selecting the
            // overlapping profile, see the port's own pin above) and again on 2026-09-22 with the DIALS
            // (the active snapshot's anchor, character floor and chunk length terms), so the figures
            // before those moves were 5.3281973709372954 and 5.4530884437856484.
            Assert.That(LyricDifficulty.Compute(lines), Is.EqualTo(5.284047654634864).Within(1e-12));

            // THE SAME FIXTURE WITHOUT THE INDEX, which is the reading the sandbox produces for it: the
            // lab's typability bundle carries no scores for this text, so the typability-free arm is
            // the one the two sides can be compared on, and it is the second map the port is pinned
            // against (the first being TheOverlappingAxisMatchesTheSandboxOnASyntheticMap).
            ChunkedEndurance.Report bare = LyricDifficulty.RateChunked(lines, 1, false, LyricDifficulty.NoScores, ChunkedEndurance.Live).Report;
            Assert.That(bare.Stars, Is.EqualTo(5.3345735594994945).Within(1e-9), "sandbox: the same map with no typability behind it");
        }
    }
}
