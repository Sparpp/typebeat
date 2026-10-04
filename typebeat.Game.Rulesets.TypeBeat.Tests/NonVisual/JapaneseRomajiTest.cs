// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Replays;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class JapaneseRomajiTest
    {
        [TestCase("つき", "tuki")]
        [TestCase("つき", "tsuki")]
        [TestCase("てんし", "tensi")]
        [TestCase("てんし", "tenshi")]
        [TestCase("てんし", "tennsi")]
        [TestCase("てんし", "tennshi")]
        [TestCase("てんし", "ten'shi")]
        [TestCase("てんし", "texnshi")]
        [TestCase("こんにちは", "konnichiha")]
        [TestCase("こんにちは", "konnnichiha")]
        [TestCase("しゃ", "sha")]
        [TestCase("しゃ", "sya")]
        [TestCase("しゃ", "shixya")]
        [TestCase("しゃ", "silya")]
        [TestCase("ちゃ", "cha")]
        [TestCase("ちゃ", "tya")]
        [TestCase("ちゃ", "cya")]
        [TestCase("じゃ", "ja")]
        [TestCase("じゃ", "zya")]
        [TestCase("じゃ", "jya")]
        [TestCase("がっこう", "gakkou")]
        [TestCase("がっこう", "gaxtukou")]
        [TestCase("がっこう", "galtsukou")]
        [TestCase("まっちゃ", "maccha")]
        [TestCase("まっちゃ", "matcha")]
        [TestCase("まっちゃ", "mattya")]
        [TestCase("ふぁ", "fa")]
        [TestCase("ふぁ", "huxa")]
        [TestCase("てぃ", "thi")]
        [TestCase("てぃ", "texi")]
        [TestCase("んあ", "nna")]
        [TestCase("んあ", "n'a")]
        [TestCase("んや", "nnya")]
        [TestCase("ん", "nn")]
        [TestCase("ん", "xn")]
        [TestCase("こーひー", "ko-hi-")]
        [TestCase("を", "o")]
        [TestCase("を", "wo")]
        [TestCase("は", "wa")]
        [TestCase("は", "ha")]
        [TestCase("へ", "e")]
        [TestCase("へ", "he")]
        [TestCase("ほんを", "honwo")]
        [TestCase("ほんを", "honno")]
        [TestCase("ほんを", "hon'o")]
        [TestCase("ほんへ", "honhe")]
        [TestCase("ほんへ", "honne")]
        [TestCase("ほんへ", "hon'e")]
        public void AcceptsImeSpellingsIncrementally(string kana, string input)
        {
            for (int i = 1; i < input.Length; i++)
                Assert.That(JapaneseRomaji.Matches(kana, input[..i]), Is.Not.EqualTo(JapaneseRomaji.Match.Invalid), input[..i]);
            Assert.That(JapaneseRomaji.Matches(kana, input), Is.EqualTo(JapaneseRomaji.Match.Complete));
        }

        [TestCase("つき", "suki")]
        [TestCase("んあ", "na")]
        [TestCase("んや", "nya")]
        [TestCase("てんし", "tens")]
        [TestCase("しゃ", "sia")]
        [TestCase("まっちゃ", "makcha")]
        [TestCase("ほんを", "hono")]
        [TestCase("ほんへ", "hone")]
        public void DoesNotConvertIncompleteOrWrongReadings(string kana, string input)
            => Assert.That(JapaneseRomaji.Matches(kana, input) & JapaneseRomaji.Match.Complete, Is.EqualTo(JapaneseRomaji.Match.Invalid));

        [TestCase("月", "tuki")]
        [TestCase("月", "tsuki")]
        [TestCase("天使", "tensi")]
        [TestCase("天使", "tenshi")]
        [TestCase("天使", "tennsi")]
        [TestCase("天使", "tennshi")]
        [TestCase("つき", "tuki")]
        [TestCase("テンシ", "tennshi")]
        [TestCase("こんにちは", "konnichiha")]
        [TestCase("食べる", "taberu")]
        [TestCase("学校", "gakkou")]
        [TestCase("真っ赤", "makka")]
        [TestCase("月とカレー", "tukitokare-")]
        [TestCase("天 つき", "ten tsuki")]
        [TestCase("天 つき", "tenn tuki")]
        [TestCase("月 と テンシ", "tuki to tennshi")]
        [TestCase("月 と テンシ", "tsuki to tensi")]
        [TestCase("月　と　テンシ", "tsuki to tenshi")]
        [TestCase("コーヒー", "ko-hi-")]
        [TestCase("はをへ", "waoe")]
        [TestCase("はをへ", "hawohe")]
        [TestCase("本を", "honwo")]
        [TestCase("本を", "honno")]
        [TestCase("本を", "hon'o")]
        [TestCase("本へ", "honhe")]
        [TestCase("本へ", "honne")]
        [TestCase("本へ", "hon'e")]
        [TestCase("ｶﾞｯｺｳ", "gakkou")]
        [TestCase("ｷｬ", "kya")]
        [TestCase("本に", "honni")]
        [TestCase("本に", "honnni")]
        [TestCase("本に", "hon'ni")]
        public void TypesJapaneseWithoutCommittingAnIme(string original, string input)
        {
            var engine = create(original);
            foreach (char c in input)
                Assert.That(engine.ProcessKey(c, 1200), Is.True, c.ToString());
            Assert.Multiple(() =>
            {
                Assert.That(engine.Lines[0].Cells.All(c => c.State == CellState.Correct), Is.True);
                Assert.That(engine.Mistypes, Is.Zero);
                Assert.That(engine.JapanesePending, Is.Empty);
            });
        }

        [TestCase("月 と テンシ", "tuki to tennshi")]
        [TestCase("月　と　テンシ", "tsuki to tensi")]
        [TestCase("月 と ｷｬ", "tuki to kya")]
        public void KanaStillAcceptsRomajiInASpacedMixedScriptLine(string original, string input)
        {
            var words = LyricOriginals.CollapseWhitespace(original).Split(' ');
            var beatmap = new LyricBeatmap
            {
                Metadata = new LyricBeatmapMetadata { Artist = "A", Title = "T", AudioFileName = "a.mp3", FolderPath = string.Empty },
                Granularity = TimingGranularity.Word,
                Lines = new[]
                {
                    new LyricLine
                    {
                        RawText = string.Join(' ', words.Select(w => LyricOriginals.RomaniseWord(w, "japanese").Text)),
                        Original = original, StartTime = 0, EndTime = 5000, SingEndTime = 3000,
                        Units = words.Select(w => new TimedUnit { Text = LyricOriginals.RomaniseWord(w, "japanese").Text, Original = w, StartTime = 1000, EndTime = 3000 }).ToArray(),
                    },
                },
            };
            var engine = new TypingEngine(beatmap, polyglot: true, polyglotLanguage: "japanese");
            engine.Update(1000);
            foreach (char c in input)
                Assert.That(engine.ProcessKey(c, 1200), Is.True, c.ToString());
            Assert.Multiple(() =>
            {
                Assert.That(engine.Lines[0].DisplayText, Is.EqualTo(string.Join(' ', words)));
                Assert.That(engine.Lines[0].Cells.All(c => c.State == CellState.Correct), Is.True);
                Assert.That(engine.Mistypes, Is.Zero);
                Assert.That(engine.JapanesePending, Is.Empty);
            });
        }

        [TestCase("月 と テンシ")]
        [TestCase("月　と　ｷｬ")]
        public void EveryKanaCellHasAnInputReadingEvenWhenTheDictionarySkipsWhitespace(string original)
        {
            var plan = JapaneseInputPlan.Create(original);
            for (int at = 0; at < original.Length; at++)
            {
                char c = original[at];
                if (char.IsWhiteSpace(c))
                    Assert.That(plan.At(at), Is.Null);
                else
                    Assert.That(plan.At(at), Is.Not.Null, $"Missing reading at {at}: {c}");
            }
            Assert.That(plan.Groups.First().Reading, Is.EqualTo("つき"));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void TerminalNAllowsTypingTheNextJapaneseLineWithoutAnExtraN(bool skipping, bool manual)
        {
            var engine = repeatedTen(manual);
            engine.SpaceSkipsWord = skipping;
            foreach (char c in "ten")
                engine.ProcessKey(c, 1030);
            Assert.That(engine.ProcessKey('t', 1200), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(engine.Lines[0].Cells.Single().State, Is.EqualTo(CellState.Correct));
                Assert.That(engine.Lines[0].Cells.Single().JudgedDelta, Is.EqualTo(30));
                Assert.That(engine.ActiveLineIndex, Is.EqualTo(1));
                Assert.That(engine.JapanesePending, Is.EqualTo("t"));
                Assert.That(engine.Mistypes, Is.Zero);
                Assert.That(engine.LiveAccuracy, Is.EqualTo(1));
            });
            foreach (char c in "enn")
                engine.ProcessKey(c, 1420);
            Assert.That(engine.Lines.SelectMany(l => l.Cells).All(c => c.State == CellState.Correct), Is.True);
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void SpaceAndEnterConfirmTerminalNWithoutAbandoningEitherLine(bool enter, bool manual)
        {
            var engine = repeatedTen(manual);
            engine.SpaceSkipsWord = true;
            foreach (char c in "Ten")
                engine.ProcessKey(c, 1030);
            Assert.That(enter ? engine.ProcessEnter(1100) : engine.ProcessKey(' ', 1100), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(engine.Lines[0].Cells.Single().State, Is.EqualTo(CellState.Correct));
                Assert.That(engine.Lines[0].Cells.Single().JudgedDelta, Is.EqualTo(30));
                Assert.That(engine.ActiveLineIndex, Is.EqualTo(1));
                Assert.That(engine.CaretIndex, Is.Zero);
                Assert.That(engine.Lines[1].Cells.Single().State, Is.EqualTo(CellState.Untyped));
                Assert.That(engine.Mistypes, Is.Zero);
                Assert.That(engine.LiveAccuracy, Is.EqualTo(1));
                Assert.That(engine.JapanesePending, Is.Empty);
            });
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void LegacyJapaneseSpacesAreDeniedAndKeepPendingInput(bool skipping, bool gatekeeper)
        {
            var engine = create("月と");
            engine.JapaneseInputEra2 = false;
            engine.SpaceSkipsWord = skipping;
            engine.StrictSpaces = true;
            engine.AllowWrongInput = !gatekeeper;
            Assert.That(engine.ProcessKey(' ', 1000), Is.False);
            engine.ProcessKey('t', 1010);
            Assert.That(engine.ProcessKey(' ', 1020), Is.False);
            Assert.That(engine.JapanesePending, Is.EqualTo("t"));
            Assert.That(engine.ActiveLineUntouched, Is.False, "pending romaji is already typing on this line");
            foreach (char c in "uki")
                engine.ProcessKey(c, 1100);
            Assert.That(engine.ProcessKey(' ', 1110), Is.False);
            Assert.That(engine.CaretIndex, Is.EqualTo(1));
            foreach (char c in "to")
                engine.ProcessKey(c, 1200);
            Assert.That(engine.Lines[0].Cells.All(c => c.State == CellState.Correct), Is.True);
            Assert.That(engine.Mistypes, Is.Zero);
            Assert.That(engine.LiveAccuracy, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ConfirmingANasalInsideALineDoesNotSkipTheRemainingJapanese(bool enter)
        {
            var engine = create("本と");
            engine.FletcherEnabled = true;
            engine.SpaceSkipsWord = true;
            foreach (char c in "hon")
                engine.ProcessKey(c, 1030);
            Assert.That(enter ? engine.ProcessEnter(1100) : engine.ProcessKey(' ', 1100), Is.True);
            Assert.That(engine.Lines[0].Cells[0].State, Is.EqualTo(CellState.Correct));
            Assert.That(engine.Lines[0].Cells[1].State, Is.EqualTo(CellState.Untyped));
            Assert.That(engine.CaretIndex, Is.EqualTo(1));
            foreach (char c in "to")
                engine.ProcessKey(c, 1200);
            Assert.That(engine.Lines[0].Cells.All(c => c.State == CellState.Correct), Is.True);
            Assert.That(engine.Mistypes, Is.Zero);
        }

        [Test]
        public void AConfirmationDoesNotSkipFollowingLatinCharactersInTheSameWord()
        {
            var engine = create("天abc");
            engine.SpaceSkipsWord = true;
            foreach (char c in "ten")
                engine.ProcessKey(c, 1030);
            Assert.That(engine.ProcessKey(' ', 1100), Is.True);
            Assert.That(engine.CaretIndex, Is.EqualTo(1));
            Assert.That(engine.Lines[0].Cells.Skip(1).All(c => c.State == CellState.Untyped), Is.True);
            foreach (char c in "abc")
                engine.ProcessKey(c, 1200);
            Assert.That(engine.Lines[0].Cells.All(c => c.State == CellState.Correct), Is.True);
            Assert.That(engine.Mistypes, Is.Zero);
        }

        [Test]
        public void FreestyleStillAcceptsSpaceOnJapaneseMaps()
        {
            var engine = create("&");
            engine.SpaceSkipsWord = true;
            Assert.That(engine.ProcessKey(' ', 1100), Is.True);
            Assert.That(engine.Lines[0].Cells.Single().State, Is.EqualTo(CellState.Correct));
            Assert.That(engine.Lines[0].Cells.Single().TypedChar, Is.EqualTo(' '));
        }

        [TestCase("、")]
        [TestCase("!")]
        [TestCase("α")]
        [TestCase("Ж")]
        public void SpaceToSkipDoesNotApplyToOtherScriptsOrPunctuation(string original)
        {
            var engine = new TypingEngine(map(original), literate: true, polyglot: true, polyglotLanguage: "japanese") { SpaceSkipsWord = true };
            engine.Update(1000);
            Assert.That(engine.ProcessKey(' ', 1100), Is.False);
            Assert.That(engine.Lines[0].Cells.All(c => c.State == CellState.Untyped), Is.True);
            Assert.That(engine.Mistypes, Is.Zero);
        }

        [TestCase("😀")]
        [TestCase("𠀀")]
        public void RomanScriptDetectionHandlesSupplementaryCharacters(string original)
        {
            var plan = JapaneseInputPlan.Create(original);
            Assert.That(plan.IsRomanCell(0), Is.False);
            Assert.That(plan.IsRomanCell(1), Is.False);
        }

        [Test]
        public void SpaceToSkipCannotAbandonJapaneseInAMixedScriptWord()
        {
            var engine = create("abc月");
            engine.SpaceSkipsWord = true;
            Assert.That(engine.ProcessKey(' ', 1100), Is.True);
            Assert.That(engine.CaretIndex, Is.EqualTo(3));
            Assert.That(engine.Lines[0].Cells.Take(3).All(c => c.State == CellState.Abandoned), Is.True);
            Assert.That(engine.Lines[0].Cells[3].State, Is.EqualTo(CellState.Untyped));
            Assert.That(engine.Mistypes, Is.Zero);
            foreach (char c in "tuki")
                engine.ProcessKey(c, 1200);
            Assert.That(engine.Lines[0].Cells[3].State, Is.EqualTo(CellState.Correct));
        }

        [TestCase("abc")]
        [TestCase("ＡＢＣ")]
        [TestCase("é")]
        [TestCase("æ")]
        public void SpaceToSkipStillAppliesToLiteralRomanTextOnJapaneseMaps(string original)
        {
            var engine = create(original);
            engine.SpaceSkipsWord = true;
            Assert.That(engine.ProcessKey(' ', 1100), Is.True);
            Assert.That(engine.Lines[0].Cells.All(c => c.State == CellState.Abandoned), Is.True);
        }

        [Test]
        public void TerminalNIsJudgedAtItsTypedTimeWhenTheLineSeals()
        {
            var engine = create("天");
            foreach (char c in "ten")
                engine.ProcessKey(c, 1030);
            engine.Update(6000);
            Assert.That(engine.Lines[0].Cells.Single().State, Is.EqualTo(CellState.Correct));
            Assert.That(engine.Lines[0].Cells.Single().JudgedDelta, Is.EqualTo(30));
            Assert.That(engine.BuildResults().Counts[JudgementType.Miss], Is.Zero);
        }

        [Test]
        public void BackspaceEditsPendingRomajiBeforeErasingJapanese()
        {
            var engine = create("月");
            engine.ProcessKey('t', 1000);
            engine.ProcessKey('s', 1010);
            Assert.That(engine.CaretIndex, Is.Zero);
            Assert.That(engine.JapanesePending, Is.EqualTo("ts"));
            Assert.That(engine.ProcessBackspace(), Is.True);
            Assert.That(engine.JapanesePending, Is.EqualTo("t"));
            engine.ProcessKey('u', 1020);
            engine.ProcessKey('k', 1030);
            engine.ProcessKey('i', 1040);
            Assert.That(engine.Lines[0].Cells[0].State, Is.EqualTo(CellState.Correct));
            Assert.That(engine.ProcessBackspace(), Is.True);
            Assert.That(engine.Lines[0].Cells[0].State, Is.EqualTo(CellState.Untyped));
            foreach (char c in "tsuki")
                engine.ProcessKey(c, 1100);
            Assert.That(engine.Mistypes, Is.Zero);
        }

        [Test]
        public void WrongPrefixUsesTheExistingCorrectionRules()
        {
            var engine = create("月");
            engine.ProcessKey('t', 1000);
            engine.ProcessKey('z', 1010);
            Assert.That(engine.Mistypes, Is.EqualTo(1));
            Assert.That(engine.JapanesePending, Is.Empty);
            engine.ProcessBackspace();
            foreach (char c in "tuki")
                engine.ProcessKey(c, 1100);
            Assert.That(engine.Lines[0].Cells[0].State, Is.EqualTo(CellState.Correct));
        }

        [Test]
        public void RawRomajiAndPendingBackspaceReplayExactlyAndRewind()
        {
            var live = create("天使");
            var replay = create("天使");
            var header = TypeBeatReplayFrame.CreateExtendedConfigFrame(0, japaneseRomajiInput: true);
            var decoded = new TypeBeatReplayFrame();
            decoded.FromLegacy(header.ToLegacy(new Game.Beatmaps.Beatmap()), new Game.Beatmaps.Beatmap());
            Assert.That(decoded.JapaneseRomajiInput, Is.True);
            ReplayEngineFeed.Apply(replay, decoded);
            foreach (char c in "tens" )
            {
                live.ProcessKey(c, 1200);
                ReplayEngineFeed.Apply(replay, new TypeBeatReplayFrame(1200, c));
            }
            live.ProcessBackspace();
            ReplayEngineFeed.Apply(replay, new TypeBeatReplayFrame(1200, TypeBeatReplayFrame.BACKSPACE));
            foreach (char c in "shi")
            {
                live.ProcessKey(c, 1200);
                ReplayEngineFeed.Apply(replay, new TypeBeatReplayFrame(1200, c));
            }
            Assert.That(replay.Lines[0].Cells.Select(c => (c.State, c.JudgedDelta)), Is.EqualTo(live.Lines[0].Cells.Select(c => (c.State, c.JudgedDelta))));
            var frames = new Game.Rulesets.Replays.ReplayFrame[] { header, new TypeBeatReplayFrame(1000, 't'), new TypeBeatReplayFrame(1100, 'e'), new TypeBeatReplayFrame(1200, 'n'), new TypeBeatReplayFrame(1300, 's'), new TypeBeatReplayFrame(1400, 'i') };
            ReplayEngineFeed.RebuildTo(replay, frames, 1250);
            Assert.That(replay.JapanesePending, Is.EqualTo("ten"));
            ReplayEngineFeed.RebuildTo(replay, frames, 1450);
            Assert.That(replay.Lines[0].Cells.All(c => c.State == CellState.Correct), Is.True);
        }

        [Test]
        public void OldReplaysStillJudgeCommittedJapaneseAndMistypedLatinLiterally()
        {
            var engine = create("月");
            ReplayEngineFeed.Apply(engine, TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true));
            Assert.That(engine.JapaneseRomajiInput, Is.False);
            ReplayEngineFeed.Apply(engine, new TypeBeatReplayFrame(1200, 't'));
            Assert.That(engine.Mistypes, Is.EqualTo(1));
            engine.ProcessBackspace();
            ReplayEngineFeed.Apply(engine, new TypeBeatReplayFrame(1300, '月'));
            Assert.That(engine.Lines[0].Cells[0].State, Is.EqualTo(CellState.Correct));
        }

        [Test]
        public void ManualNewlineAfterFreestyleRequiresAValidJapaneseRomajiPrefix()
        {
            var japanese = map("月");
            var beatmap = new LyricBeatmap
            {
                Metadata = japanese.Metadata,
                Granularity = TimingGranularity.Word,
                Lines = new[]
                {
                    new LyricLine
                    {
                        RawText = "&", StartTime = 0, EndTime = 5000, SingEndTime = 3000,
                        Units = new[] { new TimedUnit { Text = "&", StartTime = 1000, EndTime = 3000 } },
                    },
                    japanese.Lines[0],
                },
            };
            var engine = new TypingEngine(beatmap, polyglot: true, polyglotLanguage: "japanese")
            {
                FletcherEnabled = true, ManualNewlines = true, NewlineOnTypedLetter = true,
            };
            engine.Update(1000);
            engine.ProcessKey('a', 1100);
            Assert.That(engine.IsLineComplete, Is.True);
            Assert.That(engine.ProcessKey('z', 1200), Is.False);
            Assert.That(engine.ActiveLineIndex, Is.Zero);
            Assert.That(engine.ProcessKey('t', 1300), Is.True);
            Assert.That(engine.ActiveLineIndex, Is.EqualTo(1));
            Assert.That(engine.JapanesePending, Is.EqualTo("t"));
            Assert.That(engine.Mistypes, Is.Zero);
        }

        [Test]
        public void GatekeeperRejectsOnlyTheBadKeyAndKeepsTheAcceptedPrefix()
        {
            var engine = create("天使");
            engine.AllowWrongInput = false;
            engine.ProcessKey('t', 1000);
            engine.ProcessKey('e', 1010);
            engine.ProcessKey('z', 1020);
            Assert.That(engine.JapanesePending, Is.EqualTo("te"));
            foreach (char c in "nshi")
                engine.ProcessKey(c, 1100);
            Assert.That(engine.Lines[0].Cells.All(c => c.State == CellState.Correct), Is.True);
            Assert.That(engine.Mistypes, Is.EqualTo(1));
        }

        [Test]
        public void FullwidthLatinDigitsAndJapanesePunctuationAcceptTheirKeyboardForms()
        {
            var engine = new TypingEngine(map("Ａ３、。「」"), literate: true, polyglot: true, polyglotLanguage: "japanese");
            engine.Update(1000);
            foreach (char c in "A3,.[]")
                engine.ProcessKey(c, 1200);
            Assert.That(engine.Mistypes, Is.Zero);
            Assert.That(engine.Lines[0].Cells.All(c => c.State == CellState.Correct), Is.True);
        }

        [Test]
        public void DirectKanaClearsPendingRomajiAndStillTypesNormally()
        {
            var engine = create("つき");
            engine.ProcessKey('t', 1000);
            engine.ProcessKey('つ', 1100);
            Assert.That(engine.JapanesePending, Is.Empty);
            engine.ProcessKey('き', 1200);
            Assert.That(engine.Mistypes, Is.Zero);
            Assert.That(engine.Lines[0].Cells.All(c => c.State == CellState.Correct), Is.True);
        }

        [Test]
        public void NasalAtAWordBoundaryKeepsTheFollowingConsonantAndItsTiming()
        {
            var engine = create("本と", "japanese");
            foreach (char c in "hon")
                engine.ProcessKey(c, 1100);
            Assert.That(engine.JapanesePending, Is.EqualTo("hon"));
            engine.ProcessKey('t', 1200);
            Assert.That(engine.Lines[0].Cells[0].TypedChar, Is.EqualTo('本'));
            Assert.That(engine.Lines[0].Cells[0].JudgedDelta, Is.EqualTo(100));
            engine.ProcessKey('o', 1250);
            Assert.That(engine.Lines[0].Cells.All(c => c.State == CellState.Correct), Is.True);
            Assert.That(engine.Mistypes, Is.Zero);
        }

        [Test]
        public void UnfinishedJapaneseReadingsSurviveDeniedConfirmationKeys()
        {
            var engine = create("月");
            engine.ProcessKey('t', 1100);
            Assert.That(engine.ProcessEnter(1200), Is.False);
            Assert.That(engine.JapanesePending, Is.EqualTo("t"));
            Assert.That(engine.ProcessKey(' ', 1400), Is.False);
            Assert.That(engine.JapanesePending, Is.EqualTo("t"));
            engine.JapaneseRomajiInput = false;
            Assert.That(engine.JapanesePending, Is.Empty);
        }

        [Test]
        public void FuriganaCoversKanjiAndExcludesOkuriganaAndLatin()
        {
            var plan = JapaneseInputPlan.Create("月と天使が食べるabc");
            Assert.That(plan.Groups.Where(g => g.Furigana).Select(g => (g.StartCell, g.EndCellExclusive, g.Reading)),
                Is.EqualTo(new[] { (0, 1, "つき"), (2, 4, "てんし"), (5, 6, "た") }));
        }

        [Test]
        public void AuthoredKanjiReadingWinsOverAnAmbiguousDictionaryPronunciation()
        {
            var beatmap = map("月", "getsu");
            var engine = new TypingEngine(beatmap, polyglot: true, polyglotLanguage: "japanese");
            engine.Update(1000);
            Assert.That(engine.Lines[0].JapaneseInput!.Groups[0].Reading, Is.EqualTo("げつ"));
            foreach (char c in "getu")
                engine.ProcessKey(c, 1200);
            Assert.That(engine.Lines[0].Cells[0].State, Is.EqualTo(CellState.Correct));
            Assert.That(engine.Mistypes, Is.Zero);
        }

        [TestCase("本を", "hono")]
        [TestCase("本へ", "hone")]
        public void NasalBeforeAPronunciationVowelNeedsAnExplicitSecondN(string original, string input)
        {
            var engine = create(original);
            foreach (char c in input)
                engine.ProcessKey(c, 1200);
            Assert.That(engine.Lines[0].Cells.All(c => c.State == CellState.Correct), Is.False);
            Assert.That(engine.Mistypes, Is.GreaterThan(0));
        }

        [TestCase("wa", "わ")]
        [TestCase("o", "お")]
        [TestCase("e", "え")]
        [TestCase("jigokugoku!", "じごくごく")]
        [TestCase("(getsu)", "げつ")]
        [TestCase("ten'!", "てん")]
        [TestCase("ko-hi-!", "こーひー")]
        public void AuthoredReadingsKeepImeMarkersAndIgnoreBoundaryPunctuation(string spelling, string expected)
            => Assert.That(JapaneseRomaji.ReadAuthored(spelling), Is.EqualTo(expected));

        [TestCase(false)]
        [TestCase(true)]
        public void SongSpecificKanjiPronunciationSurvivesTrailingPunctuation(bool literate)
        {
            var beatmap = new LyricBeatmap
            {
                Metadata = map("天").Metadata,
                Granularity = TimingGranularity.Word,
                Lines = new[]
                {
                    new LyricLine
                    {
                        RawText = "tententengoku jigokugoku!", Original = "天天天国地獄国！",
                        StartTime = 0, EndTime = 5000, SingEndTime = 3000,
                        Units = new[]
                        {
                            new TimedUnit { Text = "tententengoku", Original = "天天天国", StartTime = 1000, EndTime = 2000 },
                            new TimedUnit { Text = "jigokugoku!", Original = "地獄国！", StartTime = 2000, EndTime = 3000 },
                        },
                    },
                },
            };
            var engine = new TypingEngine(beatmap, literate: literate, polyglot: true, polyglotLanguage: "japanese") { SpaceSkipsWord = true };
            engine.Update(1000);
            foreach (char c in "tententengoku")
                engine.ProcessKey(c, 1200);
            foreach (char c in "jigokugoku")
                engine.ProcessKey(c, 2200);
            Assert.That(engine.Lines[0].Cells.Where(c => char.IsLetter(c.Expected)).All(c => c.State == CellState.Correct), Is.True);
            Assert.That(engine.Mistypes, Is.Zero);
            if (literate)
            {
                Assert.That(engine.Lines[0].Cells.Last().State, Is.EqualTo(CellState.Untyped), "punctuation must still be typed under Literate");
                engine.ProcessKey('!', 2250);
                Assert.That(engine.Lines[0].Cells.Last().State, Is.EqualTo(CellState.Correct));
            }
        }

        [Test]
        public void NonJapanesePolyglotAndUnmodifiedPlayDoNotUseRomajiOrFurigana()
        {
            Assert.That(create("天使", "chinese").UsesJapaneseRomaji, Is.False);
            Assert.That(create("天使", "chinese").Lines[0].JapaneseInput, Is.Null);
            var normal = new TypingEngine(map("月"));
            Assert.That(normal.UsesJapaneseRomaji, Is.False);
            Assert.That(normal.Lines[0].JapaneseInput, Is.Null);
        }

        private static TypingEngine repeatedTen(bool manual = true)
        {
            var beatmap = new LyricBeatmap
            {
                Metadata = map("天").Metadata, Granularity = TimingGranularity.Word,
                Lines = new[]
                {
                    new LyricLine
                    {
                        RawText = "ten", Original = "天", StartTime = 0, EndTime = 1400, SingEndTime = 1300,
                        Units = new[] { new TimedUnit { Text = "ten", Original = "天", StartTime = 1000, EndTime = 1300 } },
                    },
                    new LyricLine
                    {
                        RawText = "ten", Original = "天", StartTime = 1400, EndTime = 5000, SingEndTime = 1700,
                        Units = new[] { new TimedUnit { Text = "ten", Original = "天", StartTime = 1400, EndTime = 1700 } },
                    },
                },
            };
            var engine = new TypingEngine(beatmap, polyglot: true, polyglotLanguage: "japanese")
            {
                FletcherEnabled = true, ManualNewlines = manual, NewlineOnTypedLetter = true, BoundedRush = true,
            };
            engine.Update(1000);
            return engine;
        }

        private static TypingEngine create(string original, string language = "japanese")
        {
            var engine = new TypingEngine(map(original), polyglot: true, polyglotLanguage: language);
            engine.Update(1000);
            return engine;
        }

        private static LyricBeatmap map(string original, string? spelling = null) => new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata { Artist = "A", Title = "T", AudioFileName = "a.mp3", FolderPath = string.Empty },
            Granularity = TimingGranularity.Word,
            Lines = new[]
            {
                new LyricLine
                {
                    RawText = "reading", StartTime = 0, EndTime = 5000, SingEndTime = 3000,
                    Units = new[] { new TimedUnit { Text = spelling ?? LyricOriginals.RomaniseWord(original, "japanese").Text, Original = original, StartTime = 1000, EndTime = 3000 } },
                },
            },
        };
    }
}
