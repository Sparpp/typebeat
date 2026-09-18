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
    }
}
