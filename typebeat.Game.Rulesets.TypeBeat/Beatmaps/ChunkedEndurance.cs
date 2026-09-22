// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// THE CHUNKED ENDURANCE AXIS: a second reading of the same map, and the port of the Star
    /// Rating Sandbox's chunked block (<c>tools/star-rating-sandbox/model.mjs</c>, the
    /// "chunked axis" section).
    ///
    /// <para>WHAT IT ASKS. The envelope model reads a map as one number built from its hardest
    /// window and how much of the range the rest of the material fills. This axis asks a different
    /// question: score the map's own windows directly, then take the decay-weighted mean of those
    /// scores with the hardest weighted most. The result is a star figure on its own scale: on this
    /// axis <c>starScale</c> is the anchor rather than <c>anchor / (1 + range)</c>, and
    /// <c>difficult</c>/<c>demanding</c> are both the weighted length count the pp figure prices a
    /// miss against.</para>
    ///
    /// <para>TWO LAYOUTS, ONE AXIS. <see cref="Settings.chunk_profile"/> picks which window family
    /// the map is read through, and the sandbox's saved configuration - which
    /// <see cref="Settings.Live"/> mirrors - selects <see cref="profile_overlapping"/>:
    /// <list type="bullet">
    /// <item><description>the CHUNK grid (<see cref="profile_chunks"/>), which the axis was first
    /// ported for: equal chunks, the hardest chunk's start as the anchor so the hardest material is
    /// never split, a pause gate, and adjacent runs lifted where material straddles a boundary;</description></item>
    /// <item><description>the OVERLAPPING family (<see cref="profile_overlapping"/>), which the game
    /// now ships: the timeline is sampled at a regular step, each sample keeps the strongest of a
    /// doubling family of windows tried at its start, centre and end, a window holding fewer than
    /// <see cref="Settings.minimum_chars"/> characters is refused, and each sample consumes rank in
    /// proportion to its own support.</description></item>
    /// </list>
    /// The chunk dials stay live for the first layout and the overlapping dials for the second,
    /// exactly as the sandbox keeps both on its panel.</para>
    ///
    /// <para>WHAT IT DOES NOT READ. The peak-window scan, the envelope sweep, the strain
    /// accumulation, the catch-up walk, stamina and the burst window. The chunked rating is
    /// <c>anchor x score x (1 + length bonus)</c> and nothing else; porting the rest "for
    /// completeness" is what made the sandbox slow.</para>
    ///
    /// <para>ONE INPUT THE ENVELOPE MODEL DOES NOT HAVE. A chunk's strain is multiplied by the
    /// rhythm boost INSIDE that window, read off a load prefix over the map's own keypress stream
    /// (see <see cref="BuildLoadPrefix"/>), not off the map-level rhythmic-complexity multiplier.
    /// That prefix is the only reason this file needs the press stream at all.</para>
    ///
    /// <para>THE DIALS ARE THE SANDBOX'S LIVE ONES. Every value in <see cref="Settings"/> mirrors
    /// the sandbox's saved configuration, so moving a dial here means moving it there first.</para>
    /// </summary>
    internal static class ChunkedEndurance
    {
        internal const string merge_off = "off";
        internal const string merge_pairs = "pairs";
        internal const string merge_runs = "runs";
        internal const string merge_runs_gated = "runs-gated";

        /// <summary>The judgement arms the rhythm dial reads the engine through (the sandbox's complexityMods).</summary>
        internal const string mod_none = "none";
        internal const string mod_easy = "easy";
        internal const string mod_hardrock = "hardrock";

        internal const string sizing_fixed = "fixed";
        internal const string sizing_nearest = "nearest";
        internal const string sizing_adaptive = "adaptive";

        /// <summary>
        /// WHICH LAYOUT the axis reads (the sandbox's <c>chunkProfile</c>). <see cref="profile_chunks"/>
        /// is the chunk grid this class was built around; <see cref="profile_overlapping"/> samples
        /// the playback timeline instead, which is what <c>sr-config.json</c> now selects and what
        /// <see cref="Live"/> therefore ships.
        /// </summary>
        internal const string profile_chunks = "chunks";
        internal const string profile_overlapping = "overlapping";

        /// <summary>
        /// The chunked axis's dials, named after the sandbox's settings keys so the two can be read
        /// side by side. <see cref="Live"/> is <c>sr-config.json</c>'s current snapshot.
        /// </summary>
        internal sealed record Settings
        {
            // The capability curve S(t) this axis reads the material against: the sandbox's
            // PARAMETERS panel, which is a different calibration from the envelope model's own
            // constants (see LyricDifficulty).
            public double capability_base_wpm { get; init; } = 220;
            public double capability_burst_wpm { get; init; } = 200;
            public double capability_ref_seconds { get; init; } = 1.5;
            public double capability_exponent { get; init; } = 0.31;

            /// <summary>Stars at the record pace: on this axis the star scale IS the anchor.</summary>
            public double anchor { get; init; } = 11.1;

            /// <summary>Floor on a word's sung span, in authoring milliseconds, before the rate divide.</summary>
            public double span_ms { get; init; } = 30;

            /// <summary>The timeline's resolution; the sandbox refuses any value but 50.</summary>
            public double bin_ms { get; init; } = 50;

            /// <summary>What one freestyle slot costs, as a fraction of an ordinary cell.</summary>
            public double freestyle { get; init; } = 0.25;

            /// <summary>
            /// THE SHIFT SURCHARGE, and it is DEMAND ONLY. Under Literate a capital is Shift plus the
            /// letter, and the press walk charges exactly that; this axis read its cells straight from
            /// the word builder, where a capital is still one character, so without this dial the rating
            /// the game actually ships priced a capital at one input. The cost is charged once per RUN
            /// of capitals or shifted marks, because the key is held across it ("USA" is one Shift,
            /// "A B" two) - see <see cref="ShiftSurcharge"/>.
            ///
            /// <para>It reaches the chunk strain's NUMERATOR and nothing else: deliberately not the
            /// character counts, so the length bonus and the difficult-character figure a miss is
            /// priced against stay character-based, and not the pace figures, which are read off the
            /// default stream. Inert without the Literate mod, whose stream is the one that carries
            /// capitals and marks at all.</para>
            /// </summary>
            public double shift_cost { get; init; } = 1;

            // The typability arm. The fixture carries no per-line scores, so these dials are inert on
            // it; the reading they price arrives with the in-client index.
            public double typability_strength { get; init; } = 0.4;
            /// <summary>
            /// How much a typist's own sentence speeds vary, per z. The LAB's live reference value
            /// (typability.json's <c>cv</c>, which the lab serves and the sandbox passes to the
            /// model), not the model file's own default: the two differ in the fourth decimal and
            /// the reference is what both sides actually price with.
            /// </summary>
            public double typability_cv { get; init; } = 0.207038;
            public double typability_min_scored_fraction { get; init; } = 0.5;

            // The rhythm arm: the per-window boost read off the local load. The shelter is always the
            // live syllable arm and the scan is always the rolling one; only the strength, the
            // tolerance and the saturation are dials.
            public double complexity_strength { get; init; } = 0.19;

            /// <summary>
            /// Which judgement arm the rhythm dial reads the engine through: <see cref="mod_none"/> is
            /// the live span rule, <see cref="mod_easy"/> doubles every window and shelters the whole
            /// word, and <see cref="mod_hardrock"/> puts every cell on its own point target. It moves
            /// the rating because the windows are what the scan prices, exactly as it does in the
            /// sandbox - not because the map changed.
            /// </summary>
            public string complexity_mod { get; init; } = mod_none;
            public double complexity_tolerance { get; init; } = 120;
            public int complexity_horizon { get; init; } = 3;
            public double boost_scale { get; init; } = 0.002;

            /// <summary>The judgement-window premium: how finely a word's timing is subdivided.</summary>
            public double window_density_bonus { get; init; } = 0.06;

            // The rolling scan's own press weights, fixed to the constants the scan has always used:
            // they price how freely a keypress can give up its own timing.
            public double scan_weight_reference { get; init; } = 0.75;
            public double scan_weight_exponent { get; init; } = 2;

            // The chunk dials.
            public double chunk_seconds { get; init; } = 1.5;
            public string chunk_sizing { get; init; } = sizing_nearest;
            public double chunk_adaptive_range { get; init; } = 0.15;
            public double chunk_decay { get; init; } = 0.9;
            public double chunk_decay_power { get; init; } = 1.07;
            public string chunk_merge_mode { get; init; } = merge_runs;
            public int chunk_merge_passes { get; init; } = 4;
            public double chunk_length_bonus { get; init; } = 0.15;
            public double chunk_length_falloff { get; init; } = 2.5;
            public double chunk_length_floor { get; init; } = 0.5;
            public double chunk_length_scale { get; init; } = 1500;

            /// <summary>
            /// The layout the axis reads: <see cref="profile_overlapping"/> at the current snapshot.
            /// The chunk dials below stay live for the other layout, exactly as the sandbox keeps
            /// both sets of dials on its panel.
            /// </summary>
            public string chunk_profile { get; init; } = profile_overlapping;

            /// <summary>
            /// The overlapping layout's three dials. <c>overlap_samples</c> is how many samples one
            /// base window is cut into (so the sample step is base/subdivisions), and
            /// <c>overlap_max_seconds</c> is the horizon the window lengths double up to, including
            /// the exact cap.
            /// </summary>
            public double overlap_max_seconds { get; init; } = 60.75;
            public double overlap_samples { get; init; } = 4;

            /// <summary>
            /// THE CHARACTER FLOOR (the sandbox's <c>minimumChars</c>). A time window holding fewer
            /// weighted characters than this is refused outright, so a base-length slice that
            /// catches a handful of cells between rests cannot stand as a candidate: the shortest
            /// eligible window becomes the shortest one that actually holds this many characters.
            /// Read off the same cell prefix the strain reads, so the floor means the same thing on
            /// this axis as it does on the peak. 0 disables it.
            /// </summary>
            public double minimum_chars { get; init; } = 22;

            /// <summary>
            /// The envelope-characters dial. On this axis only the <c>Fill</c> readout reads it;
            /// nothing in the chunk score does.
            /// </summary>
            public double chars { get; init; } = 10;
        }

        /// <summary>
        /// The dials the game SHIPS: the Star Rating Sandbox's ACTIVE settings snapshot, which the
        /// sandbox writes to <c>tools/star-rating-sandbox/sr-config.json</c> on every change (its
        /// <c>parameters</c> and <c>model</c> blocks; the <c>baseline*</c> blocks beside them are the
        /// comparison column, not this). It is the mirror of that one record: change a dial there and
        /// change it here, regenerate the catalogue fixture, and bump the difficulty version.
        /// </summary>
        internal static readonly Settings Live = new Settings();

        /// <summary>
        /// The scale one window's typability puts on the capability curve, exactly 1 when the snapshot
        /// carries no scores for the window or the map's own scored fraction is below the arm's gate.
        /// A window's typability DIVIDES the strain, as everywhere else in the model: a slow-to-type
        /// passage carries a lower multiplier and so reads as more demand.
        /// </summary>
        internal delegate double WindowMultiplier(double aMs, double bMs);

        /// <summary>
        /// The map's cells as a binned prefix, plus the origin the chunk times are reported from.
        /// The bin prefix is what the chunk strain and the length count read: both are mass
        /// arithmetic on the timeline's own 50 ms grid, so the two agree on what a chunk holds.
        /// </summary>
        internal readonly struct Timeline
        {
            public readonly IReadOnlyList<LyricDifficulty.Word> Words;
            public readonly double T0;
            public readonly int BinCount;
            public readonly double BinMs;
            private readonly double[] prefix;
            /// <summary>
            /// THE SHIFT SURCHARGE as a second prefix, or null when nothing can be charged: the
            /// sandbox's <c>preShift</c>. It is read only by the chunk strain's demand, never by the
            /// character arithmetic (see <see cref="Settings.shift_cost"/>).
            /// </summary>
            private readonly double[]? shiftPrefix;

            public Timeline(IReadOnlyList<LyricDifficulty.Word> words, double t0, int binCount, double binMs, double[] prefix, double[]? shiftPrefix = null)
            {
                Words = words;
                T0 = t0;
                BinCount = binCount;
                BinMs = binMs;
                this.prefix = prefix;
                this.shiftPrefix = shiftPrefix;
            }

            public double MassBetween(double aMs, double bMs)
                => LyricDifficulty.At(prefix, (bMs - T0) / BinMs, BinCount) - LyricDifficulty.At(prefix, (aMs - T0) / BinMs, BinCount);

            /// <summary>The Shift keypresses the window asks for, on the same bin arithmetic as the characters.</summary>
            public double ShiftMassBetween(double aMs, double bMs)
                => shiftPrefix == null
                    ? 0
                    : LyricDifficulty.At(shiftPrefix, (bMs - T0) / BinMs, BinCount) - LyricDifficulty.At(shiftPrefix, (aMs - T0) / BinMs, BinCount);
        }

        /// <summary>
        /// The map's local rhythm load as a bin prefix: the rolling scan's load split back onto the
        /// keypresses it was measured on and spread across each word's span in playback time, so any
        /// candidate window can read the load it contains in O(1). Built once per map, because a
        /// per-window rescan of the map is what would make the chunk sweep unaffordable.
        /// </summary>
        internal readonly struct LoadField
        {
            /// <summary>The whole map's rolling load: the same quantity the rhythmic-complexity bonus reports.</summary>
            public readonly double Total;

            private readonly double[] prefix;
            private readonly double t0;
            private readonly double binMs;
            private readonly int binCount;

            public LoadField(double total, double[] prefix, double t0, double binMs, int binCount)
            {
                Total = total;
                this.prefix = prefix;
                this.t0 = t0;
                this.binMs = binMs;
                this.binCount = binCount;
            }

            public double LoadBetween(double aMs, double bMs)
                => prefix.Length == 0
                    ? 0
                    : LyricDifficulty.At(prefix, (bMs - t0) / binMs, binCount) - LyricDifficulty.At(prefix, (aMs - t0) / binMs, binCount);
        }

        /// <summary>Everything the chunked axis measured for one map.</summary>
        internal sealed record Report
        {
            /// <summary>Number of chunks the map was cut into.</summary>
            public required int Count { get; init; }

            /// <summary>Pause-separated stretches of the map the grid was laid out in.</summary>
            public required int Segments { get; init; }

            /// <summary>Pauses longer than one chunk, i.e. segment boundaries.</summary>
            public required int Pauses { get; init; }

            /// <summary>Chunks sealed against merging BACKWARDS, one per pause (the chunk after it).</summary>
            public required int Sealed { get; init; }

            public required double ShortestSeconds { get; init; }
            public required double LongestSeconds { get; init; }

            /// <summary>The hardest chunk's strain.</summary>
            public required double Hardest { get; init; }

            /// <summary>The plain mean of the chunk strains.</summary>
            public required double Mean { get; init; }

            /// <summary>The decay-weighted mean, hardest first: the axis's own score.</summary>
            public required double Score { get; init; }

            /// <summary>The same mean with every window's rhythm boost neutralised.</summary>
            public required double ScoreNoRhythm { get; init; }

            /// <summary>Weighted characters on the timeline, at or above the credit floor.</summary>
            public required double LengthCharacters { get; init; }

            /// <summary>Every weighted character the chunks hold, before the credit.</summary>
            public required double CountedCharacters { get; init; }

            /// <summary>The length bonus the score was lifted by.</summary>
            public required double LengthBonusApplied { get; init; }

            /// <summary>The same lift with the rhythm boost neutralised.</summary>
            public required double LengthBonusAppliedNoRhythm { get; init; }

            /// <summary>Chunks a merge lifted.</summary>
            public required int Merged { get; init; }

            /// <summary>Runs the merge walk found.</summary>
            public required int MergeRuns { get; init; }

            /// <summary>The longest run, in chunks.</summary>
            public required int MergeLongestRun { get; init; }

            /// <summary>
            /// Every run the merge decided, in map order, as ABSOLUTE playback millisecond spans. The
            /// merged FLAGS alone would not do: two chunks lifted by different runs sit next to each
            /// other in the series and would glue into one span. A pass that is handed these prices
            /// them on its own stream instead of deciding its own (see <see cref="Compute"/>).
            /// </summary>
            public required IReadOnlyList<RunSpan> RunSpans { get; init; }

            /// <summary>The sizing the layout actually ran (a zero adaptive range falls back to fixed).</summary>
            public required string Sizing { get; init; }

            /// <summary>The merge mode the walk ran.</summary>
            public required string MergeMode { get; init; }

            /// <summary>
            /// The whole map's rolling load, read off the same prefix every window boost comes
            /// from. On this axis it is a diagnostic and nothing else: the rating already carries
            /// the rhythm through each chunk's own boost.
            /// </summary>
            public required double Load { get; init; }

            /// <summary><c>anchor x score x (1 + length bonus)</c>, the axis's star figure.</summary>
            public required double Stars { get; init; }

            /// <summary>The rating against the same model with the boost neutralised.</summary>
            public required double RhythmMultiplier { get; init; }

            /// <summary>
            /// Every chunk of the layout, in MAP order (the weighted mean sorts its own copy): the
            /// positions the fixture's scalars are built from, for the panel that draws the profile
            /// and for a test that has to see how far a merge actually spread.
            /// </summary>
            public required IReadOnlyList<Chunk> Chunks { get; init; }
        }

        /// <summary>One chunk of the layout, in playback milliseconds from the map's first word.</summary>
        internal readonly record struct Chunk(
            double StartMs,
            double EndMs,
            double Strain,
            double Plain,
            bool Merged,
            bool Sealed)
        {
            /// <summary>How much of the map this chunk covers.</summary>
            public double Seconds => (EndMs - StartMs) / 1000;
        }

        /// <summary>
        /// One RUN the merge walk found: a stretch of adjacent chunks that outscored its parts. The
        /// span is in absolute playback milliseconds, the same basis as the layout's own bounds (see
        /// <see cref="Chunk.StartMs"/>), so a run decided on one pass can be applied to another
        /// without a round trip through relative time.
        /// </summary>
        internal readonly record struct RunSpan(double StartMs, double EndMs);

        /// <summary>
        /// The characters whose glyph needs Shift held: the marks in <see cref="Typeability.PUNCTUATION"/>
        /// that sit on a shifted key. Derived from the candidate set rather than listed twice, so the
        /// two sides cannot drift - the same construction the sandbox's <c>shiftedMarks</c> uses.
        /// </summary>
        private const string SHIFTED_CANDIDATES = "~!@#$%^&*()_+{}|:\"<>?";

        private static bool IsShiftedMark(char c) => SHIFTED_CANDIDATES.IndexOf(c) >= 0 && Typeability.IsPunctuation(c);

        /// <summary>
        /// How many Shift keypresses a token costs under Literate, at <paramref name="cost"/> each: one
        /// per RUN of capitals or shifted marks, because the key is held across the run ("USA" pays one,
        /// "A B" pays two - the unshifted space resets it). A freestyle slot matches any key, so it
        /// never pays, and nothing is charged at all without the Literate mod, whose stream is the one
        /// that carries capitals and marks (see <see cref="Settings.shift_cost"/>).
        /// </summary>
        internal static double ShiftSurcharge(string token, bool literate, double cost)
        {
            if (!literate || !(cost > 0))
                return 0;

            double charges = 0;
            bool held = false;

            foreach (char c in token)
            {
                // Only the characters the stream can actually put on a cell carry a flag: everything
                // else (the syllable-split marker, junk a normalizer let through) is skipped WITHOUT
                // releasing Shift, exactly as the sandbox's shiftPressFlags skips it.
                if (!Typeability.IsTypeable(c) && !Typeability.IsFreestyle(c) && !Typeability.IsPunctuation(c))
                    continue;

                bool needs = !Typeability.IsFreestyle(c) && ((c >= 'A' && c <= 'Z') || IsShiftedMark(c));

                if (needs && !held)
                    charges += cost;

                held = needs;
            }

            return charges;
        }

        /// <summary>
        /// One word's CHARACTER mass, i.e. the word builder's own <c>chars</c>: every typeable
        /// character but the trailing space, plus the supported marks when the play is Literate, and
        /// never the freestyle slots (an any-key cell has no spelling). This is the quantity the grid's
        /// anchor is read from - see the anchor loop in <see cref="Compute"/>.
        /// </summary>
        private static double CharsOf(string token, bool literate)
        {
            double chars = 0;

            foreach (char c in token)
            {
                if (Typeability.IsTypeable(c) && c != ' ')
                    chars++;
                else if (literate && Typeability.IsPunctuation(c))
                    chars++;
            }

            return chars;
        }

        /// <summary>
        /// The CHARACTER mass one candidate anchor window holds: every word's own <see cref="CharsOf"/>
        /// pro-rated by the share of that word's span inside [<paramref name="aMs"/>,
        /// <paramref name="endMs"/>). This is the quantity a mod-shaped play reads its grid from, so
        /// it deliberately counts nothing but characters: not typability, not the rhythm boost and not
        /// the Shift surcharge, all of which move with the dials the mod changed.
        /// </summary>
        private static double CharacterMass(IReadOnlyList<LyricDifficulty.Word> words, int firstIndex, double aMs, double endMs, bool literate)
        {
            double mass = 0;

            for (int k = firstIndex; k < words.Count && words[k].Start < endMs; k++)
            {
                LyricDifficulty.Word other = words[k];
                double lo = Math.Max(aMs, other.Start), hi = Math.Min(endMs, other.End);

                if (hi > lo)
                    mass += CharsOf(other.Token, literate) * ((hi - lo) / other.Span);
            }

            return mass;
        }

        /// <summary>
        /// The window scale and the span rule the settings' judgement arm reads: Easy's doubled
        /// windows around the WORD, Hard Rock's normal windows around every point, and the live
        /// syllable arm otherwise.
        /// </summary>
        internal static (double Scale, LyricDifficulty.Shelter Shelter) Arm(Settings settings) => settings.complexity_mod switch
        {
            mod_easy => LyricDifficulty.Arm(LyricDifficulty.JudgementArm.Easy),
            mod_hardrock => LyricDifficulty.Arm(LyricDifficulty.JudgementArm.HardRock),
            _ => LyricDifficulty.Arm(LyricDifficulty.JudgementArm.None),
        };

        /// <summary>
        /// The capability curve S(t) as this axis's own dials read it: the WPM the fastest humans
        /// sustain for <paramref name="seconds"/>.
        /// </summary>
        internal static double Capability(double seconds, Settings settings)
            => settings.capability_base_wpm
               + settings.capability_burst_wpm * Math.Pow(settings.capability_ref_seconds / seconds, settings.capability_exponent);

        /// <summary>
        /// The rhythm multiplier one window earns from the load it holds: exactly 1 for a window with
        /// no forced pace change inside it, climbing to the same ceiling the bonus curve obeys.
        /// </summary>
        internal static double RhythmBoost(LoadField load, double aMs, double bMs, double seconds, Settings settings)
        {
            double ceiling = Math.Min(RhythmicComplexity.MaxMultiplier, 1 + settings.complexity_strength);
            double rate = load.LoadBetween(aMs, bMs) / Math.Max(1e-9, seconds);

            return Math.Min(ceiling, 1 + settings.complexity_strength * (1 - Math.Exp(-Math.Max(0, rate) / settings.boost_scale)));
        }

        /// <summary>
        /// Builds the load prefix the chunked axis reads its per-window boosts from: the same press
        /// stream the complexity scan reads (see <see cref="LyricDifficulty.BuildPresses"/>), priced
        /// by each keypress's own demand against a FIXED reference rather than against the map's
        /// peak, split by the rolling scan onto the keypresses it was measured on, and spread across
        /// each word's span on the timeline's bin grid.
        /// </summary>
        internal static LoadField BuildLoadPrefix(
            IReadOnlyList<LyricDifficulty.Word> words,
            double t0,
            int binCount,
            bool literate,
            Func<LyricDifficulty.Word, double> lineMultiplier,
            Settings settings)
        {
            if (words.Count == 0 || binCount <= 0)
                return new LoadField(0, Array.Empty<double>(), t0, settings.bin_ms, binCount);

            (double armScale, LyricDifficulty.Shelter shelter) = Arm(settings);
            List<RhythmicComplexity.Press> presses = LyricDifficulty.BuildPresses(
                words,
                literate,
                settings.complexity_tolerance * armScale,
                shelter,
                word =>
                {
                    double demand = word.Cells / 5 / (word.Span / 60000)
                                    / (Capability(word.Span / 1000, settings) * lineMultiplier(word));
                    double weight = Math.Min(1, Math.Max(0, demand) / settings.scan_weight_reference);

                    // The floor is the scan's own, shared with the complexity bonus: a keypress a
                    // constant pace can reach still gives up its timing, but never for free, which is
                    // what keeps the weighted bracket finite.
                    return Math.Max(RhythmicComplexity.WeightFloor, Math.Pow(weight, settings.scan_weight_exponent));
                });

            RhythmicComplexity.Split scan = RhythmicComplexity.Scan(presses, settings.complexity_horizon);

            var wordLoad = new double[words.Count];

            for (int j = 0; j < presses.Count; j++)
                wordLoad[presses[j].WordIndex] += scan.Press[j];

            var loadDensity = new double[binCount];

            for (int k = 0; k < words.Count; k++)
            {
                LyricDifficulty.Word word = words[k];
                double a = (word.Start - t0) / settings.bin_ms;
                double z = (word.End - t0) / settings.bin_ms;
                double per = wordLoad[k] / Math.Max(1e-9, z - a);

                for (int i = (int)Math.Floor(a); i <= Math.Min(binCount - 1, (int)Math.Floor(z)); i++)
                {
                    double lo = Math.Max(a, i), hi = Math.Min(z, i + 1);

                    if (hi > lo)
                        loadDensity[i] += per * (hi - lo);
                }
            }

            var prefix = new double[binCount + 1];

            for (int i = 0; i < binCount; i++)
                prefix[i + 1] = prefix[i] + loadDensity[i];

            return new LoadField(prefix[binCount], prefix, t0, settings.bin_ms, binCount);
        }

        /// <summary>
        /// THE SHIFT SURCHARGE's prefix, the sandbox's <c>preShift</c>: one word's own Shift keypresses
        /// (see <see cref="ShiftSurcharge"/>) spread across the word's span on the timeline's bin grid,
        /// so any candidate window can read the surcharge it holds in O(1). Null - the whole feature
        /// switched off - unless the play is Literate and the dial is on, which is what keeps it
        /// invisible to a default-stream reading and to every fixture taken at one.
        /// </summary>
        internal static double[]? BuildShiftPrefix(
            IReadOnlyList<LyricDifficulty.Word> words,
            double t0,
            int binCount,
            bool literate,
            Settings settings)
        {
            if (!literate || !(settings.shift_cost > 0) || words.Count == 0 || binCount <= 0)
                return null;

            var density = new double[binCount];

            foreach (LyricDifficulty.Word word in words)
            {
                double surcharge = ShiftSurcharge(word.Token, true, settings.shift_cost);

                if (!(surcharge > 0))
                    continue;

                double a = (word.Start - t0) / settings.bin_ms;
                double z = (word.End - t0) / settings.bin_ms;
                double per = surcharge / Math.Max(1e-9, z - a);

                for (int i = (int)Math.Floor(a); i <= Math.Min(binCount - 1, (int)Math.Floor(z)); i++)
                {
                    double lo = Math.Max(a, i), hi = Math.Min(z, i + 1);

                    if (hi > lo)
                        density[i] += per * (hi - lo);
                }
            }

            var prefix = new double[binCount + 1];

            for (int i = 0; i < binCount; i++)
                prefix[i + 1] = prefix[i] + density[i];

            return prefix;
        }

        /// <summary>
        /// The map's chunk layout, per-word judgement windows and chunk strains.
        /// <paramref name="typability"/> is the map's own score snapshot, read through
        /// <see cref="WindowMultiplier"/>: a delegate that returns 1 for every window is what a map
        /// the snapshot carries no scores for reads.
        /// <paramref name="preDecidedRuns"/> are the runs another pass already decided, which this
        /// one prices on its own stream instead of deciding its own (see <see cref="RateChunked"/>).
        /// Non-null also marks this pass as one a non-rate mod or a judgement arm SHAPED, so its grid
        /// is read off the material's character mass rather than off the strain (see the anchor loop
        /// below): the strain's answer moves with the very dial such a play changed, and a grid that
        /// moves with it can take back more than the extra material added. A play with neither of
        /// those passes null, and so takes the live grid rule untouched.</summary>
        internal static Report Compute(
            Timeline timeline,
            LoadField load,
            WindowMultiplier typability,
            Settings settings,
            bool literate = false,
            IReadOnlyList<RunSpan>? preDecidedRuns = null)
        {
            IReadOnlyList<LyricDifficulty.Word> words = timeline.Words;
            double t0 = timeline.T0;

            // The dials are clamped exactly as the sandbox clamps them, so a caller that edits one
            // out of range reads the same layout the sandbox would.
            double span = Math.Min(10, Math.Max(.2, settings.chunk_seconds)) * 1000;
            double decay = Math.Min(1, Math.Max(.01, settings.chunk_decay));
            double decayPower = Math.Min(4, Math.Max(0, settings.chunk_decay_power));
            string mergeMode = settings.chunk_merge_mode is merge_runs or merge_runs_gated or merge_pairs or merge_off
                ? settings.chunk_merge_mode
                : merge_runs;
            bool merge = mergeMode != merge_off;
            int mergePasses = Math.Min(24, Math.Max(0, settings.chunk_merge_passes));
            double lengthBonusStrength = Math.Min(.5, Math.Max(0, settings.chunk_length_bonus));
            double lengthFalloff = Math.Min(4, Math.Max(.25, settings.chunk_length_falloff));
            double lengthScale = Math.Min(5000, Math.Max(100, settings.chunk_length_scale));
            double lengthFloor = Math.Min(.95, Math.Max(0, settings.chunk_length_floor));
            string sizing = settings.chunk_sizing is sizing_fixed or sizing_nearest or sizing_adaptive
                ? settings.chunk_sizing
                : sizing_fixed;
            double adaptiveRange = Math.Min(.75, Math.Max(0, settings.chunk_adaptive_range));

            // A range of 0 leaves the search one length to choose from, and the honest thing for it to
            // do is nothing: the layout falls through to the plain fixed grid, which is off-character
            // by construction. That is what makes the dial's zero an exact undo of the experiment.
            if (sizing == sizing_adaptive && adaptiveRange <= 0)
                sizing = sizing_fixed;

            if (words.Count == 0)
                return Empty(sizing, mergeMode, load.Total);

            // PER-WORD WORK IS DONE ONCE, NOT ONCE PER CHUNK: judgement windows and the typeable
            // count are properties of a word (its syllables, its spelling and the judgement arm), and
            // a chunk sweep would otherwise recompute both for every word of every candidate window.
            var wordWindows = new double[words.Count];
            var wordTypeable = new double[words.Count];

            for (int i = 0; i < words.Count; i++)
            {
                LyricDifficulty.Word word = words[i];
                // The TYPEABLE count is read on the stream the play is typed in, so a Literate play
                // counts the punctuation marks its cells do, exactly as judgementWindows does in the
                // sandbox; the judgement windows themselves follow the arm.
                double cells = LyricDifficulty.TypeableCellCount(word.Token, literate);
                wordWindows[i] = JudgementWindows(word, cells, Arm(settings).Shelter);
                wordTypeable[i] = Math.Max(1, cells);
            }

            double maxWordSpan = 0;

            foreach (LyricDifficulty.Word word in words)
                maxWordSpan = Math.Max(maxWordSpan, word.Span);

            // `words` is sorted by start, so the only words that can overlap [a, b] are the ones
            // starting within one word span of `a`: everything before that ends before the window
            // opens. The bound is exact rather than heuristic, and the overlap test still decides.
            int FirstWordAt(double aMs)
            {
                double target = aMs - maxWordSpan;
                int lo = 0, hi = words.Count;

                while (lo < hi)
                {
                    int mid = (lo + hi) >> 1;

                    if (words[mid].Start < target)
                        lo = mid + 1;
                    else
                        hi = mid;
                }

                return lo;
            }

            double ChunkStrain(double aMs, double bMs, bool withRhythm = true)
            {
                double seconds = (bMs - aMs) / 1000;

                if (!(seconds > 1e-9))
                    return 0;

                // CHARACTERS ARE CHARACTERS; THE SHIFT SURCHARGE IS DEMAND ONLY. The surcharge is added
                // to the pace the chunk asks for and to nothing else - deliberately not to the character
                // counts, so the length bonus and the difficult-character figure a miss is priced
                // against stay character-based, and not to the pace figures, which are read off the
                // default stream.
                double cells = timeline.MassBetween(aMs, bMs);

                if (!(cells > 1e-9))
                    return 0;

                double shiftMass = timeline.ShiftMassBetween(aMs, bMs);
                double multiplier = typability(aMs, bMs);

                // The distinct judgement windows the chunk holds, pro-rated by each word's overlap
                // with it, per the cells it holds: the same premium the press walk charges, read at
                // the chunk rather than at the word.
                double windows = 0, typeable = 0;

                for (int wi = FirstWordAt(aMs); wi < words.Count; wi++)
                {
                    LyricDifficulty.Word word = words[wi];

                    if (word.Start >= bMs)
                        break;

                    double lo = Math.Max(aMs, word.Start), hi = Math.Min(bMs, word.End);

                    if (!(hi > lo))
                        continue;

                    double share = (hi - lo) / word.Span;
                    windows += wordWindows[wi] * share;
                    typeable += wordTypeable[wi] * share;
                }

                double density = 1 + settings.window_density_bonus * (windows / Math.Max(1e-9, typeable));
                double wpm = (cells + shiftMass) / 5 / (seconds / 60);
                double boost = withRhythm ? RhythmBoost(load, aMs, bMs, seconds, settings) : 1;

                return wpm / Capability(seconds, settings) / multiplier * density * boost;
            }

            // PAUSES BREAK THE GRID. A gap longer than one chunk is silence, not material: no chunk is
            // allowed to span it, or the resumed material would be charged at the silence's diluted
            // average. So the map is cut into SEGMENTS at those gaps, the chunk that would have
            // covered the pause starts at the next character instead, and that chunk - the first of
            // its segment - may not merge backwards, only forwards.
            var segments = new List<(double Start, double End)>();

            // ---------------------------------------------------------------------------------
            // THE OVERLAPPING LAYOUT (the sandbox's `profile_overlapping`), which is what the game
            // now ships. Nothing here is a chunk: the timeline is SAMPLED at a regular step and each
            // sample keeps the strongest window of a fixed family, so there is no pause gate, no
            // anchor, no character snapping and no merge - the runs below stay empty and the score
            // reads the samples in rank order, weighted by their own support.
            //
            // The family is the base duration doubled up to the horizon, plus the exact cap when it
            // is not on a doubling; each length is tried starting at the sample's centre, centred on
            // it, and ending at it. Windows outside the map keep their full length and read zero
            // mass there, exactly as the sandbox's prefix reads them. A window holding fewer than
            // `minimum_chars` weighted characters is refused instead of scored, which is the floor
            // this axis now shares with the peak: on sparse material the shortest eligible window
            // becomes the shortest one that actually holds that many characters.
            // ---------------------------------------------------------------------------------
            bool overlapping = settings.chunk_profile == profile_overlapping;
            List<Row>? overlappingRows = null;

            if (overlapping)
            {
                double mapEnd = t0;

                foreach (LyricDifficulty.Word word in words)
                    mapEnd = Math.Max(mapEnd, word.End);

                double baseMs = span;
                double maxMs = Math.Max(baseMs, Math.Min(120, Math.Max(1, settings.overlap_max_seconds)) * 1000);
                int subdivisions = Math.Max(1, Math.Min(8, (int)Math.Round(settings.overlap_samples)));
                double characterFloor = Math.Max(0, settings.minimum_chars);
                double stepMs = baseMs / subdivisions;

                var durations = new List<double> { baseMs };

                while (durations[^1] * 2 < maxMs - 1e-6)
                    durations.Add(durations[^1] * 2);

                if (maxMs > baseMs + 1e-6)
                    durations.Add(maxMs);

                // The floor is applied to the SCORED stream both ways round: a refused window scores
                // nothing with the rhythm boost on or off, the same as a silent one.
                double WindowStrain(double aMs, double bMs, bool withRhythm)
                    => characterFloor > 0 && timeline.MassBetween(aMs, bMs) < characterFloor - 1e-9
                        ? 0
                        : ChunkStrain(aMs, bMs, withRhythm);

                overlappingRows = new List<Row>();

                for (int index = 0; t0 + index * stepMs < mapEnd - 1e-6; index++)
                {
                    double a = t0 + index * stepMs;
                    double b = Math.Min(mapEnd, a + stepMs);
                    double centre = (a + b) / 2;
                    double strain = 0, plain = 0;
                    double windowStart = centre - baseMs / 2, windowSeconds = baseMs / 1000;

                    foreach (double duration in durations)
                    {
                        foreach (double position in new[] { 0d, .5, 1d })
                        {
                            double from = centre - duration * position, to = from + duration;
                            double value = WindowStrain(from, to, true);

                            if (value > strain)
                            {
                                strain = value;
                                windowStart = from;
                                windowSeconds = duration / 1000;
                            }

                            plain = Math.Max(plain, WindowStrain(from, to, false));
                        }
                    }

                    overlappingRows.Add(new Row
                    {
                        A = a,
                        B = b,
                        Mass = b - a,
                        Strain = strain,
                        Plain = plain,
                        WindowStartMs = windowStart,
                        WindowSeconds = windowSeconds,
                    });
                }

                segments.Add((t0, mapEnd));
            }

            // The chunk grid below is the OTHER layout: it lays chunks out, seals them at pauses and
            // merges the runs that pay. `rows`, the run counters and the run spans are declared here
            // so both layouts hand the scoring section the same thing.
            List<Row> rows = overlapping ? overlappingRows! : new List<Row>();
            int mergeRuns = 0, mergeLongestRun = 0;
            var runSpans = new List<RunSpan>();

            if (!overlapping)
            {
            double segStart = words[0].Start, segEnd = words[0].End;

            for (int i = 1; i < words.Count; i++)
            {
                LyricDifficulty.Word word = words[i];

                if (word.Start - segEnd > span)
                {
                    segments.Add((segStart, segEnd));
                    segStart = word.Start;
                }

                segEnd = Math.Max(segEnd, word.End);
            }

            segments.Add((segStart, segEnd));

            // ANCHOR AT THE START OF THE HARDEST CHUNK, which now has to sit inside one segment: a
            // window straddling a break is not a chunk at all. Candidate starts are the map's word
            // boundaries, so the anchor is exact over the grid that matters and the hardest material
            // is never split.
            //
            // WHICH RULE READS IT, and this is where a play's grid is decided. A play with neither
            // Literate nor a judgement arm IS the live path, so it reads the anchor off the STRAIN,
            // exactly as every published figure was computed: reading it off anything else would move
            // the grid, and with it the merge, of maps no mod ever touched. A play one of those has
            // actually shaped reads the material's own CHARACTER MASS instead (see
            // preDecidedRuns): there the grid must not be a function of the dial the mod just changed,
            // or a strictly denser stream could come back rated BELOW the one it is a superset of
            // (Literate under the Point arm, on Brothers [Insane]: the shift surcharge raised the
            // demand and a slid grid took away more than it added).
            double anchor = segments[0].Start, anchorScore = double.NegativeInfinity;
            int anchorSegment = 0;

            for (int wi = 0; wi < words.Count; wi++)
            {
                double a = words[wi].Start;

                while (anchorSegment + 1 < segments.Count && a > segments[anchorSegment].End + 1e-6)
                    anchorSegment++;

                (double Start, double End) seg = segments[anchorSegment];

                if (a > seg.End - 1e-6)
                    continue;

                double end = Math.Min(seg.End, a + span);

                double score = preDecidedRuns == null
                    ? ChunkStrain(a, end)
                    : CharacterMass(words, wi, a, end, literate);

                if (score > anchorScore)
                {
                    anchorScore = score;
                    anchor = a;
                }
            }

            if (!(anchorScore > double.NegativeInfinity))
                anchor = segments[0].Start;

            // THE SIZING BAND, in milliseconds. Both ends are clamped so a range that would push a
            // chunk below the model's own floor cannot, and the band is never empty.
            double spanLow = Math.Max(1, span * (1 - adaptiveRange));
            double spanHigh = Math.Max(spanLow, span * (1 + adaptiveRange));

            // THE CHUNK EDGES ARE CHARACTER BOUNDARIES: a chunk is a run of typed cells, so both of
            // its ends belong on a character. The fixed grid ignores that and lands wherever the
            // arithmetic falls, so the boundary set is not even built for it.
            // The boundaries are read on the stream the play is TYPED in, so a Literate play's chunks
            // may cut on a punctuation mark's own target: the marks are cells there, and a chunk is a
            // run of typed cells (the sandbox's own `characterBoundaries(words, literate)`).
            bool usesEdges = sizing != sizing_fixed;
            double[] edges = usesEdges ? LyricDifficulty.CharacterBoundaries(words, literate) : Array.Empty<double>();
            var segmentEdges = new List<double[]>();

            if (usesEdges)
            {
                foreach ((double start, double end) in segments)
                {
                    var own = new List<double>();

                    foreach (double edge in edges)
                    {
                        if (edge >= start - 1e-9 && edge <= end + 1e-9)
                            own.Add(edge);
                    }

                    segmentEdges.Add(own.ToArray());
                }
            }

            static int LowerBound(IReadOnlyList<double> list, double x)
            {
                int lo = 0, hi = list.Count;

                while (lo < hi)
                {
                    int mid = (lo + hi) >> 1;

                    if (list[mid] < x)
                        lo = mid + 1;
                    else
                        hi = mid;
                }

                return lo;
            }

            // The boundary closest to `target` inside [from, to] - one step of the rounding mode. An
            // exact tie goes to the LATER boundary, so a chunk that could round either way takes the
            // longer one, matching the search's own tie-break.
            static double? NearestEdge(double[] edges, double target, double from, double to)
            {
                int i = LowerBound(edges, target);
                double? after = i < edges.Length && edges[i] <= to + 1e-9 && edges[i] >= from - 1e-9 ? edges[i] : null;
                double? before = i > 0 && edges[i - 1] <= to + 1e-9 && edges[i - 1] >= from - 1e-9 ? edges[i - 1] : null;

                if (after == null)
                    return before;

                if (before == null)
                    return after;

                return target - before.Value < after.Value - target ? before : after;
            }

            // THE END THIS CHUNK SHOULD STOP AT: every character boundary in the allowed band is
            // measured and the highest-scoring window wins, so a chunk stretches over hard material
            // and is cut where the difficulty falls away. A tie goes to the longer chunk. Falls back
            // to the nominal length when the band holds no boundary at all, so the walk advances.
            double ChooseEnd(double a, (double Start, double End) seg, double[] segEdges)
            {
                double low = a + spanLow, high = Math.Min(seg.End, a + spanHigh);
                double rest = seg.End - a;
                var inBand = new List<double>();

                for (int i = LowerBound(segEdges, a + spanLow); i < segEdges.Length; i++)
                {
                    double edge = segEdges[i];

                    if (edge > high + 1e-9)
                        break;

                    inBand.Add(edge);
                }

                // The segment's own end joins the list whenever taking the whole rest cannot overshoot
                // by more than one minimum: it leaves no tail at all and is a boundary by construction.
                if (rest <= spanHigh + spanLow + 1e-9)
                    inBand.Add(seg.End);

                double? best = null, slack = null;
                double bestScore = double.NegativeInfinity, slackScore = double.NegativeInfinity;

                foreach (double edge in inBand)
                {
                    if (!(edge > a + 1e-9))
                        continue;

                    double score = ChunkStrain(a, edge);

                    // A SLIVER IS NOT A CHUNK: an end that leaves less than a minimum-length tail is
                    // held back to the second tier, so the tail stays in this chunk instead.
                    if (seg.End - edge >= spanLow - 1e-9)
                    {
                        if (score >= bestScore)
                        {
                            bestScore = score;
                            best = edge;
                        }
                    }
                    else if (score >= slackScore)
                    {
                        slackScore = score;
                        slack = edge;
                    }
                }

                if (best != null)
                    return best.Value;

                if (slack != null)
                    return slack.Value;

                // THE BAND HELD NO BOUNDARY AT ALL: the chunk runs to the next character instead of
                // landing between two of them, so it may come out past the maximum. Being a run of
                // typed cells is the stronger requirement, and a gap this size is bounded by the
                // pause that would have broken the segment.
                int index = LowerBound(segEdges, low);
                double? after = index < segEdges.Length ? segEdges[index] : null;

                return after != null && after.Value <= seg.End + 1e-9 ? after.Value : seg.End;
            }

            // The same search run backwards, for the material before the anchor: the start is chosen
            // rather than the end. Ascending with a strict improvement, so a tie again goes to the
            // longer chunk (the earlier start).
            double ChooseStart(double b, (double Start, double End) seg, double[] segEdges)
            {
                double low = Math.Max(seg.Start, b - spanHigh);
                double high = b - spanLow;
                var inBand = new List<double>();

                for (int i = LowerBound(segEdges, low); i < segEdges.Length && segEdges[i] <= high + 1e-9; i++)
                {
                    if (segEdges[i] > seg.Start + 1e-9)
                        inBand.Add(segEdges[i]);
                }

                if (seg.Start >= low - 1e-9)
                    inBand.Add(seg.Start);

                double? best = null, slack = null;
                double bestScore = double.NegativeInfinity, slackScore = double.NegativeInfinity;

                foreach (double start in inBand)
                {
                    if (!(start < b - 1e-9))
                        continue;

                    double score = ChunkStrain(start, b);

                    if (start - seg.Start >= spanLow - 1e-9)
                    {
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = start;
                        }
                    }
                    else if (score > slackScore)
                    {
                        slackScore = score;
                        slack = start;
                    }
                }

                if (best != null)
                    return best.Value;

                if (slack != null)
                    return slack.Value;

                // No boundary in the band: take the last one at or before it, so the chunk is still a
                // run of whole characters, and never start before the segment does.
                int index = LowerBound(segEdges, high + 1e-9);
                double? before = index > 0 ? segEdges[index - 1] : null;

                return before != null && before.Value >= seg.Start - 1e-9 ? before.Value : seg.Start;
            }

            // Lay every segment out. The anchor's segment is laid out from the anchor - backwards to
            // its own first character, then forwards - so the hardest material leads; the other
            // segments are laid out from their first character. Partial chunks keep their own length.
            // The first chunk of a segment opens after a pause, so it is sealed against merging
            // backwards.
            var bounds = new List<(double A, double B, bool Sealed)>();

            for (int segIndex = 0; segIndex < segments.Count; segIndex++)
            {
                (double Start, double End) seg = segments[segIndex];
                var part = new List<(double A, double B)>();
                bool anchored = anchor >= seg.Start - 1e-6 && anchor <= seg.End + 1e-6;
                double[] segEdges = usesEdges ? segmentEdges[segIndex] : Array.Empty<double>();

                // ONE WALK, THREE WAYS TO TAKE A STEP: 'fixed' steps a nominal length, 'nearest'
                // rounds that step to the character boundary closest to it, and 'adaptive' searches
                // the band for the window that scores highest.
                double StepEnd(double a)
                {
                    if (sizing == sizing_nearest)
                    {
                        double? edge = NearestEdge(segEdges, a + span, a + 1, seg.End);
                        return edge ?? Math.Min(seg.End, a + span);
                    }

                    return sizing == sizing_adaptive ? ChooseEnd(a, seg, segEdges) : Math.Min(seg.End, a + span);
                }

                double StepStart(double b)
                {
                    if (sizing == sizing_nearest)
                    {
                        double? edge = NearestEdge(segEdges, b - span, seg.Start, b - 1);
                        return edge ?? Math.Max(seg.Start, b - span);
                    }

                    return sizing == sizing_adaptive ? ChooseStart(b, seg, segEdges) : Math.Max(seg.Start, b - span);
                }

                void Forward(double from)
                {
                    for (double a = from; a < seg.End - 1e-6;)
                    {
                        double b = StepEnd(a);

                        if (!(b > a + 1e-9))
                            break;

                        part.Add((a, b));
                        a = b;
                    }
                }

                void Backward(double from)
                {
                    var before = new List<(double A, double B)>();

                    for (double b = from; b > seg.Start + 1e-6;)
                    {
                        double a = StepStart(b);

                        if (!(a < b - 1e-9))
                            break;

                        before.Add((a, b));
                        b = a;
                    }

                    before.Reverse();
                    part.AddRange(before);
                }

                // The anchor still leads its own chunk in every mode, so the hardest material keeps
                // its own window whichever way a step would otherwise have cut it.
                if (anchored)
                {
                    Backward(anchor);
                    Forward(anchor);
                }
                else
                    Forward(seg.Start);

                bool first = true;

                foreach ((double a, double b) in part)
                {
                    bounds.Add((a, b, first && segIndex > 0));
                    first = false;
                }
            }

            var chunkRows = new List<Row>(bounds.Count);

            foreach ((double a, double b, bool sealedRow) in bounds)
            {
                chunkRows.Add(new Row
                {
                    A = a,
                    B = b,
                    Sealed = sealedRow,
                    Strain = ChunkStrain(a, b),
                    Plain = ChunkStrain(a, b, withRhythm: false),
                });
            }

            rows = chunkRows;

            if (mergeMode == merge_pairs)
            {
                for (int pass = 0; pass < mergePasses; pass++)
                {
                    bool changed = false;

                    for (int i = 0; i + 1 < rows.Count; i++)
                    {
                        if (rows[i + 1].Sealed)
                            continue; // never across a pause

                        double joined = ChunkStrain(rows[i].A, rows[i + 1].B);
                        double joinedPlain = ChunkStrain(rows[i].A, rows[i + 1].B, withRhythm: false);

                        if (joined > rows[i].Strain + 1e-12 && joined > rows[i + 1].Strain + 1e-12)
                        {
                            rows[i].Strain = joined;
                            rows[i].Plain = joinedPlain;
                            rows[i].Merged = true;
                            rows[i + 1].Strain = joined;
                            rows[i + 1].Plain = joinedPlain;
                            rows[i + 1].Merged = true;
                            changed = true;
                        }
                    }

                    if (!changed)
                        break;
                }
            }

            if (preDecidedRuns != null)
            {
                // THE RUNS WERE DECIDED BEFORE THE NON-RATE MODS, so price them on the PLAYED stream.
                // The lift stays RAISE-ONLY, so a run frozen against one stream can never DEMOTE a
                // chunk whose own played strain came out above the run's figure.
                mergeRuns = preDecidedRuns.Count;

                foreach (RunSpan run in preDecidedRuns)
                {
                    double strain = ChunkStrain(run.StartMs, run.EndMs);
                    double plain = ChunkStrain(run.StartMs, run.EndMs, withRhythm: false);
                    int covered = 0;

                    foreach (Row row in rows)
                    {
                        if (!(row.B > run.StartMs + 1e-9 && row.A < run.EndMs - 1e-9))
                            continue;

                        covered++;

                        if (strain > row.Strain + 1e-12)
                        {
                            row.Strain = strain;
                            row.Plain = plain;
                            row.Merged = true;
                        }
                    }

                    if (covered > mergeLongestRun)
                        mergeLongestRun = covered;
                }
            }
            else if (mergeMode is merge_runs or merge_runs_gated)
            {
                // THE STEP GATE. The opening pair already has to outscore both of its halves, but an
                // EXTENSION only has to outscore the run as it stood - which lets a run absorb a
                // chunk individually harder than the figure it pays out. That chunk keeps its own
                // strain, because the lift below only ever RAISES; what it does is let the run's other
                // chunks collect a figure the incoming chunk did most of the earning on.
                // `runs-gated` adds the missing condition rather than changing the arithmetic: every
                // step must beat the chunk it is about to take as well as the run it is extending.
                bool gateEachStep = mergeMode == merge_runs_gated;
                var lifted = new double?[rows.Count];
                var liftedPlain = new double?[rows.Count];

                for (int i = 0; i + 1 < rows.Count; i++)
                {
                    if (rows[i + 1].Sealed)
                        continue; // a run may open at a pause, never cross it

                    double best = ChunkStrain(rows[i].A, rows[i + 1].B);
                    double bestPlain = ChunkStrain(rows[i].A, rows[i + 1].B, withRhythm: false);

                    if (!(best > rows[i].Strain + 1e-12 && best > rows[i + 1].Strain + 1e-12))
                        continue;

                    int end = i + 1;

                    while (end + 1 < rows.Count)
                    {
                        if (rows[end + 1].Sealed)
                            break;

                        double extended = ChunkStrain(rows[i].A, rows[end + 1].B);

                        if (!(extended > best + 1e-12))
                            break;

                        if (gateEachStep && !(extended > rows[end + 1].Strain + 1e-12))
                            break;

                        end++;
                        best = extended;
                        bestPlain = ChunkStrain(rows[i].A, rows[end].B, withRhythm: false);
                    }

                    mergeRuns++;

                    if (end - i + 1 > mergeLongestRun)
                        mergeLongestRun = end - i + 1;

                    runSpans.Add(new RunSpan(rows[i].A, rows[end].B));

                    for (int k = i; k <= end; k++)
                    {
                        if (lifted[k] == null || best > lifted[k]!.Value)
                        {
                            lifted[k] = best;
                            liftedPlain[k] = bestPlain;
                        }
                    }
                }

                for (int i = 0; i < rows.Count; i++)
                {
                    if (lifted[i] == null)
                        continue;

                    if (lifted[i]!.Value > rows[i].Strain + 1e-12)
                    {
                        rows[i].Strain = lifted[i]!.Value;
                        rows[i].Plain = liftedPlain[i]!.Value;
                        rows[i].Merged = true;
                    }
                }
            }
            }

            // THE WEIGHTED MEAN, hardest chunk first. `chunkDecay` at 1 reads the map's average
            // chunk; lower values pull the reading onto the hardest one.
            (double Score, List<Row> Sorted) Weighted(Func<Row, double> pick)
            {
                var sorted = new List<Row>(rows);
                sorted.Sort((x, y) => pick(y).CompareTo(pick(x)));

                double num = 0, den = 0;

                if (overlapping)
                {
                    // RANK MASS IS THE SAMPLE'S OWN SUPPORT. A sample covering twice as much map
                    // consumes twice as many ranks of the same decay, which is the sandbox's
                    // weightedProfile: subdividing identical samples cannot change the weighting,
                    // so the axis stays independent of the sampling resolution. The weights are
                    // integrated across each rank's interval, so a partial sample counts pro rata.
                    double rank = 0;

                    foreach (Row row in sorted)
                    {
                        double end = rank + row.Mass / span;
                        double weight = 0;

                        while (rank < end - 1e-12)
                        {
                            double boundary = Math.Floor(rank + 1e-10) + 1;
                            double next = Math.Min(end, boundary);
                            weight += (next - rank) * Math.Pow(decay, Math.Pow(boundary - 1, decayPower));
                            rank = next;
                        }

                        rank = end;
                        num += pick(row) * weight;
                        den += weight;
                    }

                    return (den > 0 ? num / den : 0, sorted);
                }

                for (int i = 0; i < sorted.Count; i++)
                {
                    double weight = Math.Pow(decay, Math.Pow(i, decayPower));
                    num += pick(sorted[i]) * weight;
                    den += weight;
                }

                return (den > 0 ? num / den : 0, sorted);
            }

            (double Score, List<Row> Sorted) withRhythm = Weighted(row => row.Strain);
            double scoreNoRhythm = Weighted(row => row.Plain).Score;

            // THE WEIGHTED LENGTH BONUS, and the count a miss is priced against. Characters in a chunk
            // at or above the map's own final score count in full; below it they lose credit
            // gradually, so material held near the top of the map's own range is worth more than the
            // same characters scattered under a spike.
            double creditFloor = lengthFloor * withRhythm.Score;
            double creditFloorNoRhythm = lengthFloor * scoreNoRhythm;

            static double Credit(double value, double threshold, double floor, double falloff)
            {
                if (!(threshold > 0) || !(value > floor + 1e-12))
                    return 0;

                double range = Math.Max(1e-9, threshold - floor);

                return Math.Min(1, Math.Pow((value - floor) / range, falloff));
            }

            double lengthCharacters = 0, lengthCharactersNoRhythm = 0, countedCharacters = 0;

            foreach (Row row in rows)
            {
                double mass = Math.Max(0, timeline.MassBetween(row.A, row.B));
                countedCharacters += mass;
                lengthCharacters += mass * Credit(row.Strain, withRhythm.Score, creditFloor, lengthFalloff);
                lengthCharactersNoRhythm += mass * Credit(row.Plain, scoreNoRhythm, creditFloorNoRhythm, lengthFalloff);
            }

            // The ramp tops out at an ABSOLUTE weighted-character count, so the bonus is gentle,
            // saturates rather than growing with map length, and cannot be diluted by anything the
            // map itself does.
            static double LengthRamp(double value, double scale) => Math.Min(1, Math.Max(0, value / scale));

            double lengthBonus = lengthBonusStrength * LengthRamp(lengthCharacters, lengthScale);
            double lengthBonusNoRhythm = lengthBonusStrength * LengthRamp(lengthCharactersNoRhythm, lengthScale);

            double anchorStars = settings.anchor;
            double envelopeStars = anchorStars * withRhythm.Score * (1 + lengthBonus);
            double noRhythmStars = anchorStars * scoreNoRhythm * (1 + lengthBonusNoRhythm);

            double shortest = double.PositiveInfinity, longest = 0;
            int merged = 0, sealedCount = 0;

            foreach (Row row in rows)
            {
                double seconds = (row.B - row.A) / 1000;
                shortest = Math.Min(shortest, seconds);
                longest = Math.Max(longest, seconds);
                merged += row.Merged ? 1 : 0;
                sealedCount += row.Sealed ? 1 : 0;
            }

            double mean = 0;

            foreach (Row row in rows)
                mean += row.Strain;

            mean = rows.Count > 0 ? mean / rows.Count : 0;

            return new Report
            {
                Count = rows.Count,
                Segments = segments.Count,
                Pauses = segments.Count - 1,
                Sealed = sealedCount,
                ShortestSeconds = rows.Count > 0 ? shortest : 0,
                LongestSeconds = longest,
                Hardest = withRhythm.Sorted.Count > 0 ? withRhythm.Sorted[0].Strain : 0,
                Mean = mean,
                Score = withRhythm.Score,
                ScoreNoRhythm = scoreNoRhythm,
                LengthCharacters = lengthCharacters,
                CountedCharacters = countedCharacters,
                LengthBonusApplied = lengthBonus,
                LengthBonusAppliedNoRhythm = lengthBonusNoRhythm,
                Merged = merged,
                MergeRuns = mergeRuns,
                MergeLongestRun = mergeLongestRun,
                RunSpans = runSpans,
                Sizing = overlapping ? "regular" : sizing,
                MergeMode = overlapping ? merge_off : mergeMode,
                Load = load.Total,
                Stars = envelopeStars,
                RhythmMultiplier = noRhythmStars > 0 ? envelopeStars / noRhythmStars : 1,
                Chunks = rows.Select(row => new Chunk(row.A, row.B, row.Strain, row.Plain, row.Merged, row.Sealed)).ToArray(),
            };
        }

        /// <summary>
        /// How many independent judgement WINDOWS a word's cells are read against under the live
        /// (syllable) shelter: one per authored syllable, the engine's own syllabification when the
        /// mapper did not subdivide, and one per cell for a word the syllabifier refuses outright.
        /// This is how finely the map subdivides its timing, which is the quantity the
        /// window-density premium prices.
        /// </summary>
        private static double JudgementWindows(LyricDifficulty.Word word, double cells, LyricDifficulty.Shelter shelter)
        {
            if (!(cells > 0))
                return 0;

            // A point arm reads a window per cell and a word arm reads one per word; only the live
            // syllable arm reads the map's own subdivision.
            if (shelter == LyricDifficulty.Shelter.Point)
                return cells;

            if (shelter == LyricDifficulty.Shelter.Word)
                return 1;

            if (word.Groups != null && word.Groups.Length >= 2)
                return word.Groups.Length;

            int syllables = LyricDifficulty.NaturalSyllables(word.Token);

            return syllables > 0 ? syllables : cells;
        }

        /// <summary>The report of a map the axis cannot rate at all: no typeable material, or a rate that is not a clock.</summary>
        internal static Report EmptyReport(Settings settings)
            => Empty(settings.chunk_sizing, settings.chunk_merge_mode, 0);

        private static Report Empty(string sizing, string mergeMode, double load) => new Report
        {
            Count = 0,
            Segments = 0,
            Pauses = 0,
            Sealed = 0,
            ShortestSeconds = 0,
            LongestSeconds = 0,
            Hardest = 0,
            Mean = 0,
            Score = 0,
            ScoreNoRhythm = 0,
            LengthCharacters = 0,
            CountedCharacters = 0,
            LengthBonusApplied = 0,
            LengthBonusAppliedNoRhythm = 0,
            Merged = 0,
            MergeRuns = 0,
            MergeLongestRun = 0,
            RunSpans = Array.Empty<RunSpan>(),
            Sizing = sizing,
            MergeMode = mergeMode,
            Load = load,
            Stars = 0,
            RhythmMultiplier = 1,
            Chunks = Array.Empty<Chunk>(),
        };

        /// <summary>One chunk while the layout and the merge walk through it.</summary>
        private sealed class Row
        {
            public double A;
            public double B;
            public bool Sealed;
            public bool Merged;
            public double Strain;
            public double Plain;

            /// <summary>
            /// The rank mass this row consumes, in milliseconds of SUPPORT. The chunk layout leaves
            /// it at 0 and is scored one rank per chunk; the overlapping layout sets it to the
            /// sample's own support, and that is what the rank weights integrate over - the same
            /// thing the sandbox's <c>weightedProfile</c> does with <c>row.b - row.a</c>.
            /// </summary>
            public double Mass;

            /// <summary>The winning window's start and length, for the overlapping layout's report.</summary>
            public double WindowStartMs;
            public double WindowSeconds;
        }
    }
}
