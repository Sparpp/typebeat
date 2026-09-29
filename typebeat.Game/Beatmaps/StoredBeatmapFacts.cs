// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using typebeat.Game.Rulesets.Mods;

namespace typebeat.Game.Beatmaps
{
    /// <summary>
    /// Two facts about a difficulty that live in its FILE but are stored on its realm row
    /// (<see cref="BeatmapInfo.TargetWpm"/> and <see cref="BeatmapInfo.HasIntroBeatdrop"/>, realm schema 59), so song
    /// select can group the whole library by them without decoding a single map.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WRITTEN wherever the map is decoded anyway: at import and on every editor save (both go through
    /// <see cref="BeatmapUpdater.Process"/>, beside the star rating), and by the background pass that fills missing
    /// star ratings (<c>BackgroundDataStoreProcessor</c>), which is also what backfills a library that predates
    /// schema 59.
    /// </para>
    /// <para>
    /// THE UNPROCESSED MARKER is <see cref="BeatmapInfo.TargetWpm"/> &lt; 0 (<see cref="UNPROCESSED"/>), and it gates
    /// BOTH fields: a bool has no sentinel, so <see cref="BeatmapInfo.HasIntroBeatdrop"/> is only trusted on a row
    /// whose WPM is non-negative. The two are always written together, and a processed map with nothing typeable
    /// stores 0 rather than -1, so a genuinely paceless map is not re-queued on every startup. A map whose pace
    /// cannot be computed at all (its conversion throws) keeps -1 and is retried, exactly like a star rating that
    /// failed to calculate. Readers that can afford a decode (the context menu toggle, the intro) fall back to one
    /// for an unprocessed row; song select's grouping cannot, so it reads an unprocessed row as "no beatdrop" and
    /// its WPM as unknown until the background pass reaches it.
    /// </para>
    /// </remarks>
    public static class StoredBeatmapFacts
    {
        /// <summary>
        /// <see cref="BeatmapInfo.TargetWpm"/> for a row whose facts have not been computed (the <see cref="BeatmapInfo.StarRating"/> sentinel).
        /// </summary>
        public const double UNPROCESSED = -1;

        /// <summary>
        /// Whether a row's stored facts can be trusted. See the class remarks.
        /// </summary>
        public static bool IsProcessed(BeatmapInfo beatmap) => beatmap.TargetWpm >= 0;

        /// <summary>
        /// Computes both facts from a decoded map: the target WPM at rate 1.0 with no mods, and whether it declares
        /// an intro beatdrop. Throws if the map cannot be converted; the caller decides what a failure means.
        /// </summary>
        public static (double TargetWpm, bool HasIntroBeatdrop) Compute(IWorkingBeatmap working)
        {
            bool hasIntroBeatdrop = working.Beatmap.IntroBeatdropTime != null;

            var playable = working.GetPlayableBeatmap(working.BeatmapInfo.Ruleset, Array.Empty<Mod>()) as IHasTypingPace;
            double targetWpm = playable?.GetTypingPace()?.TargetWpm ?? 0;

            // Never let a degenerate figure read as the unprocessed marker (or poison a bucket).
            if (double.IsNaN(targetWpm) || double.IsInfinity(targetWpm) || targetWpm < 0)
                targetWpm = 0;

            return (targetWpm, hasIntroBeatdrop);
        }

        /// <summary>
        /// Writes both facts onto <paramref name="beatmap"/> (a live realm row inside a write, or a detached copy).
        /// </summary>
        public static void Apply(BeatmapInfo beatmap, IWorkingBeatmap working)
        {
            var (targetWpm, hasIntroBeatdrop) = Compute(working);

            beatmap.TargetWpm = targetWpm;
            beatmap.HasIntroBeatdrop = hasIntroBeatdrop;
        }

        /// <summary>
        /// Whether any of a set's difficulties declares an intro beatdrop, as far as the STORED facts can say:
        /// <c>true</c> as soon as one processed difficulty does, <c>false</c> when every difficulty is processed and
        /// none does, and <c>null</c> when no processed difficulty does but at least one is unprocessed (the caller
        /// must decode, or, if it cannot, treat that as <c>false</c>).
        /// </summary>
        public static bool? AnyIntroBeatdrop(IEnumerable<BeatmapInfo> difficulties)
        {
            bool anyUnprocessed = false;

            foreach (var beatmap in difficulties)
            {
                if (!IsProcessed(beatmap))
                {
                    anyUnprocessed = true;
                    continue;
                }

                if (beatmap.HasIntroBeatdrop)
                    return true;
            }

            return anyUnprocessed ? null : false;
        }
    }
}
