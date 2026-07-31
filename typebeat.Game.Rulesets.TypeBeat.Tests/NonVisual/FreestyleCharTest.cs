// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.Formats;
using typebeat.Game.IO;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Screens.Edit;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// FREESTYLE characters (backlog 22): a mapper writes '&amp;' into a lyric line and gets a cell
    /// any key satisfies, whose pressed char stays on screen. This fixture pins the three halves of
    /// that feature that can be tested headlessly:
    /// <list type="bullet">
    /// <item>STORAGE: '&amp;' is a marker only for lines that opted in (<c>"freestyle": true</c>), so
    /// no map written before this feature can change meaning, and the editor's save round-trips it.</item>
    /// <item>JUDGEMENT: a freestyle cell is a completely normal typeable cell except that every char
    /// but SPACE matches it, including under the Literate, Mashing and allow-wrong-input rules, and
    /// the pressed char survives backspace/retype and replay playback.</item>
    /// <item>SHIMMER: the obfuscated-glyph pool is width-grouped and deterministic.</item>
    /// </list>
    /// </summary>
    [TestFixture]
    public class FreestyleCharTest
    {
        private const char marker = Typeability.FREESTYLE_MARKER;

        [SetUp]
        public void SetUp() => LyricBeatmapDecoder.Register();

        #region Fixture builders

        private static TimedUnit unit(string text, double start, double end)
            => new TimedUnit { Text = text, StartTime = start, EndTime = end };

        private static LyricLine line(string text, double start, double end, double singEnd, params TimedUnit[] units)
            => new LyricLine { RawText = text, StartTime = start, EndTime = end, SingEndTime = singEnd, Units = units };

        private static LyricBeatmap map(params LyricLine[] lines) => new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata
            {
                Artist = "Test",
                Title = "Song",
                FolderPath = @"X:\nowhere",
                AudioFileName = "a.mp3",
            },
            Lines = lines,
            Granularity = TimingGranularity.Word,
        };

        /// <summary>
        /// One line "a&amp;b", active [1000, 4000), single word spanning [1000, 4000]; three cells at
        /// 1000 / 2000 / 3000 ('a', the freestyle slot, 'b').
        /// </summary>
        private static LyricBeatmap freestyleMap() => map(line("a" + marker + "b", 1000, 5000, 4000, unit("a" + marker + "b", 1000, 4000)));

        private static TypingEngine activeEngine()
        {
            var engine = new TypingEngine(freestyleMap());
            engine.Update(1000);
            Assert.AreEqual(0, engine.ActiveLineIndex);
            return engine;
        }

        #endregion

        #region Typeability and flattening

        [Test]
        public void MarkerIsNotTypeableAndIsStrippedByDefault()
        {
            // The marker must stay outside the typeable surface (KeyCharMap cannot produce it), and
            // outside default normalization, which is what makes it invisible to every legacy path.
            Assert.IsFalse(Typeability.IsTypeable(marker));
            Assert.IsTrue(Typeability.IsFreestyle(marker));
            Assert.IsTrue(Typeability.IsCell(marker));

            Assert.AreEqual("RB rock roll", Typeability.Normalize("R&B rock & roll"));
        }

        [Test]
        public void MarkerSurvivesNormalizeWhenOptedIn()
        {
            Assert.AreEqual("R&B rock & roll", Typeability.Normalize("R&B rock & roll", keepFreestyleMarkers: true));

            // Everything else normalizes as ever: whitespace collapsed, supported punctuation kept
            // (it is stripped from the DEFAULT typed stream, not from the stored line).
            Assert.AreEqual("hey, &you!", Typeability.Normalize("  hey,   &you!  ", keepFreestyleMarkers: true));
            Assert.AreEqual("hey &you", Typeability.ToDefaultStream(Typeability.Normalize("  hey,   &you!  ", keepFreestyleMarkers: true)));
        }

        [Test]
        public void TypeableCountCountsFreestyleSlots()
        {
            Assert.AreEqual(3, Typeability.TypeableCount("a" + marker + "b"));
            Assert.AreEqual(2, Typeability.TypeableCount("ab")); // unchanged for marker-free text
        }

        [Test]
        public void FreestyleCellIsTypeableAndTimedLikeALetter()
        {
            var typingLine = TypingLine.FromLyricLine(freestyleMap().Lines[0], TimingGranularity.Word);

            Assert.AreEqual(3, typingLine.Cells.Count);
            Assert.AreEqual(3, typingLine.TypeableCount);

            // The slot takes its share of the word's span: 1000 + j*(4000-1000)/3.
            Assert.AreEqual(1000, typingLine.Cells[0].TargetTime);
            Assert.AreEqual(2000, typingLine.Cells[1].TargetTime);
            Assert.AreEqual(3000, typingLine.Cells[2].TargetTime);

            Assert.IsTrue(typingLine.Cells[1].IsTypeable);
            Assert.IsTrue(typingLine.Cells[1].IsFreestyle);
            Assert.AreEqual(marker, typingLine.Cells[1].Expected);

            Assert.IsFalse(typingLine.Cells[0].IsFreestyle);
            Assert.IsFalse(typingLine.Cells[2].IsFreestyle);
        }

        #endregion

        #region Judgement

        [TestCase('q')]
        [TestCase('Z')]
        [TestCase('7')]
        // "any key" is every key on the typeable surface but space (see the space cases below).
        public void AnyKeyFillsAFreestyleCellAndTheTypedCharIsKept(char pressed)
        {
            var engine = activeEngine();
            var cells = engine.Lines[0].Cells;

            Assert.IsTrue(engine.ProcessKey('a', 1000));
            engine.Update(2000);

            Assert.IsTrue(engine.ProcessKey(pressed, 2000));

            Assert.AreEqual(CellState.Correct, cells[1].State);
            Assert.AreEqual(pressed, cells[1].TypedChar);
            Assert.AreEqual(0, cells[1].JudgedDelta!.Value); // on target => Perfect
            Assert.AreEqual(2, engine.CaretIndex);
            Assert.AreEqual(2, engine.Combo);
            Assert.AreEqual(0, engine.ConsecutiveWrongKeys);
            Assert.AreEqual(1.0, engine.LiveAccuracy); // no error was recorded
        }

        [Test]
        public void SpaceIsRejectedOnAFreestyleCellExactlyLikeAnyWrongKey()
        {
            // Backlog 50: space is the word-advance key, not a glyph a player means to leave sitting
            // in a lyric, so it is the one key a freestyle cell does NOT take. It then falls through
            // the ordinary non-match path, so the consequences must be indistinguishable from a
            // space pressed on an ordinary letter cell: that control run is the assertion.
            var free = activeEngine();
            var control = new TypingEngine(map(line("axb", 1000, 5000, 4000, unit("axb", 1000, 4000))));
            control.Update(1000);

            foreach (var engine in new[] { free, control })
            {
                int judged = 0;
                var rejected = new List<char>();
                engine.CharJudged += _ => judged++;
                engine.WrongKeyRejected += rejected.Add;

                Assert.IsTrue(engine.ProcessKey('a', 1000));
                judged = 0; // the correct 'a' judged; count only what the space does.
                engine.Update(2000);

                // Handled (the press was not inert) but REJECTED: nothing lands in the cell.
                Assert.IsTrue(engine.ProcessKey(' ', 2000));

                var cell = engine.Lines[0].Cells[1];
                Assert.AreEqual(CellState.Untyped, cell.State);
                Assert.IsNull(cell.TypedChar);
                Assert.IsNull(cell.JudgedDelta);
                Assert.AreEqual(1, engine.CaretIndex); // caret unmoved: the slot is still open
                Assert.AreEqual(0, engine.Combo);
                Assert.AreEqual(1, engine.ConsecutiveWrongKeys);
                Assert.AreEqual(0.5, engine.LiveAccuracy); // one correct of two keypresses
                Assert.AreEqual(0, judged); // no CharJudged for a rejected key
                Assert.AreEqual(1, rejected.Count);
                Assert.AreEqual(' ', rejected[0]);

                // The slot is still fillable afterwards; the space cost combo, not the cell.
                engine.Update(2400);
                Assert.IsTrue(engine.ProcessKey(engine == free ? '7' : 'x', 2400));
                Assert.AreEqual(CellState.Correct, engine.Lines[0].Cells[1].State);
                Assert.AreEqual(0, engine.ConsecutiveWrongKeys);
            }

            Assert.AreEqual(control.Score, free.Score);
            Assert.AreEqual(control.MaxCombo, free.MaxCombo);
            Assert.AreEqual(control.LiveAccuracy, free.LiveAccuracy);
        }

        [Test]
        public void AllowWrongInputStillRejectsSpaceOnAFreestyleCell()
        {
            // The allow-wrong-input path types a wrong LETTER through as a red, backspaceable cell,
            // but has always refused to do that with a space. A freestyle cell inherits that: the
            // only outcome a space has there is the strict rejection.
            var engine = activeEngine();
            engine.AllowWrongInput = true;

            engine.ProcessKey('a', 1000);
            engine.Update(2000);
            Assert.IsTrue(engine.ProcessKey(' ', 2000));

            var cell = engine.Lines[0].Cells[1];
            Assert.AreEqual(CellState.Untyped, cell.State); // NOT CellState.Wrong
            Assert.IsNull(cell.TypedChar);
            Assert.AreEqual(1, engine.CaretIndex);
            Assert.AreEqual(1, engine.ConsecutiveWrongKeys); // the strict path feeds the mash streak
        }

        [Test]
        public void MashingSubstitutesTheAutoCharForASpaceOnAFreestyleCell()
        {
            // Mashing promises any key is the right key on every cell; on an ordinary cell it keeps
            // that promise by rewriting the press to the expected char. A freestyle cell is exempt
            // from the rewrite (it must remember the pressed char), so space is the one press that
            // would otherwise be rejected under the mod: it is substituted with the char autoplay
            // uses, which keeps both the mod's promise and the "no space in a freestyle slot" rule.
            var engine = activeEngine();
            engine.MashingEnabled = true;

            engine.ProcessKey('a', 1000);
            engine.Update(2000);
            Assert.IsTrue(engine.ProcessKey(' ', 2000));

            var cell = engine.Lines[0].Cells[1];
            Assert.AreEqual(CellState.Correct, cell.State);
            Assert.AreEqual(Typeability.FREESTYLE_AUTO_CHAR, cell.TypedChar);
            Assert.AreNotEqual(' ', cell.TypedChar);
            Assert.AreNotEqual(marker, cell.TypedChar);
            Assert.AreEqual(2, engine.Combo);
            Assert.AreEqual(1.0, engine.LiveAccuracy);

            // Control: mashing on an ORDINARY cell still takes a space, judged as its expected char.
            var control = new TypingEngine(map(line("axb", 1000, 5000, 4000, unit("axb", 1000, 4000))));
            control.MashingEnabled = true;
            control.Update(1000);
            Assert.IsTrue(control.ProcessKey(' ', 1000));
            Assert.AreEqual(CellState.Correct, control.Lines[0].Cells[0].State);
            Assert.AreEqual('a', control.Lines[0].Cells[0].TypedChar);
        }

        [Test]
        public void FreestyleCellScoresExactlyLikeAnOrdinaryCell()
        {
            // Same map shape, letters only, played identically: score/combo/counts must match.
            var plain = new TypingEngine(map(line("axb", 1000, 5000, 4000, unit("axb", 1000, 4000))));
            var free = new TypingEngine(freestyleMap());

            foreach ((var engine, char middle) in new[] { (plain, 'x'), (free, 'q') })
            {
                engine.Update(1000);
                engine.ProcessKey('a', 1000);
                engine.Update(2000);
                engine.ProcessKey(middle, 2000);
                engine.Update(3000);
                engine.ProcessKey('b', 3000);
                engine.Update(9000);
            }

            var expected = plain.BuildResults();
            var actual = free.BuildResults();

            Assert.AreEqual(expected.Score, actual.Score);
            Assert.AreEqual(expected.MaxCombo, actual.MaxCombo);
            Assert.AreEqual(expected.Accuracy, actual.Accuracy);
            Assert.AreEqual(expected.SyncPercent, actual.SyncPercent);
            Assert.AreEqual(expected.Wpm, actual.Wpm);
            Assert.AreEqual(expected.Counts[JudgementType.Perfect], actual.Counts[JudgementType.Perfect]);
        }

        [Test]
        public void LiterateModDoesNotConstrainAFreestyleCell()
        {
            var engine = activeEngine();
            engine.CaseSensitive = true;

            // A capital would be judged wrong on a normal lower-case target under Literate; the
            // freestyle slot accepts it regardless of case.
            Assert.IsTrue(engine.ProcessKey('a', 1000));
            engine.Update(2000);
            Assert.IsTrue(engine.ProcessKey('Q', 2000));

            Assert.AreEqual(CellState.Correct, engine.Lines[0].Cells[1].State);
            Assert.AreEqual('Q', engine.Lines[0].Cells[1].TypedChar);
            Assert.AreEqual(1.0, engine.LiveAccuracy);
        }

        [Test]
        public void MashingModLeavesTheTypedCharIntact()
        {
            var engine = activeEngine();
            engine.MashingEnabled = true;

            engine.ProcessKey('a', 1000);
            engine.Update(2000);
            engine.ProcessKey('q', 2000);

            // Mashing rewrites the pressed char to the cell's expected one; on a freestyle cell that
            // would stamp the authoring marker over the player's char, so it must not apply there.
            Assert.AreEqual('q', engine.Lines[0].Cells[1].TypedChar);
            Assert.AreNotEqual(marker, engine.Lines[0].Cells[1].TypedChar);
        }

        [Test]
        public void AllowWrongInputHasNoEffectOnAFreestyleCell()
        {
            var engine = activeEngine();
            engine.AllowWrongInput = true;

            int wrongJudgements = 0;
            engine.CharJudged += j =>
            {
                if (j.Type == JudgementType.WrongChar)
                    wrongJudgements++;
            };

            engine.ProcessKey('a', 1000);
            engine.Update(2000);
            engine.ProcessKey('q', 2000);

            // No typed-through-as-wrong path: the cell is Correct, not Wrong, and combo survives.
            Assert.AreEqual(CellState.Correct, engine.Lines[0].Cells[1].State);
            Assert.AreEqual(0, wrongJudgements);
            Assert.AreEqual(2, engine.Combo);
        }

        [Test]
        public void BackspaceReopensAFreestyleCellAndANewCharLands()
        {
            var engine = activeEngine();

            engine.ProcessKey('a', 1000);
            engine.Update(2000);
            engine.ProcessKey('q', 2000);

            long scoreAfterFirst = engine.Score;

            Assert.IsTrue(engine.ProcessBackspace());
            Assert.AreEqual(CellState.Untyped, engine.Lines[0].Cells[1].State);
            Assert.IsNull(engine.Lines[0].Cells[1].TypedChar); // shimmer resumes on screen
            Assert.AreEqual(1, engine.CaretIndex);

            engine.Update(2400);
            Assert.IsTrue(engine.ProcessKey('7', 2400));

            Assert.AreEqual('7', engine.Lines[0].Cells[1].TypedChar);
            // Retyping a once-correct cell is scoring-inert (the first judgement stands), exactly as
            // for a normal cell; the only visible change is the displayed char.
            Assert.AreEqual(scoreAfterFirst, engine.Score);
            Assert.AreEqual(0, engine.Lines[0].Cells[1].JudgedDelta!.Value);
        }

        [Test]
        public void UntypedFreestyleCellSealsAsAMiss()
        {
            var engine = activeEngine();

            engine.ProcessKey('a', 1000);
            engine.Update(9000); // past EndTime: seal

            Assert.AreEqual(CellState.Missed, engine.Lines[0].Cells[1].State);
            Assert.AreEqual(2, engine.BuildResults().Counts[JudgementType.Miss]); // the slot and 'b'
        }

        #endregion

        #region Replays

        [Test]
        public void RecordedFreestyleKeypressesReplayDeterministically()
        {
            // Record the effective inputs of a live-ish run, then feed them into a fresh engine the
            // way the playfield's replay feeder does; both engines must end in the same state.
            var recorded = new List<(double Time, char Character)>();

            var live = activeEngine();

            foreach ((double time, char c) in new[] { (1000d, 'a'), (2000d, 'Q'), (3000d, 'b') })
            {
                live.Update(time);

                if (live.ProcessKey(c, time))
                    recorded.Add((time, c));
            }

            live.Update(9000);

            var playback = new TypingEngine(freestyleMap());

            foreach ((double time, char c) in recorded)
            {
                playback.Update(time);
                playback.ProcessKey(c, time);
            }

            playback.Update(9000);

            Assert.AreEqual(3, recorded.Count);
            Assert.AreEqual('Q', recorded[1].Character); // the ACTUAL char, not the marker
            Assert.AreEqual(live.Score, playback.Score);
            Assert.AreEqual(live.MaxCombo, playback.MaxCombo);
            Assert.AreEqual(live.LiveAccuracy, playback.LiveAccuracy);

            for (int i = 0; i < live.Lines[0].Cells.Count; i++)
            {
                Assert.AreEqual(live.Lines[0].Cells[i].State, playback.Lines[0].Cells[i].State, $"cell {i} state");
                Assert.AreEqual(live.Lines[0].Cells[i].TypedChar, playback.Lines[0].Cells[i].TypedChar, $"cell {i} char");
            }
        }

        [Test]
        public void AutoplayTypesAFreestyleCellWithARealLetter()
        {
            var beatmap = new TypeBeatBeatmap();
            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = 1000,
                LineIndex = 0,
                Line = freestyleMap().Lines[0],
                Granularity = TimingGranularity.Word,
            });

            var frames = new TypeBeatAutoGenerator(beatmap).Generate().Frames.Cast<TypeBeatReplayFrame>().ToList();

            Assert.AreEqual(3, frames.Count);
            Assert.AreEqual(Typeability.FREESTYLE_AUTO_CHAR, frames[1].Character);
            Assert.AreNotEqual(marker, frames[1].Character);

            var engine = new TypingEngine(freestyleMap());

            foreach (var frame in frames)
            {
                engine.Update(frame.Time);
                Assert.IsTrue(engine.ProcessKey(frame.Character, frame.Time));
            }

            engine.Update(9000);
            Assert.IsTrue(engine.Lines[0].Cells.All(c => c.State == CellState.Correct));
        }

        #endregion

        #region Storage

        private const string freestyle_json =
            "{\"version\":2,\"song_end_ms\":20000,\"lines\":[" +
            "{\"text\":\"me & you\",\"start_ms\":1000,\"end_ms\":4000,\"freestyle\":true," +
            "\"words\":[{\"text\":\"me\",\"start_ms\":1000,\"end_ms\":2000},{\"text\":\"&\",\"start_ms\":2000,\"end_ms\":3000},{\"text\":\"you\",\"start_ms\":3000,\"end_ms\":4000}]}]}";

        private const string legacy_ampersand_json =
            "{\"version\":2,\"song_end_ms\":20000,\"lines\":[" +
            "{\"text\":\"me & you\",\"start_ms\":1000,\"end_ms\":4000}]}";

        private static typebeat.Game.Beatmaps.Beatmap decode(string osuText)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(osuText));
            using var reader = new LineBufferedReader(stream);
            return typebeat.Game.Beatmaps.Formats.Decoder.GetDecoder<typebeat.Game.Beatmaps.Beatmap>(reader).Decode(reader);
        }

        [Test]
        public void AmpersandsInAnUnflaggedLineStayLyricPunctuation()
        {
            // Back-compat pin: an aligner-authored line whose lyrics genuinely contain "&" must
            // decode exactly as it always did, marker stripped, no freestyle cells.
            string osu = LyricOsuFormat.GenerateOsu("A", "B", "audio.mp3", "t", legacy_ampersand_json);
            var hitObject = decode(osu).HitObjects.OfType<TypeBeatHitObject>().Single();

            Assert.AreEqual("me you", hitObject.Line.RawText);

            var typingLine = TypingLine.FromLyricLine(hitObject.Line, TimingGranularity.Word);
            Assert.IsTrue(typingLine.Cells.All(c => !c.IsFreestyle));
        }

        [Test]
        public void AmpersandsInAFlaggedLineDecodeAsFreestyleCells()
        {
            string osu = LyricOsuFormat.GenerateOsu("A", "B", "audio.mp3", "t", freestyle_json);
            var hitObject = decode(osu).HitObjects.OfType<TypeBeatHitObject>().Single();

            Assert.AreEqual("me & you", hitObject.Line.RawText);

            var typingLine = TypingLine.FromLyricLine(hitObject.Line, TimingGranularity.Word);
            var freestyleCells = typingLine.Cells.Where(c => c.IsFreestyle).ToList();

            Assert.AreEqual(1, freestyleCells.Count);
            Assert.AreEqual(marker, freestyleCells[0].Expected);
            Assert.AreEqual(8, typingLine.TypeableCount); // m e _ & _ y o u: 6 letters, the slot, 2 spaces
        }

        [Test]
        public void EditorTextEntryConvertsAmpersandsAndSurvivesSave()
        {
            var beatmap = new typebeat.Game.Beatmaps.Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.Metadata.Artist = "Op";
            beatmap.Metadata.Title = "Test";
            beatmap.Metadata.AudioFile = "audio.mp3";
            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = 1000,
                LineIndex = 0,
                Line = line("hello world", 1000, 5000, 4000, unit("hello", 1000, 2500), unit("world", 2500, 4000)),
                Granularity = TimingGranularity.Word,
            });

            var editorBeatmap = new EditorBeatmap(beatmap);
            var hitObject = TypeBeatEditorOperations.OrderedLines(editorBeatmap)[0];

            // The authoring gesture: type '&' into the line's text box. Supported punctuation is
            // kept in the stored line now, and derived away from the default typed stream.
            Assert.IsTrue(TypeBeatEditorOperations.SetLineText(editorBeatmap, hitObject, "he&&o, wor&d!"));
            Assert.AreEqual("he&&o, wor&d!", hitObject.Line.RawText);

            var authored = TypingLine.FromLyricLine(hitObject.Line, TimingGranularity.Word);
            Assert.AreEqual("he&&o wor&d", authored.DisplayText);
            Assert.AreEqual(3, authored.Cells.Count(c => c.IsFreestyle));

            // Save (encode) and reload (decode): the slots survive, and so does everything else.
            var sb = new StringBuilder();
            using (var writer = new StringWriter(sb))
                TypeBeatBeatmapEncoder.Encode(editorBeatmap, writer);

            Assert.IsTrue(sb.ToString().Contains("\"freestyle\":true"), "the opt-in flag must be persisted");

            var reloaded = decode(sb.ToString()).HitObjects.OfType<TypeBeatHitObject>().Single();
            Assert.AreEqual("he&&o, wor&d!", reloaded.Line.RawText);

            var reloadedLine = TypingLine.FromLyricLine(reloaded.Line, TimingGranularity.Word);
            Assert.AreEqual(3, reloadedLine.Cells.Count(c => c.IsFreestyle));

            for (int i = 0; i < authored.Cells.Count; i++)
            {
                Assert.AreEqual(authored.Cells[i].Expected, reloadedLine.Cells[i].Expected, $"cell {i}");
                Assert.AreEqual(authored.Cells[i].IsFreestyle, reloadedLine.Cells[i].IsFreestyle, $"cell {i}");
            }
        }

        [Test]
        public void MarkerFreeLinesDoNotGainTheFlag()
        {
            var beatmap = new typebeat.Game.Beatmaps.Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.Metadata.AudioFile = "audio.mp3";
            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = 1000,
                LineIndex = 0,
                Line = line("hello world", 1000, 5000, 4000, unit("hello", 1000, 2500), unit("world", 2500, 4000)),
                Granularity = TimingGranularity.Word,
            });

            var sb = new StringBuilder();
            using (var writer = new StringWriter(sb))
                TypeBeatBeatmapEncoder.Encode(new EditorBeatmap(beatmap), writer);

            Assert.IsFalse(sb.ToString().Contains("freestyle"), "ordinary lines must encode byte-identically to before");
        }

        #endregion

        #region Shimmer

        [Test]
        public void PoolGroupsCandidatesByAdvanceWidth()
        {
            // Two width classes: 'i' and 'l' narrow, everything else wide. The larger group wins, so
            // a substitution can never change the cell's advance.
            var pool = FreestyleGlyphs.BuildPool(c => c == 'i' || c == 'l' ? 4f : 10f);

            Assert.IsTrue(pool.Length > 2);
            Assert.IsFalse(pool.Contains('i'));
            Assert.IsFalse(pool.Contains('l'));
            Assert.IsTrue(pool.Contains('m'));
            Assert.IsTrue(pool.All(c => FreestyleGlyphs.CANDIDATES.Contains(c)));
        }

        [Test]
        public void PoolFallsBackToEveryCandidateWhenNothingMeasures()
        {
            var pool = FreestyleGlyphs.BuildPool(_ => null);
            Assert.AreEqual(FreestyleGlyphs.CANDIDATES.Length, pool.Length);
        }

        [Test]
        public void ShimmerGlyphIsDeterministicInPoolAndVariesOverTime()
        {
            char[] pool = FreestyleGlyphs.CANDIDATES.ToCharArray();

            Assert.AreEqual(FreestyleGlyphs.Glyph(pool, 17, 3), FreestyleGlyphs.Glyph(pool, 17, 3));
            Assert.IsTrue(pool.Contains(FreestyleGlyphs.Glyph(pool, 17, 3)));

            // Over a run of ticks the glyph must actually move around (that is the whole effect),
            // and never fall back to the authoring marker.
            var seen = new HashSet<char>();

            for (int tick = 0; tick < 40; tick++)
            {
                char g = FreestyleGlyphs.Glyph(pool, tick, 0);
                Assert.AreNotEqual(marker, g);
                seen.Add(g);
            }

            Assert.Greater(seen.Count, 5);

            // Neighbouring slots do not shimmer in lockstep.
            Assert.AreNotEqual(FreestyleGlyphs.Glyph(pool, 5, 0), FreestyleGlyphs.Glyph(pool, 5, 1));
        }

        [Test]
        public void TickAdvancesWithTheClock()
        {
            Assert.AreEqual(FreestyleGlyphs.TickFor(0), FreestyleGlyphs.TickFor(FreestyleGlyphs.SHIMMER_INTERVAL_MS - 1));
            Assert.AreNotEqual(FreestyleGlyphs.TickFor(0), FreestyleGlyphs.TickFor(FreestyleGlyphs.SHIMMER_INTERVAL_MS + 1));
        }

        [Test]
        public void SubstituteReplacesOnlyMarkers()
        {
            char[] pool = { 'x' };

            Assert.AreEqual("hexxo", FreestyleGlyphs.Substitute("he" + marker + marker + "o", pool, tick: 3));

            const string plain = "hello";
            Assert.AreSame(plain, FreestyleGlyphs.Substitute(plain, pool, tick: 3), "marker-free text must not allocate");
        }

        #endregion
    }
}
