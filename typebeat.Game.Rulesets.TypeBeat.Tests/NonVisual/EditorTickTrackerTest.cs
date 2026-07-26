// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the LICENCE file in the repository root.

using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Edit;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The compose screen's per-note tick crossing detector: it reports the unit-start times the
    /// playhead swept through in a frame's (prev, now] window, and stays silent whenever motion is
    /// not ordinary forward playback: the first frame, a paused gap, a rewind, or a seek-sized jump.
    /// </summary>
    [TestFixture]
    public class EditorTickTrackerTest
    {
        private static readonly double[] ticks = { 1000, 1500, 1500, 2000, 2600, 5000 };

        // The crossing-window tests exercise interval logic, not jump suppression, so they use a
        // generous frame budget; the suppression tests below pin the realistic 250ms threshold.
        private static EditorTickTracker roomy() => new EditorTickTracker(maxFrameDelta: 100_000);

        [Test]
        public void FirstFrameAfterReset_ReportsNothing_ThenAnchors()
        {
            var tracker = roomy();

            // Nothing to compare the first frame against; it only anchors.
            Assert.That(tracker.Advance(1200, ticks), Is.Empty);

            // Next forward frame reports crossings relative to the anchor (1200, 1600].
            Assert.That(tracker.Advance(1600, ticks), Is.EqualTo(new[] { 1500d, 1500d }));
        }

        [Test]
        public void ForwardFrame_ReportsHalfOpenInterval_Ascending()
        {
            var tracker = roomy();
            tracker.Advance(900, ticks); // anchor

            // (900, 2000]: 1000, both 1500s, and 2000 (inclusive upper bound); 2600 excluded.
            Assert.That(tracker.Advance(2000, ticks), Is.EqualTo(new[] { 1000d, 1500d, 1500d, 2000d }));
        }

        [Test]
        public void LowerBoundIsExclusive_UpperBoundIsInclusive()
        {
            var tracker = roomy();
            tracker.Advance(1000, ticks); // anchor exactly on the 1000 tick

            // (1000, 1500]: the 1000 tick is NOT re-reported, the 1500s are.
            Assert.That(tracker.Advance(1500, ticks), Is.EqualTo(new[] { 1500d, 1500d }));
        }

        [Test]
        public void NoTicksInWindow_ReportsEmpty()
        {
            var tracker = roomy();
            tracker.Advance(2000, ticks);

            Assert.That(tracker.Advance(2500, ticks), Is.Empty); // (2000, 2500] holds nothing
        }

        [Test]
        public void ResetBetweenFrames_SuppressesTheGap()
        {
            var tracker = roomy();
            tracker.Advance(900, ticks);

            // e.g. playback paused here; the screen calls Reset each paused frame.
            tracker.Reset();

            // Resuming at 2000: the 900..2000 gap must NOT burst-tick; this frame only re-anchors.
            Assert.That(tracker.Advance(2000, ticks), Is.Empty);

            // Playback continues from the new anchor: (2000, 2700] → 2600.
            Assert.That(tracker.Advance(2700, ticks), Is.EqualTo(new[] { 2600d }));
        }

        [Test]
        public void ForwardJump_IsSuppressed_AndReAnchors()
        {
            var tracker = new EditorTickTracker(maxFrameDelta: 250);
            tracker.Advance(1000, ticks);

            // A seek while playing: 1000 -> 4900 in one frame (> 250ms). No machine-gun of crossings.
            Assert.That(tracker.Advance(4900, ticks), Is.Empty);

            // The tracker re-anchored to 4900, so ordinary playback resumes cleanly: (4900, 5100] → 5000.
            Assert.That(tracker.Advance(5100, ticks), Is.EqualTo(new[] { 5000d }));
        }

        [Test]
        public void JustUnderMaxDelta_StillTicks_JustOverDoesNot()
        {
            var underTracker = new EditorTickTracker(maxFrameDelta: 250);
            underTracker.Advance(1900, ticks);
            Assert.That(underTracker.Advance(2149, ticks), Is.EqualTo(new[] { 2000d })); // delta 249 <= 250

            var overTracker = new EditorTickTracker(maxFrameDelta: 250);
            overTracker.Advance(1900, ticks);
            Assert.That(overTracker.Advance(2151, ticks), Is.Empty); // delta 251 > 250, suppressed
        }

        [Test]
        public void Rewind_ReportsNothing_AndReAnchorsForwardFromThere()
        {
            var tracker = roomy();
            tracker.Advance(3000, ticks);

            // Playhead moved backwards (scrub-back / rewind): non-forward frame, no ticks.
            Assert.That(tracker.Advance(1200, ticks), Is.Empty);

            // Now anchored at 1200, forward playback resumes: (1200, 1600] → both 1500s.
            Assert.That(tracker.Advance(1600, ticks), Is.EqualTo(new[] { 1500d, 1500d }));
        }

        [Test]
        public void StationaryFrame_ReportsNothing()
        {
            var tracker = roomy();
            tracker.Advance(2000, ticks);

            // delta == 0 (a stalled/duplicated frame) is not forward motion.
            Assert.That(tracker.Advance(2000, ticks), Is.Empty);
        }

        [Test]
        public void UnsortedTickInput_IsReturnedAscending()
        {
            var tracker = roomy();
            var unordered = new[] { 2600d, 1000d, 2000d, 1500d };
            tracker.Advance(900, unordered);

            Assert.That(tracker.Advance(2100, unordered), Is.EqualTo(new[] { 1000d, 1500d, 2000d }));
        }
    }

    /// <summary>
    /// The time-collection helper feeding the compose screen's two tick streams: WORD-unit starts
    /// (accented tick) and syllable-subdivision boundaries (lighter sub-tick), with any boundary
    /// coinciding with a word start deduped into the word stream only.
    /// </summary>
    [TestFixture]
    public class EditorTickTimesTest
    {
        private static TimedUnit unit(double start, double end, params double[] syllables) => new TimedUnit
        {
            Text = "word",
            StartTime = start,
            EndTime = end,
            SyllableBoundaries = syllables,
        };

        private static LyricLine line(params TimedUnit[] units) => new LyricLine
        {
            RawText = string.Join(" ", units.Select(u => u.Text)),
            StartTime = units[0].StartTime,
            EndTime = units[^1].EndTime,
            SingEndTime = units[^1].EndTime,
            Units = units,
        };

        [Test]
        public void CollectsWordStartsAcrossLines()
        {
            var (wordStarts, syllables) = EditorTickTimes.Collect(new[]
            {
                line(unit(1000, 1400), unit(1400, 2000)),
                line(unit(3000, 3500)),
            });

            Assert.That(wordStarts, Is.EqualTo(new[] { 1000d, 1400d, 3000d }));
            Assert.That(syllables, Is.Empty);
        }

        [Test]
        public void CollectsSyllableBoundariesFromAllUnits()
        {
            var (wordStarts, syllables) = EditorTickTimes.Collect(new[]
            {
                line(unit(1000, 1400, 1200), unit(1400, 2000, 1600, 1800)),
            });

            Assert.That(wordStarts, Is.EqualTo(new[] { 1000d, 1400d }));
            Assert.That(syllables, Is.EqualTo(new[] { 1200d, 1600d, 1800d }));
        }

        [Test]
        public void BoundaryCoincidingWithAWordStart_YieldsOnlyTheWordTick()
        {
            // 1400 is both the second word's start and (degenerately) a boundary of the first
            // word; 3000 collides with a word start on a DIFFERENT line. Both must be dropped
            // from the syllable stream, leaving only the genuinely interior 1200.
            var (wordStarts, syllables) = EditorTickTimes.Collect(new[]
            {
                line(unit(1000, 1400, 1200, 1400), unit(1400, 2000, 3000)),
                line(unit(3000, 3500)),
            });

            Assert.That(wordStarts, Is.EqualTo(new[] { 1000d, 1400d, 3000d }));
            Assert.That(syllables, Is.EqualTo(new[] { 1200d }));
        }

        [Test]
        public void CombinedStreams_CrossingDetection_TicksEachTimeOnce()
        {
            // The compose screen runs one tracker per stream over the same frame times. Sweep a
            // frame across everything and check each stream reports exactly its own times: the
            // deduped 1400 fires once, as a word tick.
            var (wordStarts, syllables) = EditorTickTimes.Collect(new[]
            {
                line(unit(1000, 1400, 1200, 1400), unit(1400, 2000, 1600)),
            });

            var wordTracker = new EditorTickTracker(maxFrameDelta: 100_000);
            var syllableTracker = new EditorTickTracker(maxFrameDelta: 100_000);

            wordTracker.Advance(900, wordStarts);
            syllableTracker.Advance(900, syllables);

            Assert.That(wordTracker.Advance(2000, wordStarts), Is.EqualTo(new[] { 1000d, 1400d }));
            Assert.That(syllableTracker.Advance(2000, syllables), Is.EqualTo(new[] { 1200d, 1600d }));
        }

        [Test]
        public void NoLines_YieldsEmptyStreams()
        {
            var (wordStarts, syllables) = EditorTickTimes.Collect(System.Array.Empty<LyricLine>());

            Assert.That(wordStarts, Is.Empty);
            Assert.That(syllables, Is.Empty);
        }

        [Test]
        public void SyllableStreamUsesTheSameSampleAsTheWordStream()
        {
            // The two streams used to play different samples (a metronome click for words, a
            // lighter UI notch for syllables). That split is gone, pin it so it stays gone: both
            // the compose screen's word tick and syllable sub-tick load LyricComposeScreen.TickSampleName,
            // told apart only by volume.
            Assert.That(LyricComposeScreen.TickSampleName, Is.EqualTo(@"UI/metronome-tick"));
        }
    }
}
