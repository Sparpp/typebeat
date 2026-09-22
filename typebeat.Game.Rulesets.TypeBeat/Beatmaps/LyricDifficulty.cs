// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// Star rating for a lyric map: how fast the map asks you to type, measured against how fast
    /// the fastest humans can type over many window durations, then adjusted for how hard the
    /// lyrics themselves are to type and for how much the map forces the player to change pace.
    ///
    /// <para>THE TIMELINE. Every typeable word is a block running from its onset to the end of its
    /// sung span, in REAL seconds (beatmap time divided by the clock <c>rate</c>, span floored at
    /// <c>min_span_ms</c>), and the block's cells are spread uniformly across that span into
    /// <c>timeline_bin_ms</c> bins. A word's cells are its fixed-key characters plus
    /// <c>freestyle_cost_weight</c> per freestyle slot, plus ONE TRAILING SPACE when the next word
    /// belongs to the same line: the spacebar is a real keypress and the pace figures have always
    /// counted it, so the rating counts it too.</para>
    ///
    /// <para>THE CAPABILITY CURVE. <c>S(t) = capability_base_wpm + capability_burst_wpm *
    /// (capability_ref_seconds / t) ^ capability_exponent</c> is the WPM the fastest humans sustain
    /// for t seconds. A window of t seconds holding <c>c</c> cells therefore has ratio
    /// <c>(c / 5) / (t / 60) / S(t)</c>, its pace as a fraction of the record pace FOR ITS OWN
    /// DURATION, which is what makes a 1.5 second burst and a 60 second verse comparable numbers.
    /// A window is measured against <c>S(t)</c> times that window's TYPABILITY multiplier, so hard
    /// lyrics buy the window a slower reference and therefore a harder ratio.</para>
    ///
    /// <para>THE PEAK sets the RANGE. <c>ratio_0</c> is the best ratio at any eligible duration
    /// anywhere on the map, and <c>stars_at_human_peak / (1 + envelope_range) * ratio_0</c> is the
    /// FLOOR of the map's range: what the hardest window alone is worth. A window is eligible only
    /// if it carries at least <see cref="MinimumWindowChars"/> weighted characters AND lasts at
    /// least the minimum duration that cutoff implies (see
    /// <see cref="ShortestCharacterWindow"/>), so a dense burst cannot set the floor from a window
    /// the player never has to hold.</para>
    ///
    /// <para>THE EXACT PEAK. The duration schedule and the 50 ms bin grid both quantise the
    /// reported peak, and the sandbox carries a fork experiment that REFINES it further onto the
    /// map's own character boundaries (see <see cref="RefinePeakWindow"/> for why the boundary set
    /// is the whole answer rather than a hill climb, and <see cref="DensityIndex"/> for the exact
    /// mass arithmetic that makes enumerating it cheap). The sandbox documents OFF as the shipped
    /// model, and <see cref="exact_peak"/> holds the game there, so no rating depends on the
    /// experiment unless it is switched on deliberately.</para>
    ///
    /// <para>THE ENVELOPE decides where inside that range the map lands. Every bin takes
    /// <c>env[i]</c>, the best ratio of any window CONTAINING it, and
    /// <c>d = min(1, env[i] / ratio_0)</c> is how hard that bin is as a fraction of the peak. The
    /// map's DIFFICULT CHARACTERS are <c>N = sum_i cells(i) * d^envelope_power</c>, with no cutoff.
    /// The range fills as <c>1 - exp(-N / envelope_chars)</c>, and the rating is
    /// <c>floor * (1 + envelope_range * fill)</c>.</para>
    ///
    /// <para>THE TWO EXPERIMENTS ARE SEPARATE MULTIPLIERS ON THAT RATING. TYPABILITY
    /// (<see cref="TypabilityIndex"/>) is the reference-speed adjustment and can contribute exactly
    /// nothing for a map whose scores do not clear its two gates. RHYTHMIC COMPLEXITY
    /// (<see cref="RhythmicComplexity"/>) is a multiplier for forced pace change, and is exactly 1
    /// at strength 0. With both off this file computes the envelope model and nothing else.</para>
    ///
    /// <para>A MAP SHORTER THAN THE SMALLEST WINDOW rates exactly zero: no scheduled window fits on
    /// its timeline, so <c>ratio_0</c> is 0 and so is the range it scales.</para>
    ///
    /// <para>Kept in step with the Star Rating Sandbox, which is where this model is tuned:
    /// <c>tools/star-rating-sandbox/model.mjs</c> is the same arithmetic with every constant lifted
    /// into a dial. Every constant below is that sandbox's currently active setting.</para>
    ///
    /// <para>One reading of a map, above. The sandbox also carries a second one - the CHUNKED
    /// ENDURANCE AXIS, which cuts the map into equal chunks and scores each as a window of its own -
    /// and <see cref="ComputeDetail"/> can take it instead through <see cref="EnduranceAxis"/>. That
    /// axis reads none of the peak, envelope, stamina or catch-up machinery below; see
    /// <see cref="ChunkedEndurance"/> for what it does read and for the fixture it is pinned
    /// against. The default is <see cref="EnduranceAxis.Envelope"/>, so every existing caller -
    /// including the pp lab's baseline - reads exactly the model documented here.</para>
    /// </summary>
    public static class LyricDifficulty
    {
        // The capability curve S(t), the WPM the fastest humans sustain for t seconds.
        private const double capability_base_wpm = 220; // the asymptote a human holds indefinitely
        private const double capability_burst_wpm = 221; // how much more a burst is worth at the reference
        private const double capability_ref_seconds = 1.35; // the burst record's own duration
        private const double capability_exponent = 0.35; // how fast the burst premium decays with t

        /// <summary>
        /// What a map that types at the record pace THROUGHOUT WITH ITS RANGE FILLED is worth, and
        /// the anchor every other rating is read against. Stars are LINEAR in it, so moving it alone
        /// is a pure rescale that cannot reorder anything.
        /// </summary>
        private const double stars_at_human_peak = 12.0;

        /// <summary>
        /// How much the map can add ON TOP of its hardest window, as a fraction of that floor: a map
        /// whose range is completely filled rates <c>1 + envelope_range</c> times what its peak
        /// alone is worth.
        /// </summary>
        private const double envelope_range = 0.3333;

        // How sharply a character's weight falls away from the peak: weight is (env/ratio_0)^this.
        private const double envelope_power = 8;

        // The difficult characters that fill 63% (1 - 1/e) of the range.
        private const double envelope_chars = 500;

        private const double timeline_bin_ms = 50; // timeline resolution

        /// <summary>
        /// Whether the peak window is refined off the duration schedule and onto the map's own
        /// character boundaries. This is the sandbox's <c>exactPeak</c> fork experiment; its own
        /// comment calls OFF "the shipped model", and every fixture in the suite pins that reading.
        /// </summary>
        private const bool exact_peak = false;

        /// <summary>The shortest window the model will rate a map from, in seconds.</summary>
        public const double MinimumWindowSeconds = 1.36;

        /// <summary>
        /// The hardest window must carry at least this many weighted characters. The shortest
        /// interval holding them raises the minimum duration above
        /// <see cref="MinimumWindowSeconds"/> when it is longer; it can never lower it. Zero
        /// disables the cutoff, which is the sandbox's own default.
        /// </summary>
        public const double MinimumWindowChars = 0;

        /// <summary>
        /// The duration every map's peak is re-expressed at for the Target WPM readout (see
        /// <see cref="ModelResult.TargetWpm"/>). Display only: nothing in the rating reads it.
        /// </summary>
        public const double TargetWindowSeconds = 30;

        /// <summary>
        /// What one FREESTYLE slot is worth, as a fraction of an ordinary cell: a real keypress with
        /// a real deadline, which merely has no letter to find.
        /// </summary>
        private const double freestyle_cost_weight = 0.25;

        private const double min_span_ms = 50; // floor a word's sung span, in beatmap time, before the rate divide
        private const double density_epsilon = 1e-9;

        /// <summary>Maps longer than this (at the selected rate) are refused rather than rated.</summary>
        private const double max_map_seconds = 1800;

        /// <summary>
        /// One word reduced to what the timeline and the complexity scan need: when it is sung, for
        /// how long, how many cells it costs, the lyric line it belongs to, its own text (the
        /// complexity scan reads the authored token), its authored syllable boundaries in real
        /// time, and the typability of the line it sits on.
        /// </summary>
        internal readonly struct Word
        {
            public readonly double Start;
            public readonly double Span;
            public readonly double Cells;
            public readonly int LineIndex;
            public readonly string Token;

            /// <summary>Start time of each authored syllable group, or null when the word carries none.</summary>
            public readonly double[]? Groups;

            /// <summary>
            /// End time of each group in <see cref="Groups"/>, when the groups are STRETCHES rather
            /// than contiguous syllables: a word cut by rests is sung in pieces whose spans do not
            /// meet, and a rest between two of them belongs to no stretch of typing. Null for every
            /// ordinary word, whose group g ends exactly where group g + 1 begins.
            /// </summary>
            public readonly double[]? GroupEnds;

            public readonly double Z;
            public readonly bool Scored;

            public double End => Start + Span;

            public Word(double start, double span, double cells, int lineIndex, string token, double[]? groups, double z, bool scored, double[]? groupEnds = null)
            {
                Start = start;
                Span = span;
                Cells = cells;
                LineIndex = lineIndex;
                Token = token;
                Groups = groups;
                GroupEnds = groupEnds;
                Z = z;
                Scored = scored;
            }
        }

        private readonly struct Interval
        {
            public readonly double Lo;
            public readonly double Hi;

            public Interval(double lo, double hi)
            {
                Lo = lo;
                Hi = hi;
            }
        }

        /// <summary>
        /// Everything the rating computed, for the surfaces that read more than a single star
        /// number: the peak window the floor came from, the difficult-character count the miss
        /// penalty is measured against, the Target WPM readout, and what each adjustment did.
        /// </summary>
        public readonly struct ModelResult
        {
            /// <summary>Final star rating: the envelope's rating times the rhythmic-complexity multiplier.</summary>
            public required double Stars { get; init; }

            /// <summary>The peak ratio the whole range is scaled by: <c>wpm / capability(seconds)</c> at its own duration.</summary>
            public required double Peak { get; init; }

            /// <summary>Duration of the peak window, in seconds, after exact refinement.</summary>
            public required double PeakSeconds { get; init; }

            /// <summary>Weighted cells the peak window carries.</summary>
            public required double PeakCells { get; init; }


            /// <summary>Start of the peak window, in seconds from the map's first sung word.</summary>
            public required double PeakStartSeconds { get; init; }

            /// <summary>The effective minimum peak duration the character cutoff produced, or null when no window qualifies.</summary>
            public required double? MinimumSeconds { get; init; }

            /// <summary>THE DIFFICULT CHARACTERS: every cell weighted by how close its bin sits to the peak.</summary>
            public required double DifficultCharacters { get; init; }

            /// <summary>How much of the envelope's range those characters fill, in [0, 1).</summary>
            public required double Fill { get; init; }

            /// <summary>What the peak window alone is worth, in stars: the floor of the map's range.</summary>
            public required double Floor { get; init; }

            /// <summary>Total weighted cells on the timeline.</summary>
            public required double Cells { get; init; }

            /// <summary>Map length in seconds at the selected rate.</summary>
            public required double DurationSeconds { get; init; }

            /// <summary>
            /// The map's fastest window re-expressed as the WPM an equally demanding
            /// <see cref="TargetWindowSeconds"/> window would need. Typability and rhythm are left out
            /// of the window choice and the translation, so this need not be the window
            /// <see cref="Peak"/> came from.
            ///
            /// <para>Display only, and on the CHUNKED axis it is 0: that axis reads none of the
            /// schedule scan this comes from. The pace figure the game publishes is its own
            /// (<see cref="LyricPaceStatistics.TargetWpm"/>), which both axes share.</para>
            /// </summary>
            public required double TargetWpm { get; init; }

            /// <summary>The multiplier rhythmic complexity put on the envelope's rating.</summary>
            public required double ComplexityMultiplier { get; init; }

            /// <summary>Forced pace change the rolling scan measured, before the curve.</summary>
            public required double ComplexityLoad { get; init; }

            /// <summary>Share of the map's weighted cells that carried a typability score.</summary>
            public required double TypabilityScoredFraction { get; init; }

            /// <summary>Character-weighted mean z of the map, 0 when typability is off.</summary>
            public required double TypabilityMeanZ { get; init; }

            /// <summary>Whether the map cleared the scored-fraction gate, so typability moved its rating.</summary>
            public required bool TypabilityApplied { get; init; }
        }

        /// <summary>
        /// Which reading of a map the rating is taken from. This is the sandbox's own
        /// <c>enduranceAxis</c> setting, and the game ships the CHUNKED one (see <see cref="Live"/>):
        /// the difficulty calculator, the pp formula and the pace figures all rate a map the way the
        /// sandbox's live configuration does. <see cref="Envelope"/> is the model this class
        /// documents, kept and still selectable by name as the sandbox's baseline.
        /// </summary>
        public enum EnduranceAxis
        {
            /// <summary>The shipped envelope model: floor from the hardest window, range filled by the rest.</summary>
            Envelope,

            /// <summary>The sandbox's chunked endurance axis, read off equal chunks of the map (see <see cref="ChunkedEndurance"/>).</summary>
            Chunked,
        }

        /// <summary>
        /// WHICH JUDGEMENT ARM a play is rated in: the mods that change the engine's own windows.
        /// The sandbox reads them through its <c>complexityMod</c> dial, and they move a rating
        /// because the rhythm arm prices the intervals a press may land in: Easy doubles every
        /// window and shelters the whole WORD, Hard Rock keeps normal windows but puts every cell on
        /// its own point target. Neither changes the map, only the windows it is judged in - which
        /// is why the star rating has to be told which one is in play.
        /// </summary>
        public enum JudgementArm
        {
            /// <summary>The live span rule: a cell is dead on anywhere inside its own syllable.</summary>
            None,

            /// <summary>TypeBeatModEasy: doubled windows, sheltered by the word rather than the syllable.</summary>
            Easy,

            /// <summary>TypeBeatModHardRock: normal windows, every cell on its own point target.</summary>
            HardRock,
        }

        /// <summary>Which span a cell's judgement interval is drawn around (see <see cref="PressIntervals"/>).</summary>
        internal enum Shelter
        {
            Syllable,
            Word,
            Point,
        }

        /// <summary>The window scale and span rule one judgement arm reads the engine through.</summary>
        internal static (double Scale, Shelter Shelter) Arm(JudgementArm arm) => arm switch
        {
            JudgementArm.Easy => (2.0, Shelter.Word),
            JudgementArm.HardRock => (1.0, Shelter.Point),
            _ => (1.0, Shelter.Syllable),
        };

        /// <summary>
        /// The axis the GAME rates maps on: the Star Rating Sandbox's own live <c>enduranceAxis</c>
        /// setting, which is the chunked one. It is the default every caller gets, so the difficulty
        /// calculator, the pp formula and the pace figures all read one rating rather than three,
        /// and <see cref="EnduranceAxis.Envelope"/> is the model this class documents - kept, and
        /// still selectable by name, as the sandbox's baseline.
        /// </summary>
        public const EnduranceAxis Live = EnduranceAxis.Chunked;

        /// <summary>
        /// A per-line score source: the typability z a line's TYPED text is worth, or false when the
        /// map's data carries no score for that line. Typability is measured per line and carried by
        /// every word on it, so this is read once per lyric line rather than per word.
        /// </summary>
        internal delegate bool LyricScoreSource(string typedText, out double z);

        /// <summary>
        /// The shipped index, which is what every caller but the chunked-axis fixture reads: the
        /// sandbox's own two gates are <see cref="TypabilityIndex.Strength"/> here and
        /// <see cref="TypabilityIndex.MinScoredFraction"/> on the map.
        /// </summary>
        internal static bool ShippedScores(string typedText, out double z)
        {
            z = 0;

            if (TypabilityIndex.Strength <= 0 || !TypabilityIndex.TryScore(typedText, out TypabilityIndex.Score score))
                return false;

            z = TypabilityIndex.ClampZ(score.Z);
            return true;
        }

        /// <summary>
        /// A snapshot with no per-line scores at all, which is the state the chunked axis's fixture
        /// was emitted in (<c>songs.json</c> carries no typability block, so the sandbox's
        /// <c>typabilityFor</c> returns null and every line reads z = 0).
        /// </summary>
        internal static bool NoScores(string typedText, out double z)
        {
            z = 0;
            return false;
        }

        /// <summary>
        /// Stars for the given lyric lines, under a clock <paramref name="rate"/> (1 = no mod) and
        /// the cell stream <paramref name="literate"/> selects (false = the default stripped,
        /// lower-case stream; true = the authored chars, marks included, i.e. the Literate mod).
        /// </summary>
        public static double Compute(IEnumerable<LyricLine> lines, double rate = 1, bool literate = false, EnduranceAxis enduranceAxis = Live, JudgementArm judgementArm = JudgementArm.None)
            => ComputeDetail(lines, rate, literate, enduranceAxis, judgementArm).Stars;

        /// <summary>
        /// The full rating: the stars plus the peak window, difficult-character count and
        /// adjustment readings the pp formula and the pace figures read.
        ///
        /// <para><paramref name="enduranceAxis"/> selects the reading. <see cref="EnduranceAxis.Envelope"/>
        /// is the shipped model documented on this class; <see cref="EnduranceAxis.Chunked"/> is the
        /// sandbox's chunked endurance axis, which reads none of the peak, envelope, stamina or
        /// catch-up machinery and carries its own star scale.</para>
        /// </summary>
        public static ModelResult ComputeDetail(IEnumerable<LyricLine> lines, double rate = 1, bool literate = false, EnduranceAxis enduranceAxis = Live, JudgementArm judgementArm = JudgementArm.None)
            => ComputeDetail(lines, rate, literate, enduranceAxis, judgementArm, ShippedScores);

        /// <summary>
        /// As above, with the per-line SCORE SOURCE chosen by the caller. The shipped source is
        /// <see cref="ShippedScores"/>; a fixture that has to agree with a reference reading taken
        /// before typability existed passes <see cref="NoScores"/>, exactly as the chunked axis's
        /// own fixture does through <see cref="RateChunked"/>.
        /// </summary>
        internal static ModelResult ComputeDetail(IEnumerable<LyricLine> lines, double rate, bool literate, EnduranceAxis enduranceAxis, JudgementArm judgementArm, LyricScoreSource scores)
        {
            var lineList = lines as IReadOnlyList<LyricLine> ?? lines.ToList();

            if (enduranceAxis == EnduranceAxis.Chunked)
            {
                // The arm is a DIAL OF THE RHYTHM AXIS rather than a filter over the finished rating,
                // exactly as it is in the sandbox where it rides in the complexity settings.
                ChunkedEndurance.Settings armSettings = ChunkedEndurance.Live with
                {
                    complexity_mod = judgementArm switch
                    {
                        JudgementArm.Easy => ChunkedEndurance.mod_easy,
                        JudgementArm.HardRock => ChunkedEndurance.mod_hardrock,
                        _ => ChunkedEndurance.mod_none,
                    },
                };

                return ComputeChunked(lineList, rate, literate, scores, armSettings);
            }

            var empty = new ModelResult
            {
                Stars = 0,
                Peak = 0,
                PeakSeconds = 0,
                PeakCells = 0,
                PeakStartSeconds = 0,
                MinimumSeconds = null,
                DifficultCharacters = 0,
                Fill = 0,
                Floor = 0,
                Cells = 0,
                DurationSeconds = 0,
                TargetWpm = 0,
                ComplexityMultiplier = 1,
                ComplexityLoad = 0,
                TypabilityScoredFraction = 0,
                TypabilityMeanZ = 0,
                TypabilityApplied = false,
            };

            if (lineList.Count == 0 || rate <= 0)
                return empty;

            var words = BuildWords(lineList, rate, literate, scores, min_span_ms);

            if (words.Count == 0)
                return empty;

            words.Sort((a, b) => a.Start.CompareTo(b.Start));

            double t0 = words[0].Start;
            double t1 = double.NegativeInfinity;

            foreach (var w in words)
                t1 = Math.Max(t1, w.End);

            double durationSeconds = (t1 - t0) / 1000.0;

            if (durationSeconds > max_map_seconds)
                throw new InvalidOperationException("Maps must fit within 30 minutes at the selected rate.");

            int nb = Math.Max(1, (int)Math.Ceiling((t1 - t0) / timeline_bin_ms) + 1);
            BuildTimeline(words, t0, nb, timeline_bin_ms, out double[] dens, out double[] pre, out double[] preZ, out double[] preScored);
            double[] env = new double[nb];

            double scoredFraction = pre[nb] > 0 ? preScored[nb] / pre[nb] : 0;
            bool applies = TypabilityIndex.Strength > 0 && scoredFraction >= TypabilityIndex.MinScoredFraction;

            // Character-weighted typability of a span given in BIN coordinates, with fractional
            // endpoints interpolating between bins.
            double typabilityMultiplier(double a, double b)
            {
                double weight = At(pre, b, nb) - At(pre, a, nb);

                if (!applies || weight <= 0)
                    return 1;

                double z = (At(preZ, b, nb) - At(preZ, a, nb)) / weight;
                return TypabilityIndex.Multiplier(z);
            }

            var minimumWindow = ShortestCharacterWindow(words, MinimumWindowChars);
            double minimumSeconds = MinimumWindowChars > 0
                ? Math.Max(MinimumWindowSeconds, minimumWindow?.Seconds ?? double.PositiveInfinity)
                : MinimumWindowSeconds;

            var schedule = new List<double> { MinimumWindowSeconds };

            for (double t = 1.5; t <= 15; t += 1.5)
                schedule.Add(t);

            for (double t = 20; t <= 60; t += 5)
                schedule.Add(t);

            for (double t = 90; t <= durationSeconds; t += 30)
                schedule.Add(t);

            double peak = 0;
            double peakSeconds = 0, peakCells = 0, peakStartSeconds = 0;
            var windows = new List<(double Seconds, double Ratio, bool Eligible)>();

            // TARGET WPM'S OWN WINDOW, which is NOT necessarily the rating's peak. The figure
            // answers "what raw speed does this map ask for over N seconds", so it is read off the
            // hardest window by speed ALONE - its own WPM over what is sustainable for its own
            // length - with the typability multiplier left out of both the window-choice and the
            // translation. A slower-to-type window can win the rating while a plain faster one
            // still asks for more speed, and mixing the two would make the published figure move
            // with a dial it is not about. Display only: nothing in the rating reads it.
            double speedTargetRatio = 0, speedTargetSeconds = 0, speedTargetCells = 0;

            void ConsiderSpeedWindow(double seconds, double cells, double startSeconds)
            {
                if (!(seconds > 0) || !(cells > 0))
                    return;

                double speedRatio = cells / 5 / (seconds / 60) / capability(seconds);

                if (speedRatio <= speedTargetRatio)
                    return;

                speedTargetRatio = speedRatio;
                speedTargetSeconds = seconds;
                speedTargetCells = cells;
            }

            if (minimumWindow is { } characterWindow && characterWindow.Seconds >= MinimumWindowSeconds)
            {
                double multiplier = typabilityMultiplier(
                    (characterWindow.Start - t0) / timeline_bin_ms,
                    (characterWindow.Start + characterWindow.Seconds * 1000 - t0) / timeline_bin_ms);

                double wpm = characterWindow.Cells / 5 / (characterWindow.Seconds / 60);
                double ratio = wpm / capability(characterWindow.Seconds) / multiplier;

                peak = ratio;
                peakSeconds = characterWindow.Seconds;
                peakCells = characterWindow.Cells;
                peakStartSeconds = (characterWindow.Start - t0) / 1000;
                ConsiderSpeedWindow(characterWindow.Seconds, characterWindow.Cells, (characterWindow.Start - t0) / 1000);
                windows.Add((characterWindow.Seconds, ratio, true));
            }

            foreach (double t in schedule)
            {
                if (t > Math.Max(durationSeconds, MinimumWindowSeconds))
                    continue;

                int wb = Math.Max(1, (int)Math.Floor(t * 1000 / timeline_bin_ms + 0.5));

                if (wb > nb)
                    continue;

                int ns = nb - wb + 1;
                double[] ratios = new double[ns];
                int[] dq = new int[ns];
                double cap = capability(t);
                double denominator = 5 * (t / 60) * cap;
                double best = 0, bestStart = 0, bestMultiplier = 1;

                for (int st = 0; st < ns; st++)
                {
                    double cells = pre[st + wb] - pre[st];
                    double multiplier = typabilityMultiplier(st, st + wb);
                    ratios[st] = cells / (denominator * multiplier);

                    if (cells > best)
                    {
                        best = cells;
                        bestStart = st;
                        bestMultiplier = multiplier;
                    }
                }

                double ratio = best / 5 / (t / 60) / cap / bestMultiplier;

                // The character cutoff gates the PEAK only: the envelope below is unchanged by it,
                // so the knob moves hardest-window selection without reweighting the rest of the map.
                bool eligible = best + 1e-9 >= MinimumWindowChars && t + 1e-12 >= minimumSeconds;

                // This duration's fastest window is its max-cells one, so the raw-speed race the
                // target is read off is exactly `best` at `bestStart`. No typability, no boost.
                if (eligible)
                    ConsiderSpeedWindow(t, best, bestStart * timeline_bin_ms / 1000);

                if (eligible && ratio > peak)
                {
                    peak = ratio;
                    peakSeconds = t;
                    peakCells = best;
                    peakStartSeconds = bestStart * timeline_bin_ms / 1000;
                }

                windows.Add((t, ratio, eligible));

                int head = 0, tail = 0, next = 0;

                for (int i = 0; i < nb; i++)
                {
                    while (next <= i && next < ns)
                    {
                        while (tail > head && ratios[dq[tail - 1]] <= ratios[next])
                            tail--;

                        dq[tail++] = next++;
                    }

                    while (tail > head && dq[head] < i - wb + 1)
                        head++;

                    if (tail > head && ratios[dq[head]] > env[i])
                        env[i] = ratios[dq[head]];
                }
            }

            // THE EXACT PEAK. The schedule scan above only SEEDS this: the winning window's
            // duration and start come from it, and the sweep then walks both edges onto character
            // boundaries and off the schedule's fixed durations.
            var seed = windows.Where(w => w.Eligible).OrderByDescending(w => w.Ratio).FirstOrDefault();

            if (exact_peak && seed.Ratio > 0)
            {
                DensityIndex index = DensityIndex.Build(words);

                PeakCandidate best = RefinePeakWindow(
                    index,
                    (aMs, bMs, seconds, cells) =>
                    {
                        double multiplier = typabilityMultiplier((aMs - t0) / timeline_bin_ms, (bMs - t0) / timeline_bin_ms);
                        double wpm = cells / 5 / (seconds / 60);
                        // Every candidate the refinement measures is gated by the same minimums as
                        // the peak, so this is where the target window's best case is found too.
                        ConsiderSpeedWindow(seconds, cells, (aMs - t0) / 1000);
                        return new PeakCandidate(wpm / capability(seconds) / multiplier, seconds, cells, aMs, multiplier);
                    },
                    minimumSeconds,
                    MinimumWindowChars,
                    schedule);

                if (best.Found && best.Ratio > peak + 1e-12)
                {
                    peak = best.Ratio;
                    peakSeconds = best.Seconds;
                    peakCells = best.Cells;
                    peakStartSeconds = (best.StartMs - t0) / 1000;
                }
            }

            double difficult = 0;

            if (peak > 0)
            {
                for (int i = 0; i < nb; i++)
                {
                    if (dens[i] <= 0)
                        continue;

                    double d = Math.Min(1, env[i] / peak);
                    difficult += dens[i] * Math.Pow(d, envelope_power);
                }
            }

            double fill = 1 - Math.Exp(-difficult / envelope_chars);
            double floor = stars_at_human_peak / (1 + envelope_range) * peak;

            // THE TARGET WPM. A window's raw speed is wpm / capability(its seconds), so the figure
            // converts to a plain speed at any fixed duration in one multiplication by the
            // capability curve. It is read off the window chosen by ConsiderSpeedWindow above -
            // the hardest by speed alone, typability and rhythm left out of both the choice and
            // the translation - which can be a different window from the rating's peak. Display
            // only: nothing in the rating reads it, and the figure rises with a rate mod because
            // the same map asks for more WPM in the same playback time.
            double targetWpm = speedTargetRatio > 0
                ? speedTargetCells / 5 / (speedTargetSeconds / 60) * capability(TargetWindowSeconds) / capability(speedTargetSeconds)
                : 0;

            double complexityMultiplier = 1, complexityLoad = 0;

            if (RhythmicComplexity.Strength > 0 && peak > 0)
            {
                // Each keypress carries its OWN demand weight, fixed BEFORE the search: its demand
                // over the map's peak, to the envelope power, times the typability of its line. An
                // easy keypress therefore gives up its own timing cheaply, and no single hardest
                // word can price a whole passage.
                var presses = BuildPresses(words, literate, RhythmicComplexity.Tolerance * Arm(judgementArm).Scale, Arm(judgementArm).Shelter, word =>
                {
                    double lineMultiplier = applies && word.Scored ? TypabilityIndex.Multiplier(word.Z) : 1;
                    double demand = word.Cells / 5 / (word.Span / 60000) / (capability(word.Span / 1000) * lineMultiplier);
                    double weight = peak > 0 ? Math.Pow(Math.Min(1, demand / peak), envelope_power) : 0;

                    return Math.Max(RhythmicComplexity.WeightFloor, weight);
                });
                var report = RhythmicComplexity.Compute(presses, RhythmicComplexity.Strength, RhythmicComplexity.Horizon, RhythmicComplexity.RollingScale);
                complexityLoad = report.Load;
                complexityMultiplier = report.Multiplier;
            }

            double stars = Math.Max(0, floor * (1 + envelope_range * fill)) * complexityMultiplier;

            return new ModelResult
            {
                Stars = Math.Max(stars, 0),
                Peak = peak,
                PeakSeconds = peakSeconds,
                PeakCells = peakCells,
                PeakStartSeconds = peakStartSeconds,
                MinimumSeconds = double.IsFinite(minimumSeconds) ? minimumSeconds : null,
                DifficultCharacters = difficult,
                Fill = fill,
                Floor = floor,
                Cells = pre[nb],
                DurationSeconds = durationSeconds,
                TargetWpm = targetWpm,
                ComplexityMultiplier = complexityMultiplier,
                ComplexityLoad = complexityLoad,
                TypabilityScoredFraction = scoredFraction,
                TypabilityMeanZ = pre[nb] > 0 ? preZ[nb] / pre[nb] : 0,
                TypabilityApplied = applies,
            };
        }

        /// <summary>
        /// The WPM the fastest humans sustain for <paramref name="seconds"/>, i.e. the pace a window
        /// of that duration is scored against.
        /// </summary>
        private static double capability(double seconds)
            => capability_base_wpm + capability_burst_wpm * Math.Pow(capability_ref_seconds / seconds, capability_exponent);

        /// <summary>Public form of the capability curve, so the pace figures and tests can read it.</summary>
        public static double Capability(double seconds) => capability(seconds);


        /// <summary>
        /// A prefix table read at a fractional position, clamped to the table's own ends. Both arms
        /// read a window's mass through this one interpolation - the envelope's character-weighted
        /// typability and the chunked axis's cell and load arithmetic - so neither can disagree with
        /// the other about where a span's edges sit.
        /// </summary>
        internal static double At(double[] table, double value, int binCount)
        {
            double x = Math.Min(binCount, Math.Max(0, value));
            int lo = (int)Math.Floor(x);

            return lo >= binCount ? table[binCount] : table[lo] + (table[lo + 1] - table[lo]) * (x - lo);
        }

        /// <summary>
        /// The map's cells on the timeline: the binned density, the cell prefix, and the
        /// character-weighted typability prefixes. Built in one place so the envelope model and the
        /// chunked endurance axis cannot weight a word differently. A word's cells are spread evenly
        /// across its own span, the z of the line it sits on rides on the mass it contributes (0 when
        /// that line carried no score), and the scored mass is accumulated separately so a window can
        /// report how much of its own material the index actually scored.
        /// </summary>
        private static void BuildTimeline(
            IReadOnlyList<Word> words,
            double t0,
            int nb,
            double binMs,
            out double[] dens,
            out double[] pre,
            out double[] preZ,
            out double[] preScored)
        {
            dens = new double[nb];
            var densZ = new double[nb];
            var densScored = new double[nb];
            pre = new double[nb + 1];
            preZ = new double[nb + 1];
            preScored = new double[nb + 1];

            foreach (var w in words)
            {
                double a = (w.Start - t0) / binMs;
                double z = (w.End - t0) / binMs;
                double per = w.Cells / Math.Max(density_epsilon, z - a);

                for (int i = (int)Math.Floor(a); i <= Math.Min(nb - 1, (int)Math.Floor(z)); i++)
                {
                    double lo = Math.Max(a, i);
                    double hi = Math.Min(z, i + 1);

                    if (hi <= lo)
                        continue;

                    double mass = per * (hi - lo);
                    dens[i] += mass;
                    densZ[i] += mass * w.Z;

                    if (w.Scored)
                        densScored[i] += mass;
                }
            }

            for (int i = 0; i < nb; i++)
            {
                pre[i + 1] = pre[i] + dens[i];
                preZ[i + 1] = preZ[i] + densZ[i];
                preScored[i + 1] = preScored[i] + densScored[i];
            }
        }

        /// <summary>
        /// The typeable cells of one token: every character a fixed key has to produce, plus the
        /// freestyle slots, which are keypresses with no spelling to be slow at. Deliberately NOT
        /// <see cref="Word.Cells"/>: a trailing space is a cell on the timeline but belongs to no
        /// word's own subdivision, and a freestyle slot counts one whole cell here rather than the
        /// discounted cost it carries as typed work.
        /// </summary>
        internal static double TypeableCellCount(string token, bool literate)
        {
            double cells = 0;

            foreach (char c in token)
            {
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                    || c == ' ' || c == Typeability.FREESTYLE_MARKER)
                    cells++;
                else if (literate && Typeability.IsPunctuation(c))
                    cells++;
            }

            return cells;
        }

        /// <summary>
        /// The CHARACTER boundaries a map's timing actually has: every keypress's own point target on
        /// the engine's per-character ramp (syllable-aware, the same targets
        /// <see cref="PressIntervals"/> returns for the point arm), plus every word start and end.
        /// These are the edges the chunked axis is allowed to cut on, because a chunk is a run of
        /// typed cells and both of its ends belong on a character.
        /// </summary>
        internal static double[] CharacterBoundaries(IReadOnlyList<Word> words, bool literate)
        {
            var times = new List<double>();
            var seen = new HashSet<double>();
            var intervals = new List<Interval>();

            foreach (Word word in words)
            {
                if (seen.Add(word.Start))
                    times.Add(word.Start);

                if (seen.Add(word.End))
                    times.Add(word.End);

                intervals.Clear();
                PressIntervals(word, 0, 0, literate, intervals, Shelter.Point);

                foreach (Interval interval in intervals)
                {
                    if (double.IsFinite(interval.Lo) && seen.Add(interval.Lo))
                        times.Add(interval.Lo);
                }
            }

            times.Sort();
            return times.ToArray();
        }

        /// <summary>
        /// The typability scale the chunked axis puts on a window: the same curve as the shipped
        /// index, at the axis's own strength and CV rather than at the shipped model's.
        /// </summary>
        private static double ChunkedTypabilityMultiplier(double z, ChunkedEndurance.Settings settings)
        {
            if (!(settings.typability_strength > 0) || !(settings.typability_cv > 0)
                || double.IsNaN(z) || double.IsInfinity(z))
                return 1;

            double raw = 1 + settings.typability_strength * settings.typability_cv * z;

            return Math.Min(TypabilityIndex.MaxMultiplier, Math.Max(TypabilityIndex.MinMultiplier, raw));
        }

        /// <summary>The chunked axis's report, plus the map-level readings the panel shows beside it.</summary>
        internal readonly record struct ChunkedRating(
            ChunkedEndurance.Report Report,
            double Cells,
            double DurationSeconds,
            double TypabilityMeanZ,
            double TypabilityScoredFraction,
            bool TypabilityApplied);

        /// <summary>
        /// Rates a map on the chunked endurance axis. Read through <see cref="ComputeDetail"/> with
        /// <see cref="EnduranceAxis.Chunked"/>; the fixture pin calls it directly so it can hand the
        /// axis the same score snapshot the fixture was emitted with (see <see cref="NoScores"/>).
        ///
        /// <para>This arm builds its own word list and timeline rather than sharing the envelope
        /// path's, because it reads none of what that path builds them for: the peak scan, the
        /// envelope sweep and the schedule. The pieces both arms really do share - the word builder,
        /// the press stream, the capability curve, the density interpolation and the rolling scan -
        /// are the envelope model's own, so neither can drift from the other.</para>
        /// </summary>
        internal static ChunkedRating RateChunked(
            IReadOnlyList<LyricLine> lines,
            double rate,
            bool literate,
            LyricScoreSource scores,
            ChunkedEndurance.Settings settings,
            bool decideRuns = true)
        {
            if (rate <= 0)
                return new ChunkedRating(ChunkedEndurance.EmptyReport(settings), 0, 0, 0, 0, false);

            var words = BuildWords(lines, rate, literate, scores, settings.span_ms);

            if (words.Count == 0)
                return new ChunkedRating(ChunkedEndurance.EmptyReport(settings), 0, 0, 0, 0, false);

            words.Sort((a, b) => a.Start.CompareTo(b.Start));

            double t0 = words[0].Start;
            double t1 = double.NegativeInfinity;

            foreach (var w in words)
                t1 = Math.Max(t1, w.End);

            double durationSeconds = (t1 - t0) / 1000.0;

            if (durationSeconds > max_map_seconds)
                throw new InvalidOperationException("Maps must fit within 30 minutes at the selected rate.");

            int nb = Math.Max(1, (int)Math.Ceiling((t1 - t0) / settings.bin_ms) + 1);
            BuildTimeline(words, t0, nb, settings.bin_ms, out _, out double[] pre, out double[] preZ, out double[] preScored);

            // The axis's own typability arm: its own strength, CV and gate, which are the sandbox's
            // PARAMETERS for this axis rather than the shipped index's.
            double scoredFraction = pre[nb] > 0 ? preScored[nb] / pre[nb] : 0;
            bool applies = settings.typability_strength > 0 && scoredFraction >= settings.typability_min_scored_fraction;
            double meanZ = pre[nb] > 0 ? preZ[nb] / pre[nb] : 0;

            double LineMultiplier(Word word)
                => applies && word.Scored ? ChunkedTypabilityMultiplier(word.Z, settings) : 1;

            ChunkedEndurance.LoadField load = ChunkedEndurance.BuildLoadPrefix(words, t0, nb, literate, LineMultiplier, settings);

            // A window's own multiplier, and exactly 1 below the gate: the readings behind it are
            // still computed, so a window reports the material it holds even when the map as a whole
            // did not clear the gate.
            double Window(double aMs, double bMs)
            {
                double weight = At(pre, (bMs - t0) / settings.bin_ms, nb) - At(pre, (aMs - t0) / settings.bin_ms, nb);

                if (weight <= 0)
                    return 1;

                double z = (At(preZ, (bMs - t0) / settings.bin_ms, nb) - At(preZ, (aMs - t0) / settings.bin_ms, nb)) / weight;

                return applies ? ChunkedTypabilityMultiplier(z, settings) : 1;
            }

            var timeline = new ChunkedEndurance.Timeline(words, t0, nb, settings.bin_ms, pre,
                ChunkedEndurance.BuildShiftPrefix(words, t0, nb, literate, settings));

            // THE RUNS AND THE GRID ARE DECIDED BEFORE THE NON-RATE MODS, and ONLY when one is actually
            // in play. A run is a property of the map's material - which passages butt up against each
            // other and which are separated by a rest - so Literate and the judgement arms must not be
            // able to reshape it: deciding it on the played stream is what let Literate dissolve a run
            // (the merge's lift fell from +7.1% to +4.6% between the plain and Literate readings of one
            // map, which swallowed everything the extra material had added).
            //
            // A play with NEITHER of those takes the live path untouched, which is the whole point of
            // the gate: the fix must not move the figures of a map no mod has shaped. That is not a
            // coincidence of the two passes agreeing - the live path reads its grid off the strain,
            // which the base pass cannot reproduce - so a plain play must not be handed a base pass at
            // all (see ChunkedEndurance.Compute's anchor rule).
            //
            // The base pass is taken at the PLAYED RATE, because a rate genuinely repacks the grid (the
            // chunk length is a real duration, so Double Time buys fewer, denser chunks) - and with the
            // DEFAULT stream and the LIVE shelter. The map's own TYPABILITY is kept: that index is part
            // of the material rather than a mod, and it is what the live figures were computed with.
            IReadOnlyList<ChunkedEndurance.RunSpan>? baseRuns = null;

            bool modShapedPlay = literate || settings.complexity_mod != ChunkedEndurance.mod_none;

            if (decideRuns && modShapedPlay && settings.chunk_merge_mode is ChunkedEndurance.merge_runs or ChunkedEndurance.merge_runs_gated)
            {
                ChunkedEndurance.Settings baseSettings = settings with { complexity_mod = ChunkedEndurance.mod_none };
                ChunkedRating baseRating = RateChunked(lines, rate, literate: false, scores, baseSettings, decideRuns: false);

                baseRuns = baseRating.Report.RunSpans;
            }

            ChunkedEndurance.Report report = ChunkedEndurance.Compute(timeline, load, Window, settings, literate, baseRuns);

            return new ChunkedRating(report, pre[nb], durationSeconds, meanZ, scoredFraction, applies);
        }

        /// <summary>
        /// The chunked endurance axis's <see cref="ModelResult"/>. It fills in the map-level readings
        /// the panel and the pp figure share, and NOTHING the peak scan produces: on this axis the
        /// peak, the floor and Target WPM are not read at all, so the work that builds them is not
        /// paid (the sandbox's own pool path skips it for the same reason).
        /// </summary>
        private static ModelResult ComputeChunked(
            IReadOnlyList<LyricLine> lines,
            double rate,
            bool literate,
            LyricScoreSource scores,
            ChunkedEndurance.Settings settings)
        {
            ChunkedRating rating = RateChunked(lines, rate, literate, scores, settings);

            return new ModelResult
            {
                Stars = Math.Max(rating.Report.Stars, 0),
                Peak = 0,
                PeakSeconds = 0,
                PeakCells = 0,
                PeakStartSeconds = 0,
                MinimumSeconds = null,
                DifficultCharacters = rating.Report.LengthCharacters,
                Fill = 1 - Math.Exp(-rating.Report.LengthCharacters / settings.chars),
                Floor = 0,
                Cells = rating.Cells,
                DurationSeconds = rating.DurationSeconds,
                TargetWpm = 0,
                ComplexityMultiplier = rating.Report.RhythmMultiplier,
                ComplexityLoad = rating.Report.Load,
                TypabilityScoredFraction = rating.TypabilityScoredFraction,
                TypabilityMeanZ = rating.TypabilityMeanZ,
                TypabilityApplied = rating.TypabilityApplied,
            };
        }

        /// <summary>
        /// The map's typeable units, in playback milliseconds: one entry per word, with the weighted
        /// cell mass it carries. A word followed by another word on the same line also carries one
        /// space cell. The density index, the peak refinement and the complexity scan all read this
        /// same structure, so none of them can weight a word differently from the others.
        /// </summary>
        internal static List<Word> BuildWords(IReadOnlyList<LyricLine> lines, double rate, bool literate)
            => BuildWords(lines, rate, literate, ShippedScores, min_span_ms);

        /// <summary>
        /// As above, with the two things a non-envelope arm moves: the per-line score source (the
        /// chunked axis's fixture was emitted with no scores at all) and the floor on a word's sung
        /// span, which is a dial of the arm that reads it rather than a property of the map.
        /// </summary>
        internal static List<Word> BuildWords(IReadOnlyList<LyricLine> lines, double rate, bool literate, LyricScoreSource scores, double spanFloorMs)
        {
            var words = new List<Word>();

            for (int li = 0; li < lines.Count; li++)
            {
                LyricLine line = lines[li];
                string[] tokens = line.RawText.Split(' ');

                for (int j = 0; j < tokens.Length; j++)
                {
                    string token = tokens[j];
                    int chars = 0, freestyle = 0;

                    foreach (char c in token)
                    {
                        if (Typeability.IsTypeable(c) && c != ' ')
                            chars++;
                        else if (literate && Typeability.IsPunctuation(c))
                            chars++;
                        else if (Typeability.IsFreestyle(c))
                            freestyle++;
                    }

                    if (chars == 0 && freestyle == 0)
                        continue;

                    double unitStart, unitEnd;

                    if (j < line.Units.Count)
                    {
                        unitStart = line.Units[j].StartTime;
                        unitEnd = line.Units[j].EndTime;
                    }
                    else
                    {
                        double span = Math.Max(line.EndTime - line.StartTime, spanFloorMs) / tokens.Length;
                        unitStart = line.StartTime + span * j;
                        unitEnd = unitStart + span;
                    }

                    // Both figures leave this loop in REAL time: the span floor is applied in
                    // beatmap time (it is a floor on the AUTHORING, not on the player's clock) and
                    // the divide happens after it, exactly as the onset's does.
                    double[]? groups = null;
                    double[]? groupEnds = null;

                    // A PAUSE IS A DIVIDER TOO. A word the mapper cut with rests is sung in as many
                    // separate stretches, and live play judges each character inside its STRETCH's own
                    // span exactly as it does inside a syllable's - so the rating has to read those
                    // spans, not one long word with the rests folded in as free time. Derived through
                    // PausedWord itself, the one derivation the engine's targets and judgement groups
                    // read, so the two can never disagree about where a stretch begins or ends.
                    if (j < line.Units.Count && PausedWord.Of(token, unitStart, unitEnd, line.Units[j]) is PausedWord.Cut paused)
                    {
                        groups = new double[paused.Pieces.Count];
                        groupEnds = new double[paused.Pieces.Count];

                        for (int g = 0; g < paused.Pieces.Count; g++)
                        {
                            groups[g] = paused.Pieces[g].StartTime / rate;
                            groupEnds[g] = paused.Pieces[g].EndTime / rate;
                        }
                    }
                    else if (j < line.Units.Count && line.Units[j].SyllableBoundaries.Count > 0)
                    {
                        IReadOnlyList<double> boundaries = line.Units[j].SyllableBoundaries;
                        groups = new double[boundaries.Count + 1];
                        groups[0] = unitStart / rate;

                        for (int g = 0; g < boundaries.Count; g++)
                            groups[g + 1] = boundaries[g] / rate;
                    }

                    // Typability is measured per LINE and carried by every word on it. It is read
                    // on the text the line is TYPED as: the authored text under Literate, the
                    // default stripped lower-case stream otherwise, the same switch the cells above
                    // follow. The index scores capitals and marks as keystrokes of their own, so
                    // scoring the authored text while charging the cells of the stripped one prices
                    // a line nobody types.
                    //
                    // The freestyle MARKERS come out of it, exactly as the lab's own playedLineText
                    // drops them: an any-key slot has no spelling to be slow at, so leaving the
                    // ampersands in makes the index score glyphs the player never types - and the
                    // lab has never seen them, so the two sides would disagree about the same line.
                    string typedText = (literate ? line.RawText : Typeability.ToDefaultStream(line.RawText)).Replace("&", string.Empty);
                    bool scored = scores(typedText, out double z);

                    words.Add(new Word(
                        unitStart / rate,
                        Math.Max(unitEnd - unitStart, spanFloorMs) / rate,
                        chars + freestyle_cost_weight * freestyle,
                        li,
                        token,
                        groups,
                        z,
                        scored,
                        groupEnds));
                }
            }

            // THE TRAILING SPACE, decided before the timeline sort because "the next word" means the
            // next word of the lyric, not the next word in time. A word ending its line is followed
            // by a line break rather than a spacebar press, so it gets none.
            for (int i = 0; i < words.Count; i++)
            {
                if (i + 1 < words.Count && words[i + 1].LineIndex == words[i].LineIndex)
                {
                    Word w = words[i];
                    words[i] = new Word(w.Start, w.Span, w.Cells + 1, w.LineIndex, w.Token, w.Groups, w.Z, w.Scored, w.GroupEnds);
                }
            }

            return words;
        }

        /// <summary>
        /// The scan's press stream: every keypress of every word, with its Great interval, its own
        /// target time, the idle time accumulated before it inside its line, and the demand weight
        /// that prices it.
        ///
        /// <para>A pause BETWEEN two lyric lines is deleted rather than priced: the next line cues
        /// its own first word, so the player is told exactly when to resume and the silence is
        /// neither a rest the pace recovers inside nor a rhythm it has to cover. `pause` is the
        /// running total of those deleted gaps and every later press is shifted back by it, which
        /// leaves the scan reading the two lines as the continuous typed stream they are cued to
        /// be. A pause INSIDE a line is sung time and stays in as idle recovery.</para>
        ///
        /// <para>The Great window the intervals are read in and the weight each keypress carries are
        /// the caller's, because the rhythmic-complexity bonus and the chunked endurance axis's load
        /// prefix are two different readings of the same stream: the bonus prices a press against the
        /// map's own peak, the load prefix against a fixed reference demand. Everything else - the
        /// deleted line pauses, the point targets, the idle accumulation - is shared, so no two
        /// callers can disagree about what a press IS.</para>
        /// </summary>
        internal static List<RhythmicComplexity.Press> BuildPresses(IReadOnlyList<Word> words, bool literate, double toleranceMs, Shelter shelter, Func<Word, double> weightOf)
        {
            var presses = new List<RhythmicComplexity.Press>(words.Count * 4);
            double idle = 0, pause = 0, previousEnd = double.NegativeInfinity;
            bool hasPrevious = false;

            for (int k = 0; k < words.Count; k++)
            {
                Word word = words[k];

                if (hasPrevious)
                {
                    double gap = Math.Max(0, word.Start - previousEnd);

                    if (words[k - 1].LineIndex == word.LineIndex)
                        idle += gap;
                    else
                        pause += gap;
                }

                previousEnd = Math.Max(previousEnd, word.End);
                hasPrevious = true;

                Word timed = pause != 0
                    ? new Word(word.Start - pause, word.Span, word.Cells, word.LineIndex, word.Token,
                        word.Groups?.Select(g => g - pause).ToArray(), word.Z, word.Scored,
                        word.GroupEnds?.Select(g => g - pause).ToArray())
                    : word;

                // Each keypress carries its OWN weight, fixed BEFORE the search: an easy keypress
                // gives up its own timing cheaply, and no single hardest word can price a whole
                // passage.
                double weight = weightOf(word);

                var intervals = new List<Interval>();
                PressIntervals(timed, toleranceMs, toleranceMs, literate, intervals, shelter);

                var targets = new List<Interval>();
                PressIntervals(timed, 0, 0, literate, targets, Shelter.Point);

                for (int j = 0; j < intervals.Count; j++)
                    presses.Add(new RhythmicComplexity.Press(intervals[j].Lo, intervals[j].Hi, targets[j].Lo, idle, weight, k));
            }

            return presses;
        }

        /// <summary>
        /// One judgement interval per keypress of a word, where the press may land and still be
        /// judged Great.
        ///
        /// <para>Live play judges each character inside its SYLLABLE's sung span
        /// (<c>TypingEngine.SyllableTiming</c>), except that a syllable's FIRST character is
        /// anchored to the syllable's start so the group's opening sits on the clock, and except for
        /// STRETCH cells (a freestyle slot, or a run of three or more identical characters inside
        /// one group), which keep their own point target. A word the mapper did not subdivide is
        /// syllabified by the engine, so it is syllabified here too, and a word the syllabifier
        /// REFUSES outright keeps point targets throughout.</para>
        ///
        /// <para>The syllables are cut by INDEX, not by the syllables' text lengths, because that is
        /// how the engine spreads a word's cells (see <c>TypingLine.syllableCharTarget</c>), and a
        /// space is untimed, so it appears in no interval.</para>
        /// </summary>
        private static void PressIntervals(Word word, double early, double late, bool literate, List<Interval> into, Shelter shelter)
        {
            var typed = new List<char>(word.Token.Length);

            foreach (char c in word.Token)
            {
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                    typed.Add(c);
                else if (literate && Typeability.IsPunctuation(c))
                    typed.Add(c);
                else if (Typeability.IsFreestyle(c))
                    typed.Add(c);
            }

            int count = typed.Count;

            if (count == 0)
                return;

            double start = word.Start, end = word.End;
            bool authored = word.Groups != null && word.Groups.Length >= 2;
            int segments = authored ? word.Groups!.Length : NaturalSyllables(word.Token);
            int[] groupOf = new int[count];

            for (int j = 0; j < count; j++)
                groupOf[j] = segments > 0 ? Math.Min(segments - 1, j * segments / count) : 0;

            double edge(int g) => start + (end - start) * g / Math.Max(1, segments);
            double spanLo(int g) => authored ? (g == 0 ? start : word.Groups![g]) : edge(g);

            // A STRETCH word says where each group ends itself: its pieces are separated by rests that
            // belong to no group, so "the next group's start" is not this group's end. Every other
            // word's groups tile its span, and the last of them ends at the word's own end.
            double spanHi(int g) => word.GroupEnds != null
                ? word.GroupEnds[g]
                : authored ? (g == segments - 1 ? end : word.Groups![g + 1]) : edge(g + 1);

            bool[] firstOf = new bool[count];

            for (int j = count - 1; j >= 0; j--)
                firstOf[j] = j == 0 || groupOf[j] != groupOf[j - 1];

            bool[] stretch = new bool[count];

            for (int at = 0; at < count;)
            {
                int groupEnd = at;

                while (groupEnd < count && groupOf[groupEnd] == groupOf[at])
                    groupEnd++;

                for (int run = at; run < groupEnd;)
                {
                    int runEnd = run + 1;

                    while (runEnd < groupEnd && typed[runEnd] == typed[run])
                        runEnd++;

                    if (typed[run] == Typeability.FREESTYLE_MARKER || runEnd - run >= 3)
                    {
                        for (int m = run; m < runEnd; m++)
                            stretch[m] = true;
                    }

                    run = runEnd;
                }

                at = groupEnd;
            }

            double pointTarget(int j)
            {
                if (segments == 0)
                    return start + j * (end - start) / count;

                int group = groupOf[j];
                double lo = spanLo(group), hi = spanHi(group);
                double indexLo = (double)group * count / segments;
                double indexHi = (double)(group + 1) * count / segments;

                return indexHi <= indexLo ? lo : lo + (j - indexLo) / (indexHi - indexLo) * (hi - lo);
            }

            if (segments == 0 || shelter == Shelter.Point)
            {
                for (int j = 0; j < count; j++)
                {
                    double target = pointTarget(j);
                    into.Add(new Interval(target - early, target + late));
                }

                return;
            }

            // EASY SHELTERS THE WHOLE WORD: every cell but the first may land anywhere from the
            // word's start to its end, and only the first still opens on the clock.
            if (shelter == Shelter.Word)
            {
                for (int j = 0; j < count; j++)
                {
                    into.Add(j == 0 ? new Interval(start - early, start + late) : new Interval(start - early, end + late));
                }

                return;
            }

            for (int j = 0; j < count; j++)
            {
                // A stretch cell is point-judged under every arm: the mod buttons do not move it.
                if (stretch[j])
                {
                    double target = pointTarget(j);
                    into.Add(new Interval(target - early, target + late));
                    continue;
                }

                double lo = spanLo(groupOf[j]), hi = spanHi(groupOf[j]);
                into.Add(firstOf[j] ? new Interval(lo - early, lo + late) : new Interval(lo - early, hi + late));
            }
        }

        /// <summary>
        /// How many syllables the engine finds in a word the mapper did not subdivide: vowel runs,
        /// with a silent final 'e' folded back in ("flame" is one syllable, "water" two), and 0 for
        /// a token the syllabifier REFUSES (three identical characters in a row), which is the one
        /// case that stays point-timed.
        /// </summary>
        internal static int NaturalSyllables(string token)
        {
            string lower = token.ToLowerInvariant();

            // A STYLISED SPELLING ("woooords", "ohhh", and a token carrying an ellipsis, whose
            // three dots are three identical characters) is refused outright, so the engine judges
            // every cell of it on its own point target. Tested on the WHOLE token, marks included,
            // because that is what the syllabifier refuses on.
            for (int i = 2; i < lower.Length; i++)
            {
                if (lower[i] == lower[i - 1] && lower[i] == lower[i - 2])
                    return 0;
            }

            var letters = new List<char>();

            for (int i = 0; i < lower.Length; i++)
            {
                char c = lower[i];

                if (c < 'a' || c > 'z')
                    continue;

                letters.Add(c);
            }

            if (letters.Count == 0)
                return 0;

            int runs = 0;
            bool vowel = false;

            foreach (char c in letters)
            {
                bool isVowel = "aeiouy".IndexOf(c) >= 0;

                if (isVowel && !vowel)
                    runs++;

                vowel = isVowel;
            }

            if (letters.Count > 2 && letters[^1] == 'e' && runs > 1)
                runs--;

            return Math.Max(1, Math.Min(letters.Count, runs));
        }

        /// <summary>
        /// The map's cells as an exact piecewise-uniform density: each word spreads its own cells
        /// evenly across its own span, so the mass between two arbitrary times is exact with no bin
        /// rounding and a window edge can sit anywhere, down to the millisecond.
        ///
        /// <para><see cref="Times"/> holds every word start and end, which are the only times the
        /// density can change, and therefore the candidate set the exact peak lives on.</para>
        /// </summary>
        private sealed class DensityIndex
        {
            public readonly double[] Times;
            private readonly double[] density;
            private readonly double[] mass;
            public readonly double Total;

            private DensityIndex(double[] times, double[] density, double[] mass, double total)
            {
                Times = times;
                this.density = density;
                this.mass = mass;
                Total = total;
            }

            public static DensityIndex Build(IReadOnlyList<Word> words)
            {
                var events = new SortedDictionary<double, double>();

                foreach (var w in words)
                {
                    if (!(w.Span > 0))
                        continue;

                    double d = w.Cells / w.Span;
                    events[w.Start] = events.TryGetValue(w.Start, out double atStart) ? atStart + d : d;
                    events[w.End] = events.TryGetValue(w.End, out double atEnd) ? atEnd - d : -d;
                }

                double[] times = events.Keys.ToArray();
                var density = new double[Math.Max(0, times.Length - 1)];
                var mass = new double[times.Length];
                double current = 0;

                for (int i = 0; i < times.Length - 1; i++)
                {
                    current += events[times[i]];
                    density[i] = Math.Max(0, current);
                    mass[i + 1] = mass[i] + density[i] * (times[i + 1] - times[i]);
                }

                return new DensityIndex(times, density, mass, mass.Length > 0 ? mass[^1] : 0);
            }

            public double MassAt(double x)
            {
                if (Times.Length == 0)
                    return 0;

                if (x <= Times[0])
                    return 0;

                if (x >= Times[^1])
                    return Total;

                int lo = 0, hi = Times.Length - 1;

                while (lo < hi)
                {
                    int mid = (lo + hi + 1) >> 1;

                    if (Times[mid] <= x)
                        lo = mid;
                    else
                        hi = mid - 1;
                }

                return mass[lo] + density[lo] * (x - Times[lo]);
            }

            public double CellsBetween(double a, double b) => b > a ? MassAt(b) - MassAt(a) : 0;
        }

        /// <summary>
        /// The exact shortest interval in the piecewise-uniform word density that holds
        /// <paramref name="target"/> weighted characters, with no bin rounding. A minimum occurs
        /// with at least one endpoint on a density boundary, so the scan walks the prefix mass table
        /// from each boundary forward and backward.
        /// </summary>
        private static (double Start, double Seconds, double Cells)? ShortestCharacterWindow(IReadOnlyList<Word> words, double target)
        {
            if (!(target > 0) || words.Count == 0)
                return null;

            DensityIndex index = DensityIndex.Build(words);
            double[] times = index.Times;

            if (times.Length < 2)
                return null;

            const double tolerance = 1e-9;
            double total = index.Total;

            if (target > total + tolerance)
                return null;

            target = Math.Min(target, total);

            double[] mass = new double[times.Length];

            for (int i = 0; i < times.Length; i++)
                mass[i] = index.MassAt(times[i]);

            double inverse(double value, bool latest)
            {
                int lo = 0, hi = mass.Length;

                while (lo < hi)
                {
                    int mid = (lo + hi) >> 1;

                    if (latest ? mass[mid] <= value : mass[mid] < value)
                        lo = mid + 1;
                    else
                        hi = mid;
                }

                int indexAt = latest ? lo - 1 : lo;

                if (indexAt >= 0 && indexAt < mass.Length && mass[indexAt] == value)
                    return times[indexAt];

                int a = Math.Max(0, lo - 1), b = Math.Min(times.Length - 1, lo);

                if (b <= a)
                    return times[a];

                return times[a] + (value - mass[a]) / (mass[b] - mass[a]) * (times[b] - times[a]);
            }

            (double Start, double Seconds, double Cells)? best = null;

            void consider(double a, double b)
            {
                double from = inverse(a, true), to = inverse(b, false);
                double seconds = (to - from) / 1000;

                if (seconds > 0 && (best == null || seconds < best.Value.Seconds))
                    best = (from, seconds, target);
            }

            foreach (double c in mass)
            {
                if (c + target <= total + tolerance)
                    consider(c, Math.Min(total, c + target));

                if (c - target >= -tolerance)
                    consider(Math.Max(0, c - target), c);
            }

            return best;
        }

        private readonly record struct PeakCandidate(double Ratio, double Seconds, double Cells, double StartMs, double Multiplier, bool Found = true);

        /// <summary>
        /// The exact peak-window refinement, on the CHARACTER grid.
        ///
        /// <para>Two facts bound the search, which is why this is a boundary sweep rather than a
        /// hill climb. For a FIXED DURATION the mass in <c>[a, a+t]</c> is piecewise-linear in the
        /// start <c>a</c>, with slope equal to the density entering at the left edge minus the
        /// density leaving at the right, so its maximum sits where that slope changes sign: at a
        /// word start, or with the window's END on a word end. For a FIXED START the mass is linear
        /// in <c>t</c> between word boundaries while the denominator
        /// <c>5*(t/60)*capability(t)</c> is concave, so the ratio on such a piece is maximised at
        /// one of the piece's endpoints, which are word boundaries again. An expand-and-contract
        /// walk stalls the moment both edges would have to move together; enumerating the two
        /// families does not.</para>
        ///
        /// <para>Family 1 is every duration the schedule scans, with the window's start at a word
        /// start or its end at a word end. Family 2 is every pair of word boundaries at any duration
        /// at or above the floor, which covers moving both edges onto characters and letting the
        /// duration come out wherever it lands.</para>
        /// </summary>
        private static PeakCandidate RefinePeakWindow(
            DensityIndex index,
            Func<double, double, double, double, PeakCandidate> measure,
            double minimumSeconds,
            double minimumChars,
            IReadOnlyList<double> durations)
        {
            double[] times = index.Times;

            if (times.Length < 2)
                return default;

            double first = times[0], last = times[^1];
            PeakCandidate best = default;
            bool hasBest = false;

            void consider(double a, double b, double? cells = null)
            {
                if (!(b > a) || a < first - 1e-9 || b > last + 1e-9)
                    return;

                double seconds = (b - a) / 1000;

                if (seconds + 1e-12 < minimumSeconds)
                    return;

                double count = cells ?? index.CellsBetween(a, b);

                if (count + 1e-9 < minimumChars)
                    return;

                PeakCandidate measured = measure(a, b, seconds, count);

                if (!(measured.Ratio > 0) || double.IsNaN(measured.Ratio) || double.IsInfinity(measured.Ratio))
                    return;

                if (!hasBest || measured.Ratio > best.Ratio)
                {
                    best = measured;
                    hasBest = true;
                }
            }

            foreach (double seconds in durations.Concat(new[] { minimumSeconds }).Where(t => t > 0))
            {
                double span = seconds * 1000;

                foreach (double a in times)
                {
                    if (a + span > last + 1e-9)
                        break;

                    consider(a, a + span);
                }

                for (int j = times.Length - 1; j >= 0; j--)
                {
                    double b = times[j];

                    if (b - span < first - 1e-9)
                        break;

                    consider(b - span, b);
                }
            }

            double[] mass = new double[times.Length];

            for (int i = 0; i < times.Length; i++)
                mass[i] = index.MassAt(times[i]);

            for (int i = 0; i < times.Length; i++)
            {
                double a = times[i], mi = mass[i];

                for (int j = i + 1; j < times.Length; j++)
                {
                    double b = times[j];

                    if ((b - a) / 1000 + 1e-12 < minimumSeconds)
                        continue;

                    consider(a, b, mass[j] - mi);
                }
            }

            return hasBest ? best : default;
        }
    }
}
