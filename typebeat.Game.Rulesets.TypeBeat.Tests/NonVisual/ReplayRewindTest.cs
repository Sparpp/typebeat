// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Backlog 165: seeking BACKWARDS while watching a replay or autoplay froze gameplay. The engine has
// no reverse gear (Update clamps its delta at zero and seals lines one way only) and the ticker's
// frame index only ever grew, so a rewound clock left every cell, the caret and the active line
// pinned at their pre-seek values while the song played on.
//
// The fix re-derives instead of unwinding: reset in place, replay the whole prefix, announce nothing
// but one Rewound edge. These pins hold that re-derivation to the only standard it can be judged
// against, which is a run that was simply watched straight to the seek target and never rewound.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using NUnit.Framework;
using typebeat.Game.Replays;
using typebeat.Game.Rulesets.Replays;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class ReplayRewindTest
    {
        #region Fixture

        /// <summary>The cadence both sides of every comparison tick at, and the one
        /// <see cref="ReplayEngineFeed.RebuildTo"/> uses. Judgement is cadence-independent; the WPM
        /// clock is not, so a fair comparison has to hold it fixed.</summary>
        private const double frame_ms = ReplayEngineFeed.FRAME_MS;

        private static readonly string[] line_text = { "abcd", "efgh", "ijkl" };

        private const double line_ms = 4000;

        /// <summary>
        /// Three four-cell lines back to back, each singing for the first 3s of its 4s window so
        /// there is a seal grace and a dead zone to land a seek in.
        /// </summary>
        private static LyricBeatmap beatmap()
        {
            var lines = new List<LyricLine>();

            for (int k = 0; k < line_text.Length; k++)
            {
                double start = k * line_ms;

                lines.Add(new LyricLine
                {
                    RawText = line_text[k],
                    StartTime = start,
                    EndTime = start + line_ms,
                    SingEndTime = start + 3000,
                    Units = new[] { new TimedUnit { Text = line_text[k], StartTime = start, EndTime = start + 3000 } },
                });
            }

            return new LyricBeatmap
            {
                Metadata = new LyricBeatmapMetadata { Artist = "a", Title = "rewind", FolderPath = string.Empty, AudioFileName = "a.mp3" },
                Lines = lines,
                Granularity = TimingGranularity.Line,
            };
        }

        /// <summary>Cell target times of one line, read off the engine's own flattening.</summary>
        private static IReadOnlyList<double> targets(LyricBeatmap map, int lineIndex)
            => TypingLine.FromLyricLine(map.Lines[lineIndex], map.Granularity, false).Cells.Select(c => c.TargetTime).ToList();

        /// <summary>
        /// A deliberately messy run, so the rewind has something to get wrong: a clean first line, a
        /// typo the player backspaces and fixes (which restores the broken streak) plus a character
        /// the line runs out of time on, and a typo left standing to seal as an unfixed typo.
        /// Times are integral, exactly as <c>TypeBeatReplayRecorder</c> stamps them.
        /// </summary>
        private static Replay run(LyricBeatmap map)
        {
            var frames = new List<ReplayFrame> { TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true) };

            void press(double time, char c) => frames.Add(new TypeBeatReplayFrame(Math.Round(time), c));

            var first = targets(map, 0);
            for (int i = 0; i < line_text[0].Length; i++)
                press(first[i], line_text[0][i]);

            var second = targets(map, 1);
            press(second[0], 'e');
            press(second[1], 'x');            // typo, typed through
            press(second[1] + 40, '\b');      // backspace
            press(second[1] + 80, 'f');       // fixed: restores the streak the typo broke
            press(second[2], 'g');
            // 'h' is never typed: the line seals it as a Miss.

            var third = targets(map, 2);
            press(third[0], 'i');
            press(third[1], 'q');             // typo left standing: seals as an unfixed typo
            press(third[2], 'k');
            press(third[3], 'l');

            var replay = new Replay();
            replay.Frames.AddRange(frames);
            return replay;
        }

        /// <summary>
        /// Three two-word lines, so a space pressed inside a word has something to abandon
        /// (backlog 167). Same shape and timings as <see cref="beatmap"/> otherwise.
        /// </summary>
        private static LyricBeatmap skipBeatmap()
        {
            var lines = new List<LyricLine>();

            for (int k = 0; k < line_text.Length; k++)
            {
                double start = k * line_ms;
                string text = line_text[k];

                lines.Add(new LyricLine
                {
                    RawText = text[..2] + " " + text[2..],
                    StartTime = start,
                    EndTime = start + line_ms,
                    SingEndTime = start + 3000,
                    Units = new[]
                    {
                        new TimedUnit { Text = text[..2], StartTime = start, EndTime = start + 1500 },
                        new TimedUnit { Text = text[2..], StartTime = start + 1500, EndTime = start + 3000 },
                    },
                });
            }

            return new LyricBeatmap
            {
                Metadata = new LyricBeatmapMetadata { Artist = "a", Title = "rewind", FolderPath = string.Empty, AudioFileName = "a.mp3" },
                Lines = lines,
                Granularity = TimingGranularity.Line,
            };
        }

        /// <summary>
        /// A run through <see cref="skipBeatmap"/> that exercises both fates of an abandoned word:
        /// line 0's second word is skipped and RECLAIMED (backspaced back into and typed out), line
        /// 1's is skipped and left, so it seals as misses. The CONFIG frame carries the setting, as a
        /// real recording does.
        /// </summary>
        private static Replay skipRun(LyricBeatmap map)
        {
            var frames = new List<ReplayFrame> { TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true, spaceSkipsWord: true) };

            void press(double time, char c) => frames.Add(new TypeBeatReplayFrame(Math.Round(time), c));

            // Line 0 is "ab cd": type "ab", skip the gap into "cd", abandoning it, then go back for
            // it and type it out.
            var first = targets(map, 0);
            press(first[0], 'a');
            press(first[1], 'b');
            press(first[2], ' '); // ON the gap: an ordinary typed space
            press(first[3] + 100, ' '); // inside "cd": abandons it, and the line is complete
            press(first[3] + 200, TypeBeatReplayFrame.BACKSPACE); // back into the word, over the gap it lands on
            press(first[3] + 250, ' '); // the gap again (scoring-inert: it was already earned)
            press(first[3] + 300, 'c');
            press(first[3] + 400, 'd');

            // Line 1 is "ef gh": type "e", skip the rest of the word and never come back.
            var second = targets(map, 1);
            press(second[0], 'e');
            press(second[1] + 100, ' '); // abandons 'f', lands on the gap and types it
            press(second[3], 'g');
            press(second[4], 'h');

            var replay = new Replay();
            replay.Frames.AddRange(frames);
            return replay;
        }

        /// <summary>
        /// The engine ticker's loop, headless, and the same one <see cref="ReplayEngineFeed.RebuildTo"/>
        /// runs: per-display-frame Updates with each due frame applied as Update(frameTime) + the
        /// keystroke, then a final Update landing exactly on <paramref name="time"/>. Written out by
        /// hand rather than delegated, so a comparison against RebuildTo is a comparison and not a
        /// tautology. Returns how many frames were consumed.
        /// </summary>
        private static int playTo(TypingEngine engine, Replay replay, double time)
        {
            var frames = replay.Frames;
            int next = 0;

            void due(double now)
            {
                while (next < frames.Count && frames[next].Time <= now)
                {
                    var frame = (TypeBeatReplayFrame)frames[next];

                    if (frame.IsConfig)
                    {
                        engine.AllowWrongInput = frame.AllowWrongInput;
                        engine.SpaceSkipsWord = frame.SpaceSkipsWord;
                    }
                    else
                    {
                        engine.Update(frame.Time);

                        if (frame.IsBackspace)
                            engine.ProcessBackspace();
                        else
                            engine.ProcessKey(frame.Character, frame.Time);
                    }

                    next++;
                }
            }

            for (double now = 0; now <= time; now += frame_ms)
            {
                due(now);
                engine.Update(now);
            }

            due(time);
            engine.Update(time);

            return next;
        }

        /// <summary>
        /// Everything the engine carries that a play can move: the run's public account, the WPM and
        /// sync readouts (which the monotonic clock and the sync timeline feed), and the full cell
        /// grid, which is what the lyric stack renders and what a freeze leaves stale.
        /// </summary>
        private static string snapshot(TypingEngine engine)
        {
            var results = engine.BuildResults();
            var text = new StringBuilder();

            text.Append(CultureInfo.InvariantCulture, $"active={engine.ActiveLineIndex} caret={engine.CaretIndex} finished={engine.IsFinished} ");
            text.Append(CultureInfo.InvariantCulture, $"nextUnsealed={engine.NextUnsealedLineIndex} lineComplete={engine.IsLineComplete} ");
            text.Append(CultureInfo.InvariantCulture, $"score={engine.Score} combo={engine.Combo} maxCombo={engine.MaxCombo} mistypes={engine.Mistypes} ");
            text.Append(CultureInfo.InvariantCulture, $"wrongStreak={engine.ConsecutiveWrongKeys} acc={engine.LiveAccuracy:F9} ");
            text.Append(CultureInfo.InvariantCulture, $"wpm={engine.LiveWpm:F9} rolling={engine.LiveRollingWpm:F9} sync={engine.LiveSyncPercent:F9} ");
            text.Append(CultureInfo.InvariantCulture, $"resultWpm={results.Wpm:F9} resultSync={results.SyncPercent:F9} ");

            foreach (var pair in results.Counts.OrderBy(p => p.Key))
                text.Append(CultureInfo.InvariantCulture, $"{pair.Key}={pair.Value} ");

            foreach (var sample in results.SyncTimeline)
                text.Append(CultureInfo.InvariantCulture, $"({sample.Time:F3},{sample.Delta:F3})");

            foreach (var line in engine.Lines)
            {
                foreach (var cell in line.Cells)
                    text.Append(CultureInfo.InvariantCulture, $"[{cell.State},{cell.TypedChar?.ToString() ?? "-"},{cell.JudgedDelta?.ToString("F6", CultureInfo.InvariantCulture) ?? "-"}]");

                text.Append('|');
            }

            return text.ToString();
        }

        /// <summary>The end of the map plus enough tail for every line to seal.</summary>
        private static double past_the_end => line_text.Length * line_ms + 10000;

        #endregion

        /// <summary>
        /// THE PROPERTY. Watch the whole run, seek back to T, and the engine must hold exactly the
        /// state it would have held had it been watched straight to T and stopped there: same cells,
        /// same caret, same active line, same counts, same WPM clock.
        ///
        /// <para>The seek targets cover the shapes a rewind can land in: before anything is typed,
        /// mid-line, in the dead zone after a line has sealed, and on a line that seals a miss and an
        /// unfixed typo behind it.</para>
        /// </summary>
        [TestCase(0)]
        [TestCase(500)]
        [TestCase(2000)]
        [TestCase(3500)]
        [TestCase(4200)]
        [TestCase(6000)]
        [TestCase(7900)]
        [TestCase(9000)]
        [TestCase(11500)]
        public void RewindLandsOnTheStateAStraightWatchWouldHave(double seekTarget)
        {
            var map = beatmap();
            var replay = run(map);

            var straight = new TypingEngine(map);
            int consumedStraight = playTo(straight, replay, seekTarget);

            var rewound = new TypingEngine(map);
            playTo(rewound, replay, past_the_end);

            // The freeze this fixes: without the rebuild the engine is still sitting at the end of
            // the run. Pinned so a rebuild that silently did nothing could not pass the test below.
            Assert.That(snapshot(rewound), Is.Not.EqualTo(snapshot(straight)), "fixture must actually distinguish the two states");

            int consumedRewound = ReplayEngineFeed.RebuildTo(rewound, replay.Frames, seekTarget);

            Assert.That(snapshot(rewound), Is.EqualTo(snapshot(straight)));
            Assert.That(consumedRewound, Is.EqualTo(consumedStraight), "the feeder must resume from the same frame");
        }

        /// <summary>
        /// A rebuild announces NOTHING it walked back over, and exactly one <c>Rewound</c>. This is
        /// what keeps the drawable layer honest: a cell takes only its first osu result, so re-emitted
        /// judgements would be dropped for the cells before the seek target and double-counted, via
        /// the hand-mirrored mistype counter, for the ones after it.
        /// </summary>
        [Test]
        public void ARebuildIsSilentApartFromOneRewound()
        {
            var map = beatmap();
            var replay = run(map);

            var engine = new TypingEngine(map);
            playTo(engine, replay, past_the_end);

            int announced = 0;
            int rewound = 0;

            engine.CharJudged += _ => announced++;
            engine.LineSealed += _ => announced++;
            engine.LineActivated += _ => announced++;
            engine.ComboBroken += () => announced++;
            engine.Mistyped += () => announced++;
            engine.ComboRestored += _ => announced++;
            engine.WrongKeyRejected += _ => announced++;
            engine.Finished += () => announced++;
            // The fixture backspaces over a typo, so this one is not a formality: its drain rides on
            // CharJudged and is therefore already silent here, and an ungated erase would refund a
            // drain that a rebuild never took (backlog 166 landing on top of this).
            engine.TypoErased += () => announced++;
            engine.Rewound += () => rewound++;

            ReplayEngineFeed.RebuildTo(engine, replay.Frames, 6000);

            Assert.That(announced, Is.Zero, "the rebuild must not re-announce the run it walked back over");
            Assert.That(rewound, Is.EqualTo(1));
        }

        /// <summary>
        /// The same property with a WORD SKIP in the run (backlog 167). The phantom state is cell
        /// state like any other, so a rebuild has to land on it exactly: the targets here bracket a
        /// word while it is abandoned, after it has been reclaimed, and after a second one has sealed
        /// as misses without ever being reclaimed.
        /// </summary>
        [TestCase(0)]
        [TestCase(1650)]
        [TestCase(1780)]
        [TestCase(2000)]
        [TestCase(5000)]
        [TestCase(9000)]
        [TestCase(11500)]
        public void RewindLandsOnTheStateAStraightWatchWouldHaveWithASkipInTheRun(double seekTarget)
        {
            var map = skipBeatmap();
            var replay = skipRun(map);

            var straight = new TypingEngine(map);
            int consumedStraight = playTo(straight, replay, seekTarget);

            var rewound = new TypingEngine(map);
            playTo(rewound, replay, past_the_end);

            Assert.That(snapshot(rewound), Is.Not.EqualTo(snapshot(straight)), "fixture must actually distinguish the two states");

            int consumedRewound = ReplayEngineFeed.RebuildTo(rewound, replay.Frames, seekTarget);

            Assert.That(snapshot(rewound), Is.EqualTo(snapshot(straight)));
            Assert.That(consumedRewound, Is.EqualTo(consumedStraight), "the feeder must resume from the same frame");
        }

        /// <summary>
        /// The fixture really does put a word into the phantom state and take it out again by both
        /// exits, which is what makes the seek targets above worth their run time.
        /// </summary>
        [Test]
        public void TheSkipFixtureAbandonsReclaimsAndSeals()
        {
            var map = skipBeatmap();
            var replay = skipRun(map);

            // A fresh engine per checkpoint: playTo always feeds from the first frame.
            var abandoned = new TypingEngine(map);
            playTo(abandoned, replay, 1650);
            Assert.That(abandoned.Lines[0].Cells[3].State, Is.EqualTo(CellState.Abandoned), "abandoned by the skip");

            var reclaimed = new TypingEngine(map);
            playTo(reclaimed, replay, 1900);
            Assert.That(reclaimed.Lines[0].Cells[3].State, Is.EqualTo(CellState.Correct), "reclaimed and typed for real");

            var finished = new TypingEngine(map);
            playTo(finished, replay, past_the_end);
            Assert.That(finished.Lines[1].Cells[1].State, Is.EqualTo(CellState.Missed), "the skip nobody came back for");
            // That one cell, plus the whole of the third line, which the run never touches.
            Assert.That(finished.BuildResults().Counts[JudgementType.Miss], Is.EqualTo(6));
        }

        /// <summary>
        /// The three seams a word skip announces on are gated by the same rebuild silence every other
        /// event is (backlog 165). They carry HP by hand, in both directions, so an ungated one would
        /// drain or refund per skip in the whole prefix a backwards seek walks over.
        /// </summary>
        [Test]
        public void ARebuildIsSilentAboutAnAbandonedWordToo()
        {
            var map = skipBeatmap();
            var replay = skipRun(map);

            var engine = new TypingEngine(map);
            playTo(engine, replay, past_the_end);

            int announced = 0;
            int rewound = 0;

            engine.WordAbandoned += _ => announced++;
            engine.AbandonReclaimed += _ => announced++;
            engine.AbandonSealed += _ => announced++;
            engine.CharJudged += _ => announced++;
            engine.LineSealed += _ => announced++;
            engine.ComboBroken += () => announced++;
            engine.ComboRestored += _ => announced++;
            engine.Rewound += () => rewound++;

            ReplayEngineFeed.RebuildTo(engine, replay.Frames, 6000);

            Assert.That(announced, Is.Zero, "the rebuild must not re-announce the skips it walked back over");
            Assert.That(rewound, Is.EqualTo(1));
        }

        /// <summary>
        /// The engine's own mistype count is what the score processor's hand-mirrored counter is
        /// re-derived from after a rewind (<c>TypeBeatScoreProcessor.ResyncAfterRewind</c>), so the
        /// two have to mean the same thing: one count per announced <c>Mistyped</c>. Pinned here
        /// because the resync is the one place the count is written from anywhere but that event.
        /// </summary>
        [Test]
        public void MistypeCountMatchesTheAnnouncedMistypes()
        {
            var map = beatmap();
            var replay = run(map);

            var engine = new TypingEngine(map);

            int announced = 0;
            engine.Mistyped += () => announced++;

            playTo(engine, replay, past_the_end);

            Assert.That(announced, Is.EqualTo(2), "the fixture types two wrong characters");
            Assert.That(engine.Mistypes, Is.EqualTo(announced));

            ReplayEngineFeed.RebuildTo(engine, replay.Frames, 6000);

            Assert.That(engine.Mistypes, Is.EqualTo(1), "only the first typo is inside the seek target");
        }

        /// <summary>
        /// A rebuild re-judges the SAME run, so everything describing how the run is judged survives
        /// it: the mod flags, the window scale and the two era rules. Only progress is wiped. (The
        /// replay CONFIG bits are excluded on purpose: the header frame re-feeds them, which is
        /// itself the pin that a rebuild replays from the true beginning.)
        ///
        /// <para><see cref="TypingEngine.FletcherEnabled"/> is now one of those CONFIG bits
        /// (backlog 208, flags bit 5), so it is asserted through its DERIVATION rather than as a
        /// preserved setting: the fixture's frames carry the bit clear, and
        /// <see cref="TypingEngine.FlexibleCaretFromMod"/> is the half of the answer the frame
        /// cannot give (the retired "FT" mod on the score). That flag is a mod fact, so it survives
        /// the reset like any other, and re-feeding the header has to reach the same unpinned caret
        /// a second time.</para>
        /// </summary>
        [Test]
        public void ARebuildKeepsTheSettingsTheRunIsJudgedUnder()
        {
            var map = beatmap();
            var replay = run(map);

            var engine = new TypingEngine(map)
            {
                FlexibleCaretFromMod = true,
                FletcherEnabled = true,
                MashingEnabled = true,
                WindowScale = 1.4,
                ComboRestore = ComboRestoreRule.Never,
                SpaceTiming = SpaceTimingRule.Timed,
                WordSkip = WordSkipRule.ImmediateMiss,
            };

            playTo(engine, replay, past_the_end);
            ReplayEngineFeed.RebuildTo(engine, replay.Frames, 2000);

            Assert.That(engine.FlexibleCaretFromMod, Is.True);
            Assert.That(engine.FletcherEnabled, Is.True, "the retired mod's unpinned caret must survive the header being re-fed");
            Assert.That(engine.FlexibleLineSnap, Is.False, "an FT-era run never had the line-start snap");
            Assert.That(engine.MashingEnabled, Is.True);
            Assert.That(engine.WindowScale, Is.EqualTo(1.4));
            Assert.That(engine.ComboRestore, Is.EqualTo(ComboRestoreRule.Never));
            Assert.That(engine.SpaceTiming, Is.EqualTo(SpaceTimingRule.Timed));
            Assert.That(engine.WordSkip, Is.EqualTo(WordSkipRule.ImmediateMiss));
        }

        /// <summary>
        /// The other half of the same seam (backlog 208): a run recorded in the FLEXIBLE-LINES era
        /// carries flags bit 5 SET, and a rebuild has to put both flags back from the header even
        /// though the engine being rebuilt has no mod telling it anything. Without the bit surviving
        /// the re-feed the rewind would re-derive a pinned caret and land every keystroke after the
        /// seek target on a different line.
        /// </summary>
        [Test]
        public void ARebuildRestoresTheFlexibleLinesEraFromTheHeader()
        {
            var map = beatmap();
            var replay = run(map);

            // Stamp the fixture's header the way the live recorder does today.
            var header = replay.Frames.OfType<TypeBeatReplayFrame>().First(f => f.IsConfig);
            header.FlexibleLines = true;

            var engine = new TypingEngine(map);

            Assert.That(engine.FletcherEnabled, Is.False, "the engine default is the classic pinned era");

            // RebuildTo is the seek path, and it goes through ReplayEngineFeed.Apply, the one place
            // a recorded frame reaches an engine.
            ReplayEngineFeed.RebuildTo(engine, replay.Frames, past_the_end);

            Assert.That(engine.FletcherEnabled, Is.True);
            Assert.That(engine.FlexibleLineSnap, Is.True);

            // And again, backwards: a second rebuild resets everything the run touched and has to
            // reach the same era a second time off the same header.
            ReplayEngineFeed.RebuildTo(engine, replay.Frames, 2000);

            Assert.That(engine.FletcherEnabled, Is.True);
            Assert.That(engine.FlexibleLineSnap, Is.True);
        }

        /// <summary>
        /// Cell OBJECT IDENTITY survives a rebuild. The lyric stack's line displays hold the cells
        /// handed out at construction for the whole play, so a rebuild that swapped them (a fresh
        /// engine) would leave the display bound to objects nothing writes to and reintroduce the
        /// freeze it was meant to fix, silently.
        /// </summary>
        [Test]
        public void ARebuildKeepsCellIdentity()
        {
            var map = beatmap();
            var replay = run(map);

            var engine = new TypingEngine(map);
            var before = engine.Lines.SelectMany(l => l.Cells).ToArray();

            playTo(engine, replay, past_the_end);
            ReplayEngineFeed.RebuildTo(engine, replay.Frames, 2000);

            Assert.That(engine.Lines.SelectMany(l => l.Cells).ToArray(), Is.EqualTo(before).Using<TypingCell>(ReferenceEquals));
        }
    }
}
