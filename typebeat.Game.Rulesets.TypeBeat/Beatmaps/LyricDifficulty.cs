// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
    /// the axis's <c>span_ms</c>), and the block's cells are spread uniformly across that span into
    /// the axis's <c>bin_ms</c> bins. A word's cells are its fixed-key characters plus
    /// <c>freestyle_cost_weight</c> per freestyle slot, plus ONE TRAILING SPACE when the next word
    /// belongs to the same line: the spacebar is a real keypress and the pace figures have always
    /// counted it, so the rating counts it too.</para>
    ///
    /// <para>THE CAPABILITY CURVE. <c>S(t) = capability_base_wpm + capability_burst_wpm *
    /// (capability_ref_seconds / t) ^ capability_exponent</c> is the WPM the fastest humans sustain
    /// for t seconds (<see cref="ChunkedEndurance.Capability"/>, at the axis's own dials). A window
    /// of t seconds holding <c>c</c> cells therefore has ratio <c>(c / 5) / (t / 60) / S(t)</c>, its
    /// pace as a fraction of the record pace FOR ITS OWN DURATION, which is what makes a 1.5 second
    /// burst and a 60 second verse comparable numbers.</para>
    ///
    /// <para>THE RATING IS THE CHUNKED ENDURANCE AXIS (<see cref="ChunkedEndurance"/>): the map's own
    /// windows are scored against that curve, each with its typability, its judgement-window premium
    /// and its local rhythm boost, and the stars are their decay-weighted mean with the hardest
    /// weighted most, lifted by a length bonus. This class builds what the axis reads (the words, the
    /// cell timeline, the press stream and the character boundaries) and reports what it found.</para>
    ///
    /// <para>THE TARGET WPM (<see cref="TargetWpm"/>) is read off the same timeline and the same window
    /// family by raw speed alone, with every difficulty adjustment left out. It is display only.</para>
    ///
    /// <para>Kept in step with the Star Rating Sandbox, which is where this model is tuned:
    /// <c>tools/star-rating-sandbox/model.mjs</c> is the same arithmetic with every constant lifted
    /// into a dial, and <see cref="ChunkedEndurance.Settings"/> mirrors its active settings.</para>
    ///
    /// <para>The sandbox's other reading, the ENVELOPE model (a floor from the hardest window and a
    /// range filled by the rest), used to live here beside the chunked axis. Nothing rated through
    /// it, and once Target WPM moved onto the chunked axis nothing read it either, so it is gone.</para>
    /// </summary>
    public static class LyricDifficulty
    {
        /// <summary>
        /// The Target WPM window must carry at least this many weighted characters (see
        /// <see cref="TargetWpm"/>), so a map whose whole timeline is one short burst reports no
        /// target rather than one read off a window the player never has to hold. The rating itself
        /// has no such cutoff: the chunked axis scores sparse windows directly.
        /// </summary>
        public const double MinimumWindowChars = 16;

        /// <summary>
        /// The duration every map's fastest window is re-expressed at for the Target WPM readout (see
        /// <see cref="TargetWpm"/>). Display only: nothing in the rating reads it.
        /// </summary>
        public const double TargetWindowSeconds = 30;

        /// <summary>
        /// What one FREESTYLE slot is worth, as a fraction of an ordinary cell: a real keypress with
        /// a real deadline, which merely has no letter to find.
        /// </summary>
        private const double freestyle_cost_weight = 0.25;

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
        /// number: the difficult-character count the miss penalty is measured against, the Target
        /// WPM readout, and what each adjustment did.
        /// </summary>
        public readonly struct ModelResult
        {
            /// <summary>Final star rating: the chunked axis's figure, its rhythm boost and length bonus included.</summary>
            public required double Stars { get; init; }

            /// <summary>
            /// THE DIFFICULT CHARACTERS: the chunked axis's LengthCharacters, every weighted
            /// character credited by how close its window's strain sits to the map's own score.
            /// </summary>
            public required double DifficultCharacters { get; init; }

            /// <summary>How much of the axis's <c>chars</c> dial those characters fill, in [0, 1). A readout only.</summary>
            public required double Fill { get; init; }

            /// <summary>Total weighted cells on the timeline.</summary>
            public required double Cells { get; init; }

            /// <summary>Map length in seconds at the selected rate.</summary>
            public required double DurationSeconds { get; init; }

            /// <summary>
            /// The map's fastest window re-expressed as the WPM an equally demanding
            /// <see cref="TargetWindowSeconds"/> window would need: <see cref="LyricDifficulty.TargetWpm"/>
            /// for the same lines, rate and stream, so neither the judgement arm nor any difficulty
            /// adjustment moves it.
            ///
            /// <para>Display only. The pace figure the game publishes floors it at the whole-map
            /// average (<see cref="LyricPaceStatistics.TargetWpm"/>).</para>
            /// </summary>
            public required double TargetWpm { get; init; }

            /// <summary>What the rhythm boost multiplied the rating by, against the same axis with every window's boost neutralised.</summary>
            public required double ComplexityMultiplier { get; init; }

            /// <summary>The map's whole rolling load, the same prefix every window boost is read from. A diagnostic.</summary>
            public required double ComplexityLoad { get; init; }

            /// <summary>Share of the map's weighted cells that carried a typability score.</summary>
            public required double TypabilityScoredFraction { get; init; }

            /// <summary>Character-weighted mean z of the map, 0 when typability is off.</summary>
            public required double TypabilityMeanZ { get; init; }

            /// <summary>Whether the map cleared the scored-fraction gate, so typability moved its rating.</summary>
            public required bool TypabilityApplied { get; init; }
        }

        /// <summary>
        /// Which reading of a map the rating is taken from: the sandbox's own <c>enduranceAxis</c>
        /// setting. One reading remains, the CHUNKED one (see <see cref="Live"/>), since the
        /// envelope model it replaced was retired; the switch stays so every caller names the axis
        /// it rates on.
        /// </summary>
        public enum EnduranceAxis
        {
            /// <summary>The sandbox's chunked endurance axis, read off the map's own windows (see <see cref="ChunkedEndurance"/>).</summary>
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
        /// calculator, the pp formula and the pace figures all read one rating.
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
        /// index's own <see cref="TypabilityIndex.Strength"/> gates it here, and the axis applies
        /// its own scored-fraction gate on the map.
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
        /// The full rating: the stars plus the difficult-character count, the Target WPM readout and
        /// the adjustment readings the pp formula and the panels read.
        ///
        /// <para><paramref name="enduranceAxis"/> names the reading, and the chunked endurance axis
        /// is the only one there is (see <see cref="EnduranceAxis"/>).</para>
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

        /// <summary>
        /// THE TARGET WPM: the map's fastest window BY RAW SPEED, re-expressed as the WPM an equally
        /// demanding <see cref="TargetWindowSeconds"/> window would need, under a clock
        /// <paramref name="rate"/> and on the cell stream <paramref name="literate"/> selects.
        ///
        /// <para>The windows are the chunked axis's own family (see
        /// <see cref="ChunkedEndurance.TargetWpm"/>) on the rating's own timeline, and a window's raw
        /// speed is its WPM over the axis's capability curve at its own length. Typability, the
        /// judgement-window premium, the rhythm boost and the Shift surcharge are left out of both the
        /// window choice and the conversion, so the figure is a speed and no difficulty dial moves it.
        /// A window holding fewer than <see cref="MinimumWindowChars"/> weighted characters never
        /// qualifies, so a map that is one short burst reports 0.</para>
        ///
        /// <para>Read AT THE RATE rather than scaled from rate 1: the timeline is binned in real
        /// milliseconds and the window lengths are real durations, so a faster clock can name a
        /// different window. Display only: nothing in the rating reads it.</para>
        /// </summary>
        public static double TargetWpm(IEnumerable<LyricLine> lines, double rate = 1, bool literate = false)
        {
            var lineList = lines as IReadOnlyList<LyricLine> ?? lines.ToList();
            ChunkedEndurance.Settings settings = ChunkedEndurance.Live;

            // No typability reaches the figure, so no line is scored for it.
            List<Word>? words = SortedWords(lineList, rate, literate, NoScores, settings.span_ms, out double t0, out double t1);

            if (words == null)
                return 0;

            int nb = Math.Max(1, (int)Math.Ceiling((t1 - t0) / settings.bin_ms) + 1);
            BuildTimeline(words, t0, nb, settings.bin_ms, out double[] pre, out _, out _);

            return ChunkedEndurance.TargetWpm(new ChunkedEndurance.Timeline(words, t0, nb, settings.bin_ms, pre), settings, TargetWindowSeconds, MinimumWindowChars);
        }

        /// <summary>
        /// A prefix table read at a fractional position, clamped to the table's own ends. Every mass
        /// the chunked axis reads (its cells, its typability and its load) and every window the
        /// Target WPM scan measures is read through this one interpolation, so none of them can
        /// disagree with another about where a span's edges sit.
        /// </summary>
        internal static double At(double[] table, double value, int binCount)
        {
            double x = Math.Min(binCount, Math.Max(0, value));
            int lo = (int)Math.Floor(x);

            return lo >= binCount ? table[binCount] : table[lo] + (table[lo + 1] - table[lo]) * (x - lo);
        }

        /// <summary>
        /// The map's cells on the timeline: the cell prefix and the character-weighted typability
        /// prefixes. Built in one place so the rating and the Target WPM scan cannot weight a word
        /// differently. A word's cells are spread evenly across its own span, the z of the line it
        /// sits on rides on the mass it contributes (0 when that line carried no score), and the
        /// scored mass is accumulated separately so a window can report how much of its own material
        /// the index actually scored.
        /// </summary>
        private static void BuildTimeline(
            IReadOnlyList<Word> words,
            double t0,
            int nb,
            double binMs,
            out double[] pre,
            out double[] preZ,
            out double[] preScored)
        {
            var dens = new double[nb];
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
            bool TypabilityApplied,
            double TargetWpm);

        /// <summary>
        /// The map's words in timeline order, plus the first onset and the last sung end, or null
        /// when there is nothing to rate (no typeable word, or a rate that is not a clock). Shared by
        /// the rating and the Target WPM scan so the two lay out one timeline.
        /// </summary>
        private static List<Word>? SortedWords(IReadOnlyList<LyricLine> lines, double rate, bool literate, LyricScoreSource scores, double spanFloorMs, out double t0, out double t1)
        {
            t0 = 0;
            t1 = 0;

            if (rate <= 0)
                return null;

            var words = BuildWords(lines, rate, literate, scores, spanFloorMs);

            if (words.Count == 0)
                return null;

            words.Sort((a, b) => a.Start.CompareTo(b.Start));

            t0 = words[0].Start;
            t1 = double.NegativeInfinity;

            foreach (var w in words)
                t1 = Math.Max(t1, w.End);

            if ((t1 - t0) / 1000.0 > max_map_seconds)
                throw new InvalidOperationException("Maps must fit within 30 minutes at the selected rate.");

            return words;
        }

        /// <summary>
        /// Rates a map on the chunked endurance axis. Read through <c>ComputeDetail</c>; the
        /// fixture pin calls it directly so it can hand the axis the same score snapshot the fixture
        /// was emitted with (see <see cref="NoScores"/>).
        ///
        /// <para>The pieces the axis shares with the rest of this class (the word builder, the press
        /// stream, the density interpolation and the rolling scan) live here, so the rating, the
        /// Target WPM scan and the panels cannot drift from one another.</para>
        /// </summary>
        internal static ChunkedRating RateChunked(
            IReadOnlyList<LyricLine> lines,
            double rate,
            bool literate,
            LyricScoreSource scores,
            ChunkedEndurance.Settings settings,
            bool decideRuns = true)
        {
            List<Word>? words = SortedWords(lines, rate, literate, scores, settings.span_ms, out double t0, out double t1);

            if (words == null)
                return new ChunkedRating(ChunkedEndurance.EmptyReport(settings), 0, 0, 0, 0, false, 0);

            double durationSeconds = (t1 - t0) / 1000.0;

            int nb = Math.Max(1, (int)Math.Ceiling((t1 - t0) / settings.bin_ms) + 1);
            BuildTimeline(words, t0, nb, settings.bin_ms, out double[] pre, out double[] preZ, out double[] preScored);

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

            // Read off the very timeline the rating lays out, so the readout and the rating cannot
            // disagree about where a word's cells sit (see TargetWpm).
            double targetWpm = ChunkedEndurance.TargetWpm(timeline, settings, TargetWindowSeconds, MinimumWindowChars);

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

            return new ChunkedRating(report, pre[nb], durationSeconds, meanZ, scoredFraction, applies, targetWpm);
        }

        /// <summary>
        /// The chunked endurance axis's <see cref="ModelResult"/>: the stars, the difficult
        /// characters pp prices a miss against, the Target WPM readout and the map-level readings
        /// the panel and the pp figure share.
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
                DifficultCharacters = rating.Report.LengthCharacters,
                Fill = 1 - Math.Exp(-rating.Report.LengthCharacters / settings.chars),
                Cells = rating.Cells,
                DurationSeconds = rating.DurationSeconds,
                TargetWpm = rating.TargetWpm,
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
        /// space cell. The timeline, the press stream and the complexity scan all read this same
        /// structure, so none of them can weight a word differently from the others. Built at the
        /// chunked axis's own span floor.
        /// </summary>
        internal static List<Word> BuildWords(IReadOnlyList<LyricLine> lines, double rate, bool literate)
            => BuildWords(lines, rate, literate, ShippedScores, ChunkedEndurance.Live.span_ms);

        /// <summary>
        /// As above, with the two things a caller moves: the per-line score source (the chunked
        /// axis's fixture was emitted with no scores at all) and the floor on a word's sung span,
        /// which is a dial of the axis that reads it rather than a property of the map.
        /// </summary>
        internal static List<Word> BuildWords(IReadOnlyList<LyricLine> lines, double rate, bool literate, LyricScoreSource scores, double spanFloorMs)
        {
            var words = new List<Word>();

            for (int li = 0; li < lines.Count; li++)
            {
                LyricLine line = lines[li];
                string[] tokens = line.RawText.Split(' ');
                var wordIndices = new int[tokens.Length];
                Array.Fill(wordIndices, -1);

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

                    wordIndices[j] = words.Count;
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

                if (!literate)
                    addDefaultSpaces(line.RawText, wordIndices, words);
            }

            // Default spaces (including hyphens) were counted through the whole-line projection.
            // Literate keeps authored punctuation, so its gaps still follow the authored words.
            // THE TRAILING SPACE, decided before the timeline sort because "the next word" means the
            // next word of the lyric, not the next word in time. A word ending its line is followed
            // by a line break rather than a spacebar press, so it gets none.
            for (int i = 0; i < words.Count; i++)
            {
                if (literate && i + 1 < words.Count && words[i + 1].LineIndex == words[i].LineIndex)
                {
                    Word w = words[i];
                    words[i] = new Word(w.Start, w.Span, w.Cells + 1, w.LineIndex, w.Token, w.Groups, w.Z, w.Scored, w.GroupEnds);
                }
            }

            return words;
        }

        // Charge each surviving space to its authored unit without splitting or retiming it.
        // Project the entire line so adjacent hyphens/spaces collapse exactly as they do in play.
        private static void addDefaultSpaces(string raw, int[] wordIndices, List<Word> words)
        {
            int firstWord = Array.FindIndex(wordIndices, w => w >= 0);

            if (firstWord < 0)
                return;

            var text = new StringBuilder(raw.Length);
            var sources = new List<int>(raw.Length);
            Typeability.ProjectDefault(raw, text, sources);
            int rawPosition = 0, tokenIndex = 0;

            for (int i = 0; i < sources.Count; i++)
            {
                while (rawPosition < sources[i])
                {
                    if (raw[rawPosition++] == ' ')
                        tokenIndex++;
                }

                if (text[i] != ' ')
                    continue;

                // A punctuation-only token has no demand block. Its surviving gap belongs to the
                // previous played word, or the first played word for a leading authored space.
                int owner = tokenIndex;

                while (owner >= 0 && wordIndices[owner] < 0)
                    owner--;

                int index = wordIndices[owner >= 0 ? owner : firstWord];

                Word word = words[index];
                words[index] = new Word(word.Start, word.Span, word.Cells + 1, word.LineIndex,
                    word.Token, word.Groups, word.Z, word.Scored, word.GroupEnds);
            }
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
        /// the caller's: the chunked endurance axis's load prefix prices a press against a fixed
        /// reference demand, at the judgement arm's own window. Everything else - the deleted line
        /// pauses, the point targets, the idle accumulation - is built here, so no caller can
        /// disagree with another about what a press IS.</para>
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
        /// one group), which keep their own point target. A word the mapper did not subdivide is ONE
        /// group spanning its unit in the engine (backlog 363: only the mapper subdivides), so it is
        /// one segment here too, and a word the syllabifier REFUSES outright keeps point targets
        /// throughout.</para>
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
        /// How many syllables the engine plays a word the mapper did not subdivide as: ONE, the
        /// word's whole unit (backlog 363, the engine no longer syllabifies at gameplay), and 0 for a
        /// token the syllabifier REFUSES (three identical characters in a row), which is the one case
        /// that stays point-timed. Before 363 this counted vowel runs, a reduced stand-in for the
        /// engine's automatic split.
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

            // Only the mapper subdivides (backlog 363): a word with no authored boundary is ONE
            // judgement window spanning its unit.
            return 1;
        }
    }
}
