// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Backlog 331: the Polyglot mod. The lyric is typed in its ORIGINAL script, local only. These pins
// cover the flattening (which text each word plays, the reverse-unit carry of the romanised syllable
// cuts, the fallbacks), the hangul keys and their recomposition, the NFC matching the engine and the
// text-input path share, the replay round trip, availability and the local-only gate, and the
// promise that a play WITHOUT the mod is untouched by any of it.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.ControlPoints;
using typebeat.Game.Beatmaps.Formats;
using typebeat.Game.Graphics.Fonts;
using typebeat.Game.Replays;
using typebeat.Game.Replays.Legacy;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Scoring;
using typebeat.Game.Screens.Play;
using typebeat.Game.Tests.Beatmaps;
using typebeat.Game.Utils;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class PolyglotTest
    {
        #region Fixture

        private static TimedUnit unit(string text, double start, double end, string? original = null, double[]? boundaries = null, int[]? splits = null)
            => new TimedUnit
            {
                Text = text,
                StartTime = start,
                EndTime = end,
                Original = original,
                SyllableBoundaries = boundaries ?? Array.Empty<double>(),
                SyllableSplits = splits ?? Array.Empty<int>(),
            };

        private static LyricLine line(params TimedUnit[] units) => new LyricLine
        {
            RawText = string.Join(' ', units.Select(u => u.Text)),
            StartTime = 0,
            EndTime = 6000,
            SingEndTime = units.Length > 0 ? units[^1].EndTime : 0,
            Units = units,
        };

        private static string display(TypingLine l) => l.DisplayText;

        private static TypingLine polyglot(LyricLine l, bool literate = false, string? language = null)
            => TypingLine.ForMods(l, literate, true, language);

        /// <summary>"Privet mir" over two words, both recording a Cyrillic original.</summary>
        private static LyricLine privetMir(bool withOriginals = true) => line(
            unit("Privet", 1000, 2000, withOriginals ? "Привет" : null),
            unit("mir", 2500, 3000, withOriginals ? "мир" : null));

        private static TypeBeatBeatmap beatmapOf(bool polyglotMod, params LyricLine[] lines)
        {
            var map = new TypeBeatBeatmap();

            for (int i = 0; i < lines.Length; i++)
                map.HitObjects.Add(new TypeBeatHitObject { StartTime = lines[i].StartTime, LineIndex = i, Line = lines[i], Granularity = TimingGranularity.Word });

            if (polyglotMod)
                new TypeBeatModPolyglot().ApplyToBeatmap(map);

            foreach (var hitObject in map.HitObjects)
                hitObject.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty(), CancellationToken.None);

            return map;
        }

        private static LyricBeatmap lyricMap(params LyricLine[] lines) => new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata { Artist = "A", Title = "T", FolderPath = string.Empty, AudioFileName = "a.mp3" },
            Lines = lines,
            Granularity = TimingGranularity.Word,
        };

        private static TypingEngine polyglotEngine(LyricLine l, bool literate = false, string? language = null)
        {
            var engine = new TypingEngine(lyricMap(l), literate, polyglot: true, polyglotLanguage: language) { AllowWrongInput = true };
            engine.Update(1000);
            return engine;
        }

        #endregion

        #region Flattening

        [Test]
        public void WordsWithOriginalsPlayTheOriginalAtTheWordsOwnTimes()
        {
            var cells = polyglot(privetMir(), language: "russian");

            Assert.Multiple(() =>
            {
                Assert.That(display(cells), Is.EqualTo("привет мир"), "the original, folded to lower case like the default stream");
                Assert.That(cells.Cells.All(c => c.IsTypeable), Is.True);
                Assert.That(cells.Words.Select(w => (w.StartTime, w.EndTime)), Is.EqualTo(new[] { (1000.0, 2000.0), (2500.0, 3000.0) }), "word times unchanged");
                Assert.That(cells.Cells[0].TargetTime, Is.EqualTo(1000));
                Assert.That(cells.Cells[5].TargetTime, Is.EqualTo(1000 + 5 * 1000.0 / 6).Within(1e-9), "six cells re-spread over the word, not the romanised five");
                Assert.That(cells.Cells[6].TargetTime, Is.EqualTo(2000), "the gap keeps the word's end");
                Assert.That(cells.Cells[7].TargetTime, Is.EqualTo(2500));
            });
        }

        [Test]
        public void ALineWithNoOriginalFlattensByteForByteAsWithoutTheMod()
        {
            var ascii = privetMir(withOriginals: false);
            var off = TypingLine.FromLyricLine(ascii);
            var on = polyglot(ascii);

            Assert.Multiple(() =>
            {
                Assert.That(on.Source, Is.SameAs(ascii));
                Assert.That(on.Cells.Select(c => (c.Expected, c.IsTypeable, c.TargetTime)), Is.EqualTo(off.Cells.Select(c => (c.Expected, c.IsTypeable, c.TargetTime))));
                Assert.That(on.Syllables, Is.EqualTo(off.Syllables));
                Assert.That(on.Words, Is.EqualTo(off.Words));
            });
        }

        [Test]
        public void AMixedLinePlaysEachWordInWhatItHas()
        {
            var mixed = line(unit("Privet", 1000, 2000, "Привет"), unit("world", 2500, 3000));

            Assert.That(display(polyglot(mixed, language: "russian")), Is.EqualTo("привет world"));
        }

        [Test]
        public void AnUnromanisedWordPlaysItsOriginalInItsOwnSlot()
        {
            var l = new LyricLine
            {
                RawText = "hello",
                StartTime = 0,
                EndTime = 6000,
                SingEndTime = 3000,
                Units = new[] { unit("hello", 1000, 2000) },
                UnromanisedWords = new[] { new UnromanisedWord(1, "愛", 2500, 3000) },
            };

            var cells = polyglot(l);

            Assert.Multiple(() =>
            {
                Assert.That(display(cells), Is.EqualTo("hello 愛"));
                Assert.That(cells.Cells[^1].TargetTime, Is.EqualTo(2500), "at the unromanised word's own start");
                Assert.That(TypingLine.FromLyricLine(l).DisplayText, Is.EqualTo("hello"), "without the mod it has no cell");
            });
        }

        [Test]
        public void LiterateTypesTheOriginalsOwnCaseAndPunctuation()
        {
            var l = line(unit("Privet,", 1000, 2000, "Привет,"));

            Assert.Multiple(() =>
            {
                Assert.That(display(polyglot(l, literate: true, language: "russian")), Is.EqualTo("Привет,"));
                Assert.That(polyglot(l, literate: true, language: "russian").Cells[^1].IsTypeable, Is.True, "the comma is a Literate cell");
                Assert.That(display(polyglot(l, language: "russian")), Is.EqualTo("привет"), "the default stream drops it");
                Assert.That(display(polyglot(line(unit("Kon'nichiwa", 1000, 2000, "こんにちは、")), literate: true)), Is.EqualTo("こんにちは、"),
                    "a CJK mark is punctuation of its own script");
            });
        }

        #endregion

        #region Reverse-unit carry of syllable cuts

        /// <summary>
        /// こんにちは romanised "konnichiha" by Hepburn units こ ko, ん n, に ni, ち chi, は ha. The
        /// romanised split "kon|ni|chi|ha" is [3, 5, 8]; each cut is the text start of a unit (に at
        /// 3, ち at 5, は at 8), so it carries back to that unit's source start: [2, 3, 4].
        /// Cross-check of the targets on [1000, 3000] with boundaries 1400 / 1800 / 2400: the first
        /// segment's two cells at 1000 and 1200, then one cell on each boundary.
        /// </summary>
        [Test]
        public void RomanisedCutsCarryBackThroughTheRomanisersUnits()
        {
            var l = line(unit("konnichiha", 1000, 3000, "こんにちは", new[] { 1400.0, 1800, 2400 }, new[] { 3, 5, 8 }));
            var cells = polyglot(l, language: "japanese");

            Assert.Multiple(() =>
            {
                Assert.That(cells.Source.Units[0].SyllableSplits, Is.EqualTo(new[] { 2, 3, 4 }));
                Assert.That(cells.Cells.Select(c => c.TargetTime), Is.EqualTo(new[] { 1000.0, 1200, 1400, 1800, 2400 }));
                Assert.That(cells.Syllables.Select(s => (s.StartCell, s.EndCellExclusive, s.StartTime, s.EndTime)),
                    Is.EqualTo(new[] { (0, 2, 1000.0, 1400.0), (2, 3, 1400.0, 1800.0), (3, 4, 1800.0, 2400.0), (4, 5, 2400.0, 3000.0) }));
            });
        }

        [Test]
        public void ACyrillicCutLandsOnTheLetterItOpens()
        {
            var l = line(unit("Privet", 1000, 2000, "Привет", new[] { 1500.0 }, new[] { 3 }));

            Assert.That(polyglot(l, language: "russian").Source.Units[0].SyllableSplits, Is.EqualTo(new[] { 3 }), "Pri|vet is При|вет");
        }

        [Test]
        public void ARespelledWordFallsBackToEvenCutsOverItsGlyphs()
        {
            // The mapper typed "Privyet": the romaniser's own spelling no longer matches, so no cut can
            // be carried. Six glyphs over two segments cut at glyph 3.
            var l = line(unit("Privyet", 1000, 2000, "Привет", new[] { 1500.0 }, new[] { 4 }));

            Assert.That(polyglot(l, language: "russian").Source.Units[0].SyllableSplits, Is.EqualTo(new[] { 3 }));
        }

        [Test]
        public void TheReverseMappingRefusesACutInsideOneUnit()
        {
            var word = LyricOriginals.RomaniseWord("きゃく", "japanese");

            Assert.Multiple(() =>
            {
                Assert.That(word.Text, Is.EqualTo("kyaku"));
                Assert.That(word.SourceIndexOfTextSplit(3), Is.EqualTo(2), "kya|ku: the cut opens く");
                Assert.That(word.SourceIndexOfTextSplit(1), Is.Null, "k|yaku falls inside the one mora きゃ");
            });
        }

        [Test]
        public void ASubdividedWordWithTooFewGlyphsKeepsItsSpanAndLosesTheSubdivision()
        {
            // One kanji for a two-syllable romanisation: no cut can be carried and none can be spread.
            var l = line(unit("ai", 1000, 2000, "愛", new[] { 1500.0 }, new[] { 1 }));
            var cells = polyglot(l);

            Assert.Multiple(() =>
            {
                Assert.That(cells.Source.Units[0].SyllableBoundaries, Is.Empty);
                Assert.That(cells.Cells.Single().TargetTime, Is.EqualTo(1000));
                Assert.That(cells.Words.Single().EndTime, Is.EqualTo(2000));
            });
        }

        [Test]
        public void ALineOriginalThatTokenisesPlaysWordForWord()
        {
            var l = new LyricLine
            {
                RawText = "konnichiha sekai",
                StartTime = 0,
                EndTime = 6000,
                SingEndTime = 3000,
                Units = new[] { unit("konnichiha", 1000, 2000), unit("sekai", 2500, 3000) },
                Original = "こんにちは 世界",
            };

            var cells = polyglot(l);

            Assert.Multiple(() =>
            {
                Assert.That(display(cells), Is.EqualTo("こんにちは 世界"));
                Assert.That(cells.Words.Select(w => (w.StartTime, w.EndTime)), Is.EqualTo(new[] { (1000.0, 2000.0), (2500.0, 3000.0) }));
            });
        }

        [Test]
        public void ALineOriginalThatDoesNotTokeniseIsSpreadByCharacter()
        {
            // Three romanised words, one original run: the seven characters share [1000, 3000].
            var l = new LyricLine
            {
                RawText = "kon nichi ha",
                StartTime = 0,
                EndTime = 6000,
                SingEndTime = 3000,
                Units = new[] { unit("kon", 1000, 1500), unit("nichi", 1500, 2500), unit("ha", 2500, 3000) },
                Original = "こんにちは世界",
            };

            var cells = polyglot(l);

            Assert.Multiple(() =>
            {
                Assert.That(display(cells), Is.EqualTo("こんにちは世界"));
                Assert.That(cells.Cells.Select(c => c.TargetTime), Is.EqualTo(Enumerable.Range(0, 7).Select(j => 1000 + j * 2000.0 / 7)).Within(1e-9));
            });
        }

        #endregion

        #region Hangul

        [Test]
        public void AHangulBlockIsCutIntoTheKeysThatTypeIt()
        {
            Assert.Multiple(() =>
            {
                Assert.That(PolyglotText.HangulKeys('안'), Is.EqualTo("ㅇㅏㄴ"));
                Assert.That(PolyglotText.HangulKeys('와'), Is.EqualTo("ㅇㅗㅏ"), "a compound vowel is two keys");
                Assert.That(PolyglotText.HangulKeys('닭'), Is.EqualTo("ㄷㅏㄹㄱ"), "a double final is two keys");
                Assert.That(PolyglotText.HangulKeys('까'), Is.EqualTo("ㄲㅏ"), "a tense initial is one (shifted) key");
            });
        }

        [Test]
        public void TheKeysRecomposeAsTyped()
        {
            Assert.Multiple(() =>
            {
                Assert.That(PolyglotText.Compose("ㅇ".ToList()), Is.EqualTo("ㅇ"));
                Assert.That(PolyglotText.Compose("ㅇㅏ".ToList()), Is.EqualTo("아"));
                Assert.That(PolyglotText.Compose("ㅇㅏㄴ".ToList()), Is.EqualTo("안"));
                Assert.That(PolyglotText.Compose("ㅇㅗㅏ".ToList()), Is.EqualTo("와"));
                Assert.That(PolyglotText.Compose("ㄷㅏㄹㄱ".ToList()), Is.EqualTo("닭"));
                Assert.That(PolyglotText.Compose("ㅏㅇ".ToList()), Is.EqualTo("ㅏㅇ"), "no initial: shown as typed, never an invented block");
            });
        }

        [Test]
        public void HangulPlaysAsKeysAndDisplaysAsBlocks()
        {
            var l = line(unit("annyeong", 1000, 2000, "안녕"));
            var cells = polyglot(l, language: "korean");

            Assert.Multiple(() =>
            {
                Assert.That(display(cells), Is.EqualTo("ㅇㅏㄴㄴㅕㅇ"));
                Assert.That(Enumerable.Range(0, 6).Select(cells.JamoBlockHead), Is.EqualTo(new[] { 0, 0, 0, 3, 3, 3 }));
                Assert.That(Enumerable.Range(0, 6).Select(i => LyricLineDisplay.InitialGlyph(cells, i)), Is.EqualTo(new[] { "안", "", "", "녕", "", "" }));
                Assert.That(TypingLine.FromLyricLine(l).JamoBlockHead(0), Is.EqualTo(-1), "no block without the mod");
            });

            var engine = polyglotEngine(l, language: "korean");
            engine.ProcessKey('ㅇ', 1000);
            engine.ProcessKey('ㅏ', 1100);

            Assert.That(LyricLineDisplay.BlockGlyph(engine.Lines[0], 0), Is.EqualTo("아"), "the block shows what has been typed into it");
        }

        [Test]
        public void AComposedCommitIsFedAsItsKeys()
        {
            var l = line(unit("annyeong", 1000, 2000, "안녕"));
            var engine = polyglotEngine(l, language: "korean");

            // One IME commit per block, each at one timestamp.
            foreach (char c in PolyglotText.InputCharacters("안"))
                engine.ProcessKey(c, 1000);

            foreach (char c in PolyglotText.InputCharacters("녕"))
                engine.ProcessKey(c, 1500);

            Assert.That(engine.Lines[0].Cells.All(c => c.State == CellState.Correct), Is.True);
        }

        #endregion

        #region Matching

        [Test]
        public void ADeadKeyCommitArrivesAsTheOneComposedCharacter()
        {
            Assert.That(PolyglotText.InputCharacters("é"), Is.EqualTo(new[] { 'é' }));

            var l = line(unit("cafe", 1000, 2000, "café"));
            var engine = polyglotEngine(l);

            foreach (char c in PolyglotText.InputCharacters("café"))
                engine.ProcessKey(c, 1000);

            Assert.That(engine.Lines[0].Cells.Select(c => c.State), Is.All.EqualTo(CellState.Correct));
        }

        [Test]
        public void APressIsMatchedInNfc()
        {
            // U+F900 is a CJK compatibility ideograph whose NFC is U+8C48: the lyric holds the
            // canonical one (an original is normalised before it is cut), an input method may type
            // the compatibility one.
            var l = line(unit("gai", 1000, 2000, "豈"));
            var engine = polyglotEngine(l);

            engine.ProcessKey('豈', 1000);

            Assert.That(engine.Lines[0].Cells[0].State, Is.EqualTo(CellState.Correct));
        }

        [Test]
        public void CaseFoldsUnlessLiterateIsOn()
        {
            var l = line(unit("Privet", 1000, 2000, "Привет"));

            var folded = polyglotEngine(l, language: "russian");
            folded.ProcessKey('П', 1000);

            var literate = polyglotEngine(l, literate: true, language: "russian");
            literate.ProcessKey('п', 1000);

            Assert.Multiple(() =>
            {
                Assert.That(folded.Lines[0].Cells[0].State, Is.EqualTo(CellState.Correct));
                Assert.That(literate.Lines[0].Cells[0].State, Is.EqualTo(CellState.Wrong));
            });
        }

        [Test]
        public void TheTextPathDropsWhatAFrameCannotHold()
        {
            Assert.Multiple(() =>
            {
                Assert.That(PolyglotText.InputCharacters("a\U0001F600b\u0007"), Is.EqualTo(new[] { 'a', 'b' }), "outside the BMP and controls are dropped");
                Assert.That(Typeability.IsPolyglotCell('\uD83D'), Is.False, "a surrogate half is never a cell");
                Assert.That(Typeability.IsTypeable('п'), Is.False, "the ASCII invariant every legacy path relies on stands");
                Assert.That(Typeability.IsPolyglotCell('п') && Typeability.IsPolyglotCell('́') && Typeability.IsPolyglotCell('٣'), Is.True);
            });
        }

        [Test]
        public void AScriptTheLyricFontLacksGetsAFaceThatDrawsIt()
        {
            // Georgian: the first candidate that says it draws the letter is the one registered.
            var candidates = new[] { "Segoe UI", "Sylfaen", "Arial" };

            Assert.Multiple(() =>
            {
                Assert.That(LyricFontManager.ChooseCoverageFamily('ა', candidates, (family, c) => family != "Segoe UI"), Is.EqualTo("Sylfaen"));
                Assert.That(LyricFontManager.ChooseCoverageFamily('ა', candidates, (_, _) => false), Is.Null, "none: the framework's fallback glyph, never a blank");
                Assert.That(LyricFontManager.PREFERRED_COVERAGE_FAMILIES, Does.Contain("Segoe UI").And.Contain("Nirmala UI").And.Contain("Malgun Gothic"));
            });
        }

        #endregion

        #region Replays and default play

        private static LyricLine cyrillicLine() => new LyricLine
        {
            RawText = "Privet mir",
            StartTime = 0,
            EndTime = 6000,
            SingEndTime = 3000,
            Units = new[] { unit("Privet", 1000, 2000, "Привет"), unit("mir", 2500, 3000, "мир") },
        };

        private static Replay perfectRun(IReadOnlyList<TypingCell> cells)
        {
            var r = new Replay();
            r.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, true));

            foreach (var cell in cells)
                r.Frames.Add(new TypeBeatReplayFrame(Math.Round(cell.TargetTime), cell.Expected));

            return r;
        }

        /// <summary>Every frame through the legacy .osr carrier: MouseX written as the encoder writes it and parsed as the decoder parses it.</summary>
        private static Replay throughLegacy(Replay replay, IBeatmap map)
        {
            var r = new Replay();

            foreach (var frame in replay.Frames.Cast<TypeBeatReplayFrame>())
            {
                var legacy = frame.ToLegacy(map);
                float mouseX = Parsing.ParseFloat(FormattableString.Invariant($"{legacy.MouseX ?? 0}"), Parsing.MAX_COORDINATE_VALUE);
                float mouseY = Parsing.ParseFloat(FormattableString.Invariant($"{legacy.MouseY ?? 0}"), Parsing.MAX_COORDINATE_VALUE);

                var decoded = new TypeBeatReplayFrame();
                decoded.FromLegacy(new LegacyReplayFrame(legacy.Time, mouseX, mouseY, legacy.ButtonState), map);
                decoded.Time = legacy.Time; // as LegacyScoreDecoder.convertFrame does
                r.Frames.Add(decoded);
            }

            return r;
        }

        [Test]
        public void ACyrillicPlayRoundTripsThroughTheReplayAndReScores()
        {
            var map = beatmapOf(true, cyrillicLine());
            var cells = TypingLine.ForMods(cyrillicLine(), false, true, TypeBeatModPolyglot.LanguageFor(map)).Cells;
            var run = perfectRun(cells);
            var mods = new Mod[] { new TypeBeatModPolyglot() };

            var live = TypeBeatReplayScorer.Score(map, mods, run, TypoRule.Deferred, ComboRestoreRule.OnFix);
            var decoded = throughLegacy(run, map);
            var stored = TypeBeatReplayScorer.Score(map, mods, decoded, TypoRule.Deferred, ComboRestoreRule.OnFix);

            Assert.Multiple(() =>
            {
                Assert.That(decoded.Frames.Cast<TypeBeatReplayFrame>().Select(f => f.Character), Is.EqualTo(run.Frames.Cast<TypeBeatReplayFrame>().Select(f => f.Character)),
                    "every Cyrillic character survives the float carrier exactly");
                Assert.That(live.Statistics.GetValueOrDefault(HitResult.Great), Is.EqualTo(10), "привет, the gap, мир");
                Assert.That(live.Statistics.GetValueOrDefault(HitResult.Miss), Is.Zero);
                Assert.That(live.Rank, Is.EqualTo(ScoreRank.X));
                Assert.That(stored.Statistics, Is.EqualTo(live.Statistics));
                Assert.That(stored.TotalScore, Is.EqualTo(live.TotalScore));
                Assert.That(stored.MaxCombo, Is.EqualTo(live.MaxCombo));
            });
        }

        [Test]
        public void TheModInTheScoreIsWhatMakesItAPolyglotRun()
        {
            // The same frames re-scored without the mod judge Cyrillic presses against romanised cells.
            var map = beatmapOf(false, cyrillicLine());
            var cells = TypingLine.ForMods(cyrillicLine(), false, true, "russian").Cells;
            var account = TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), perfectRun(cells), TypoRule.Deferred, ComboRestoreRule.OnFix);

            Assert.That(account.Rank, Is.Not.EqualTo(ScoreRank.X));
        }

        [Test]
        public void DefaultPlayOnAMapWithOriginalsIsByteIdenticalWithTheModOff()
        {
            var withOriginals = cyrillicLine();
            var without = new LyricLine
            {
                RawText = withOriginals.RawText,
                StartTime = withOriginals.StartTime,
                EndTime = withOriginals.EndTime,
                SingEndTime = withOriginals.SingEndTime,
                Units = withOriginals.Units.Select(u => unit(u.Text, u.StartTime, u.EndTime)).ToArray(),
            };

            var a = new TypingEngine(lyricMap(withOriginals)).Lines[0];
            var b = new TypingEngine(lyricMap(without)).Lines[0];

            var run = perfectRun(b.Cells);
            var scoredA = TypeBeatReplayScorer.Score(beatmapOf(false, withOriginals), Array.Empty<Mod>(), run, TypoRule.Deferred, ComboRestoreRule.OnFix);
            var scoredB = TypeBeatReplayScorer.Score(beatmapOf(false, without), Array.Empty<Mod>(), run, TypoRule.Deferred, ComboRestoreRule.OnFix);

            Assert.Multiple(() =>
            {
                Assert.That(a.Cells.Select(c => (c.Expected, c.IsTypeable, c.TargetTime)), Is.EqualTo(b.Cells.Select(c => (c.Expected, c.IsTypeable, c.TargetTime))));
                Assert.That(a.DisplayText, Is.EqualTo("privet mir"));
                Assert.That(scoredA.Statistics, Is.EqualTo(scoredB.Statistics));
                Assert.That(scoredA.TotalScore, Is.EqualTo(scoredB.TotalScore));
                Assert.That(scoredA.Rank, Is.EqualTo(ScoreRank.X));
            });
        }

        [Test]
        public void TheNestedObjectsFollowTheEnginesCells()
        {
            var map = beatmapOf(true, cyrillicLine());
            var nested = map.HitObjects.Cast<TypeBeatHitObject>().Single().NestedHitObjects.Cast<TypeBeatCharObject>().ToList();

            Assert.That(nested.Select(n => n.Expected), Is.EqualTo("привет мир".ToCharArray()));
        }

        #endregion

        #region The mod, availability and local-only

        [Test]
        public void TheModIsALocalOnlyUnrankedConversion()
        {
            var mod = new TypeBeatModPolyglot();
            var calculator = new TypeBeatScoreMultiplierCalculator(new ScoreMultiplierContext(new BeatmapDifficulty()));

            Assert.Multiple(() =>
            {
                Assert.That(mod.Acronym, Is.EqualTo("PG"));
                Assert.That(mod.Type, Is.EqualTo(ModType.Conversion));
                Assert.That(mod.Ranked, Is.False);
                Assert.That(mod.LocalOnly, Is.True);
                Assert.That(mod.Description.ToString(), Is.EqualTo("Type the lyric in its original script."));
                Assert.That(calculator.CalculateFor(new Mod[] { mod }), Is.EqualTo(1.0).Within(1e-9));
                Assert.That(new TypeBeatRuleset().AllMods.Count(m => m.Acronym == "PG"), Is.EqualTo(1), "the acronym is free");
                Assert.That(ModUtils.CheckCompatibleSet(new Mod[] { mod, new TypeBeatModLiterate(), new TypeBeatModDoubleTime() }), Is.True, "incompatible with nothing by rule");
            });
        }

        [Test]
        public void ALocalOnlyModRequestsNoToken()
        {
            Assert.Multiple(() =>
            {
                Assert.That(SubmittingPlayer.IsLocalOnly(new Mod[] { new TypeBeatModLiterate(), new TypeBeatModPolyglot() }), Is.True);
                Assert.That(SubmittingPlayer.IsLocalOnly(new Mod[] { new TypeBeatModLiterate() }), Is.False);
                Assert.That(SubmittingPlayer.IsLocalOnly(Array.Empty<Mod>()), Is.False);
            });
        }

        private static TestWorkingBeatmap working(bool withOriginals) => new TestWorkingBeatmap(beatmapOf(false, privetMir(withOriginals)));

        [Test]
        public void ThePolyglotModIsAvailableOnlyOnAMapWithOriginals()
        {
            var mod = new TypeBeatModPolyglot();

            Assert.Multiple(() =>
            {
                Assert.That(mod.IsAvailableFor(working(true)), Is.True);
                Assert.That(mod.IsAvailableFor(working(false)), Is.False);
                Assert.That(mod.IsAvailableFor(null), Is.True, "no beatmap known: nothing to refuse it on");
                Assert.That(new TypeBeatModLiterate().IsAvailableFor(working(false)), Is.True, "every other mod keeps the default");
                Assert.That(mod.UnavailableReason.ToString(), Is.EqualTo("This map has no original-script lyrics"));
            });
        }

        [Test]
        public void AnUnromanisedWordAloneMakesTheModAvailable()
        {
            var l = new LyricLine
            {
                RawText = "hello",
                StartTime = 0,
                EndTime = 6000,
                SingEndTime = 3000,
                Units = new[] { unit("hello", 1000, 2000) },
                UnromanisedWords = new[] { new UnromanisedWord(1, "愛", 2500, 3000) },
            };

            Assert.That(new TypeBeatModPolyglot().IsAvailableFor(new TestWorkingBeatmap(beatmapOf(false, l))), Is.True);
        }

        /// <summary>
        /// The one test every restoring path runs (OsuGame on a beatmap change, which is what a carried-over
        /// selection meets, and on a selection change, which is what applying a preset is): the mod the map
        /// cannot carry drops out and everything else stays, exactly as an incompatible mod drops.
        /// </summary>
        [Test]
        public void APresetOrCarriedOverSelectionDropsPolyglotOnAMapWithoutOriginals()
        {
            var preset = new Mod[] { new TypeBeatModPolyglot(), new TypeBeatModLiterate(), new TypeBeatModDoubleTime() };

            Assert.Multiple(() =>
            {
                Assert.That(ModUtils.CheckAvailableFor(preset, working(true), out var none), Is.True);
                Assert.That(none, Is.Null);

                Assert.That(ModUtils.CheckAvailableFor(preset, working(false), out var dropped), Is.False);
                Assert.That(dropped!.Single(), Is.InstanceOf<TypeBeatModPolyglot>());
                Assert.That(preset.Except(dropped!).Select(m => m.Acronym), Is.EqualTo(new[] { "LT", "DT" }));
            });
        }

        #endregion
    }
}
