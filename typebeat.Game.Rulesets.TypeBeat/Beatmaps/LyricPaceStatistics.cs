// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// Typing-pace statistics for a lyric map, in two deliberate shapes.
    ///
    /// <para><see cref="AverageWpm"/> is the WHOLE-MAP rate with the song's breaks taken out:
    /// every counted line's cells over the total time the map is actually being sung,
    /// walked span by span. A line's <see cref="LyricLine.EndTime"/> is the NEXT line's start, so
    /// the boundary window swallows whatever pause follows a line; summing those windows would
    /// charge the player for a long instrumental, which is what the per-line mean below was
    /// introduced to avoid in the first place. Here a pause counts as singing time up to
    /// <see cref="break_min_ms"/> and is dropped whole beyond it, so a breath between words stays
    /// in the average and a real break does not. Because it is a sum over the map, a line is
    /// weighted by how long it is sung: that is the point of the change, and it makes a burst of
    /// short fast lines stop inflating the figure the way it does under an unweighted mean.</para>
    ///
    /// <para>WHAT IS A CELL here is narrower than the engine's: <see cref="Typeability.IsTypeable"/>
    /// characters and the inter-word spaces, but NOT freestyle slots. A freestyle slot takes any
    /// key, so no map can ask for a particular speed in one, and counting them would credit the
    /// map with typing it never demanded. The player's own readouts (<c>TypingEngine.LiveWpm</c>
    /// and the results screen's WPM) count every cell they actually pressed, freestyle slots
    /// included, so on a map that carries them the two figures part company: those answer "how
    /// fast did this player type", this one answers "how fast does this map ask to be typed".</para>
    ///
    /// <para>WHICH STREAM those cells come from is the caller's choice (<see cref="Compute"/>'s
    /// <c>literate</c> parameter). It defaults to the DEFAULT stream, the one every play without
    /// the Literate mod types and the one every stored figure was computed on, so nothing here
    /// moves for a caller that does not opt in. Under the mod the authored marks ARE typed cells,
    /// so they are counted and the rates rise: that is the whole point of the flag, and it is read
    /// by the surfaces that display a beatmap converted with the selected mods (song select's count
    /// statistics and the metadata wedge's pace readouts) while the server's stored per-map columns
    /// stay on the default stream.</para>
    ///
    /// <para><see cref="LineAverageWpm"/> is the figure that averaged before: each counted line's
    /// own cells over its BOUNDARY window, one vote per line however long or short it is. Both are
    /// kept because they answer different questions — "how fast is this map typed" against "how
    /// fast is the average line" — and the sandbox prints them side by side.</para>
    ///
    /// <para><see cref="TargetWpm"/> is neither: it is the map's hardest window BY RAW SPEED
    /// re-expressed at <see cref="LyricDifficulty.TargetWindowSeconds"/> (see the property). So it
    /// answers "what pace does this map ask to be typed at", against the whole-map rate's "what
    /// pace does the song ask per minute" and the line mean's "what pace does the average line
    /// ask". It is deliberately NOT the rating's peak: the peak is chosen against the typability
    /// multiplier and can be moved by the rhythm bonus, and neither belongs in a speed figure, so
    /// this reads its own window with both left out of the window choice and the conversion.</para>
    ///
    /// <para>THE WORD CONVENTION IS THE TYPING-TEST ONE: a word is <see cref="CHARS_PER_WORD"/>
    /// typeable cells, so WPM is exactly CPM/5. This file used to do the opposite, count real
    /// words, on the argument that the CPM:WPM ratio then carried the map's true word length.
    /// It did carry it, implicitly, which is the problem: a reader had to divide two numbers to
    /// recover it, and every WPM the map advertised was in a private unit that agreed with no
    /// typing test anywhere, not even with our own HUD (Gameplay/TypingEngine.cs, LiveWpm and
    /// LiveRollingWpm, have always divided characters by 5). So the ratio is now published
    /// outright as <see cref="AverageCharsPerWord"/>, and WPM is comparable with MonkeyType,
    /// with the in-game counter and with the number on the results screen.</para>
    ///
    /// <para>The two are coherent BY CONSTRUCTION, and the identity to hold on to is: a line
    /// whose average word is exactly <see cref="CHARS_PER_WORD"/> cells long has the same WPM
    /// under both conventions. Above 5 the new figure reads higher than the old one, below 5
    /// lower, in exact proportion. <see cref="AverageCharsPerWord"/> counts the inter-word
    /// space cells, because the 5 does too: a "word" in a typing test is five keystrokes, and
    /// the space after a word is a keystroke.</para>
    /// </summary>
    public readonly struct LyricPaceStatistics
    {
        /// <summary>
        /// Typeable cells per word, the typing-test convention. Same 5 as
        /// <c>TypingEngine.LiveWpm</c> and <c>LyricWpmCurve</c>; all three must agree or the
        /// map's advertised pace stops meaning what the HUD shows.
        /// </summary>
        public const double CHARS_PER_WORD = 5.0;

        /// <summary>
        /// <see cref="AverageCpm"/> / <see cref="CHARS_PER_WORD"/>, the whole-map rate in words:
        /// total typeable cells over total time sung, derived from the CPM rather than accumulated
        /// separately so the two can never drift apart by a rounding step.
        /// </summary>
        public double AverageWpm => AverageCpm / CHARS_PER_WORD;

        /// <summary>
        /// The whole-map rate: total typeable cells over the summed sung windows, where a sung
        /// window is the line's word spans plus every pause no wider than <see cref="break_min_ms"/>.
        /// Breaks, anything longer than that whether between two lines or inside one, are NOT in
        /// the denominator, and a line contributes time in proportion to how long it is sung rather
        /// than one vote.
        ///
        /// <para>The cell stream is the one <see cref="Compute"/> was asked for: the default stream
        /// (marks deleted, a hyphen a word break) unless the caller opted into the Literate one, in
        /// which case every authored mark is a typed cell and the rate is correspondingly higher.</para>
        /// </summary>
        public double AverageCpm { get; init; }

        /// <summary>
        /// <see cref="LineAverageCpm"/> / <see cref="CHARS_PER_WORD"/>: the unweighted mean of the
        /// per-line rates, the figure <see cref="AverageWpm"/> replaced.
        /// </summary>
        public double LineAverageWpm => LineAverageCpm / CHARS_PER_WORD;

        /// <summary>
        /// Mean of per-line (typeable cells / boundary window) rates: one vote per counted line,
        /// with the pause that follows each line inside that line's own window. Kept as the
        /// companion figure the whole-map rate is read against.
        /// </summary>
        public double LineAverageCpm { get; init; }

        /// <summary>
        /// The pace to SUSTAIN: the map's hardest window by RAW SPEED, re-expressed as the WPM an
        /// equally demanding <see cref="LyricDifficulty.TargetWindowSeconds"/> stretch would ask
        /// for. This is <see cref="LyricDifficulty.ModelResult.TargetWpm"/>, the same figure the
        /// Star Rating Sandbox prints beside every map, and the reason this statistic reads the
        /// difficulty model at all.
        ///
        /// <para>A window's raw speed is <c>wpm / capability(its own seconds)</c> - its pace against
        /// what the fastest humans sustain for that long - so translating it to one shared duration
        /// is a single multiplication by the capability curve and makes every map comparable. The
        /// window is chosen on the AUTHORED density with the typability multiplier and the rhythm
        /// bonus left out of BOTH the choice and the conversion, which is what stops this figure
        /// moving when either experiment does; it can therefore name a different window from the
        /// rating's peak. Bodies of fewer than <c>MinimumWindowChars</c> characters never qualify,
        /// so a map whose whole timeline is one short burst reports 0 here, exactly as it rates 0.</para>
        ///
        /// <para>DISPLAY ONLY: nothing here feeds a rating. The figure is FLOORED at
        /// <see cref="AverageWpm"/> (see <see cref="pace_floor_target"/>), so a map whose hardest
        /// window is slower than its own whole-map pace reports the average instead of a target
        /// below it - and a map with no qualifying window at all, which the model prices at 0,
        /// reports its average for the same reason.</para>
        ///
        /// <para>Both the window scan and the pace it is read at follow the same cell stream as
        /// <see cref="AverageCpm"/>, because they come from the difficulty model configured with the
        /// caller's <c>literate</c> flag. Under the mod the authored marks are real cells, so the
        /// hardest window is denser and the target rises with the average.</para>
        /// </summary>
        public double TargetWpm { get; init; }

        /// <summary>
        /// Typeable cells per word over the WHOLE map (<see cref="TypeableCellCount"/> /
        /// <see cref="WordCount"/>), inter-word spaces included. This is the number the old
        /// CPM:WPM ratio encoded implicitly; 0 for a map with no words.
        ///
        /// <para>A map total, not a mean of per-line ratios, so it is the length of the average
        /// word the player types rather than the average of the lines' averages. WPM and CPM go
        /// the other way (unweighted per-line means) because they are RATES and a per-line mean
        /// is what keeps a long instrumental gap from diluting them, while this is a pure count
        /// ratio with no time in it to dilute.</para>
        /// </summary>
        public double AverageCharsPerWord => WordCount == 0 ? 0 : (double)TypeableCellCount / WordCount;

        /// <summary>Total typeable cells (chars + inter-word spaces) across all lines.</summary>
        public int TypeableCellCount { get; init; }

        /// <summary>Total words (tokens containing at least one typeable char) across all lines.</summary>
        public int WordCount { get; init; }

        /// <summary>Guards degenerate data (zero/near-zero windows) from exploding the rate.</summary>
        private const double min_line_window_ms = 500;

        /// <summary>
        /// How long a pause has to be before it stops counting as singing time.
        ///
        /// <para>The engine's own rest threshold is 400 ms (the widest Great-late window in the
        /// judgement ladder, which is where the rhythmic-complexity section split comes from): a gap
        /// wider than that resets a player's pace. This is deliberately far more forgiving than the
        /// engine, so a breath between words still counts towards the average and only a real break
        /// is dropped whole. Lower it towards 0 to drop every pause, or raise it to include the
        /// song's longer silences as typing time.</para>
        /// </summary>
        private const double break_min_ms = 1000;

        /// <summary>
        /// Whether a target below the whole-map average is raised to it, the lab's own presentation
        /// rule (its <c>paceFloorTarget</c>), and it is ON.
        ///
        /// <para>WHY IT NEEDS A RULE AT ALL. The two figures answer different questions - the target
        /// is the pace of the map's hardest window, the average is the pace of the whole song - and
        /// on most maps the target is comfortably the higher of the two. On a map whose hardest
        /// stretch is SLOWER than its relentless average they invert: the figure presented as "the
        /// pace this map asks for" comes out below the pace the map already demands everywhere. Three
        /// maps in the bundled pool do that under the live dials. Raising the target to the average
        /// whenever the average is the higher of the two removes the contradiction.</para>
        ///
        /// <para>DISPLAY ONLY, like both figures: no rating reads either one, so this moves no star.
        /// A map with no qualifying window at all reads its average here rather than the 0 the model
        /// returned, since every positive average is above it.</para>
        /// </summary>
        private const bool pace_floor_target = true;

        /// <summary>
        /// The time a line is actually SUNG, walked word span by word span: every span counts, every
        /// pause counts up to <see cref="break_min_ms"/>, and a pause wider than that is a break and
        /// is dropped whole. A line carrying no word timings falls back to its vocal end, which is
        /// the figure the whole-map average divided by before the breaks became a threshold.
        /// </summary>
        private static double SungWindow(LyricLine line)
        {
            if (line.Units.Count == 0)
                return Math.Max(line.SingEndTime - line.StartTime, 0);

            double charged = 0;
            double cursor = line.StartTime;

            // A line whose end precedes its start (a mid-edit line, or a malformed import) has no
            // window to charge: clamping against it would throw, so it reads as its own start and
            // the walk below charges nothing for it.
            double lineEnd = Math.Max(line.EndTime, line.StartTime);

            foreach (TimedUnit unit in line.Units)
            {
                double start = Math.Clamp(unit.StartTime, line.StartTime, lineEnd);
                double end = Math.Clamp(unit.EndTime, start, lineEnd);
                double gap = start - cursor;

                if (gap > 0 && gap <= break_min_ms)
                    charged += gap;

                charged += end - start;
                cursor = Math.Max(cursor, end);
            }

            double tail = lineEnd - cursor;

            if (tail > 0 && tail <= break_min_ms)
                charged += tail;

            return charged;
        }

        /// <summary>
        /// The pace figures for <paramref name="lines"/>.
        ///
        /// <para><paramref name="literate"/> selects the cell stream, exactly as
        /// <c>TypingLine.FromLyricLine</c> does for the engine: off (the default) is
        /// <see cref="Typeability.ToDefaultStream"/>, the play everyone shares; on is the authored
        /// text, where every supported punctuation mark is a typed cell of its own and a hyphen is
        /// no longer a word break. A caller that has a converted beatmap must therefore ask for the
        /// stream that map was converted with, or its figures describe a different play.</para>
        /// </summary>
        /// <param name="rate">
        /// The CLOCK the map is read at: 1 for no rate mod, 1.5 for DoubleTime, 0.75 for HalfTime, and
        /// whatever a custom rate mod asks for. The two whole-map rates are cells over TIME, so they
        /// scale with it exactly; the TARGET is not, because the fixed reading duration it is
        /// re-expressed at is a duration the faster clock also shortens - so it is recomputed through
        /// the difficulty model at this rate rather than multiplied (see <see cref="TargetWpm"/>).
        /// </param>
        public static LyricPaceStatistics Compute(IEnumerable<LyricLine> lines, bool literate = false, double rate = 1)
        {
            // Materialised because the target below reads the same lines again through the
            // difficulty model, and a caller is free to hand in a lazy sequence.
            List<LyricLine> lineList = lines as List<LyricLine> ?? lines.ToList();

            int totalCells = 0;
            int totalWords = 0;
            int lineCount = 0;
            double cpmSum = 0;
            double vocalMinutes = 0;
            var lineCpms = new List<double>();

            foreach (var line in lineList)
            {
                // Cell arithmetic mirrors TypingLine.FromLyricLine: every typeable char is a
                // cell, plus one typeable space cell per token gap.
                //
                // The DEFAULT stream unless the caller opted into the Literate one. The default
                // path is byte-identical to what every stored figure was computed on:
                // ToDefaultStream is idempotent, so on an already-stripped line this is exactly the
                // old arithmetic (lower-casing cannot change any count). The Literate path is the
                // authored text, where a mark is a typed cell and a hyphen keeps its word together,
                // so both the cell total and the word total move with the mod.
                string[] tokens = (literate ? line.RawText : Typeability.ToDefaultStream(line.RawText)).Split(' ');

                int cells = tokens.Length - 1;
                int words = 0;

                foreach (string token in tokens)
                {
                    int counted = 0;

                    foreach (char ch in token)
                    {
                        // TYPING, not mere keypresses: a freestyle slot takes any key, so no map can
                        // ask for a particular speed in one. IsCell would count them; IsTypeable does
                        // not, and the pace figure is about the speed the map actually demands.
                        // Under Literate a supported mark is demanded too, so it counts exactly as
                        // the engine makes it a typed cell.
                        if (Typeability.IsTypeable(ch) || (literate && Typeability.IsPunctuation(ch)))
                            counted++;
                    }

                    cells += counted;

                    if (counted > 0)
                        words++;
                }

                if (cells <= 0)
                    continue;

                double windowMinutes = Math.Max(line.EndTime - line.StartTime, min_line_window_ms) / 60000.0;

                // WPM is not accumulated here: it is CPM/5 by definition (see AverageWpm), so a
                // second sum could only introduce a way for the two to disagree.
                double lineCpm = cells / windowMinutes;

                // The whole-map denominator: the time the line is actually SUNG, walked span by span
                // so that short pauses count and real breaks do not (see SungWindow).
                vocalMinutes += Math.Max(SungWindow(line), min_line_window_ms) / 60000.0;

                cpmSum += lineCpm;
                lineCpms.Add(lineCpm);

                totalCells += cells;
                totalWords += words;
                lineCount++;
            }

            if (lineCount == 0)
                return default;

            double averageCpm = vocalMinutes > 0 ? totalCells / vocalMinutes : 0;

            // THE TARGET. Read from the difficulty model, which is where the window schedule, the
            // density prefix and the capability curve live: this statistic has no second copy of
            // the scan, so the figure song select prints and the figure the sandbox prints are the
            // same computation rather than two that have to be kept in step by hand. The envelope
            // axis is asked for by name because that is the arm that owns the scan on every
            // configuration - the chunked axis rates none of it - and typability and rhythm are
            // left out of the figure there by construction (see TargetWpm).
            //
            // Guarded rather than thrown: a map too long for the model (30 minutes at the read
            // rate) still has a perfectly good average, and dropping the target is a better answer
            // for a metadata strip than refusing to draw the map at all.
            double targetWpm = 0;

            try
            {
                // Read AT THE RATE, because the model's own scan is what "the SR algorithm's target"
                // means: a faster clock does not merely scale the figures it finds, it moves the reading
                // durations (the timeline is binned in REAL milliseconds) and can therefore name a
                // different window. Scaling the rate-1 target would answer a different question.
                targetWpm = LyricDifficulty
                    .ComputeDetail(lineList, rate, literate, LyricDifficulty.EnduranceAxis.Envelope)
                    .TargetWpm;
            }
            catch (InvalidOperationException)
            {
                targetWpm = 0;
            }

            // The two whole-map rates scale with the clock; the target is already read at it.
            averageCpm *= rate;

            if (pace_floor_target && averageCpm / CHARS_PER_WORD > targetWpm)
                targetWpm = averageCpm / CHARS_PER_WORD;

            return new LyricPaceStatistics
            {
                AverageCpm = averageCpm,
                LineAverageCpm = cpmSum / lineCount * rate,
                TargetWpm = targetWpm,
                TypeableCellCount = totalCells,
                WordCount = totalWords,
            };
        }
    }
}
