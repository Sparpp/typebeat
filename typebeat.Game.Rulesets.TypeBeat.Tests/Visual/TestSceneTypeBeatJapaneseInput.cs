// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework;
using osu.Framework.Input;
using osu.Framework.Testing;
using osuTK.Input;
using typebeat.Game.Beatmaps;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Replays.Legacy;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    public partial class TestSceneTypeBeatJapaneseInput : PlayerTestScene
    {
        protected override bool HasCustomSteps => true;
        protected override Ruleset CreatePlayerRuleset() => new TypeBeatRuleset();
        private TypeBeatPlayfield playfield => (TypeBeatPlayfield)Player.DrawableRuleset.Playfield;
        private TypingEngine engine => playfield.Engine;
        private LyricStage stage => Player.ChildrenOfType<LyricStage>().Single();
        private LyricLineDisplay display => stage.DisplayAt(0)!;
        private LyricLine? testLine;
        private LyricLine? nextTestLine;
        private EmulatedTextInput os => ((KeycapManualInputManager)InputManager).TextInput;
        private TypeBeatRulesetConfigManager config => (TypeBeatRulesetConfigManager)RulesetConfigs.GetConfigFor(new TypeBeatRuleset())!;
        private List<TypeBeatReplayFrame> frames => Player.GameplayState.Score.Replay.Frames.OfType<TypeBeatReplayFrame>().ToList();
        private OsuSpriteText ruby(string text) => display.ChildrenOfType<OsuSpriteText>().Single(t => t.Text.ToString() == text);

        public override void SetUpSteps()
        {
            AddStep("reset lyric fixture", () => { testLine = null; nextTestLine = null; });
            AddStep("word skipping off", () => config.SetValue(TypeBeatRulesetSetting.SpaceSkipsWord, false));
            AddStep("manual newlines off", () => config.SetValue(TypeBeatRulesetSetting.ManualNewlines, false));
            base.SetUpSteps();
        }

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var beatmap = new Beatmap { HitObjects = new List<Rulesets.Objects.HitObject>() };
            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.BeatmapInfo.Metadata.Language = BeatmapLanguage.Japanese;
            var line = testLine ?? new LyricLine
            {
                RawText = "tsukitotenshi", Original = "月と天使",
                StartTime = 0, EndTime = 600000, SingEndTime = 300000,
                Units = new[] { new TimedUnit { Text = "tsukitotenshi", Original = "月と天使", StartTime = 0, EndTime = 300000 } },
            };
            beatmap.HitObjects.Add(new TypeBeatHitObject { StartTime = 0, LineIndex = 0, Line = line, Granularity = TimingGranularity.Word });
            if (nextTestLine != null)
                beatmap.HitObjects.Add(new TypeBeatHitObject { StartTime = 0, LineIndex = 1, Line = nextTestLine, Granularity = TimingGranularity.Word });
            return beatmap;
        }

        [TestCase("tuki", "tensi")]
        [TestCase("tsuki", "tenshi")]
        [TestCase("tuki", "tennsi")]
        [TestCase("tsuki", "tennshi")]
        public void TestRomajiTypesJapaneseWithoutImeSubmission(string moon, string angel)
        {
            load();
            AddAssert("Japanese input uses Code with IME disabled", () => os.ActiveProperties,
                () => Is.EqualTo(new TextInputProperties(TextInputType.Code, AllowIme: false)));
            AddAssert("furigana is present", () => ruby("つき").IsLoaded && ruby("てんし").IsLoaded);
            press("t");
            AddUntilStep("partial romaji is visible", () => engine.JapanesePending == "t" && stage.ImeComposition == "t");
            press(moon[1..]);
            AddAssert("月 converted without enter", () => engine.CaretIndex == 1 && engine.Lines[0].Cells[0].State == CellState.Correct);
            press("to" + angel);
            AddAssert("whole Japanese line typed correctly", () => engine.Lines[0].Cells.All(c => c.State == CellState.Correct) && engine.Mistypes == 0);
            AddAssert("replay stores raw romaji and its input flag", () =>
                frames.Single(f => f.IsConfigExtended).JapaneseRomajiInput
                && new string(frames.Where(f => !f.IsConfig && !f.IsConfigExtended).Select(f => f.Character).ToArray()) == moon + "to" + angel);
            AddAssert("legacy replay reproduces the play", reDerivedMatchesLive);
        }

        [Test]
        public void TestJapaneseWordsIgnoreRomanisedSubdivisions()
        {
            AddStep("use a subdivided kana word", () => testLine = new LyricLine
            {
                RawText = "konnichiha", Original = "こんにちは", StartTime = 0, EndTime = 600000, SingEndTime = 300000,
                Units = new[]
                {
                    new TimedUnit
                    {
                        Text = "konnichiha", Original = "こんにちは", StartTime = 0, EndTime = 300000,
                        SyllableBoundaries = new[] { 100.0, 200, 300 }, SyllableSplits = new[] { 3, 5, 8 },
                    },
                },
            });
            load();
            AddAssert("the whole mapper word has one timing window", () => engine.Lines[0].Syllables.Count == 1
                && engine.Lines[0].Syllables[0].StartTime == 0 && engine.Lines[0].Syllables[0].EndTime == 300000);
            press("konnichiha");
            AddAssert("all kana typed correctly", () => engine.Lines[0].Cells.All(c => c.State == CellState.Correct) && engine.Mistypes == 0);
            AddAssert("the recorder stores the word timing rule", () => frames.Single(f => f.IsConfigExtended).JapaneseWordTiming);
            AddAssert("the recorded timing and score replay exactly", reDerivedMatchesLive);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TestKanaAndWordSeamsThroughGameplayAndReplay(bool originalSpaces)
        {
            AddStep("use mixed kanji and kana word originals", () => testLine = new LyricLine
            {
                RawText = "tsuki to tenshi", Original = originalSpaces ? "月 と テンシ" : null,
                StartTime = 0, EndTime = 600000, SingEndTime = 300000,
                Units = new[]
                {
                    new TimedUnit { Text = "tsuki", Original = "月", StartTime = 0, EndTime = 100000 },
                    new TimedUnit { Text = "to", Original = "と", StartTime = 100000, EndTime = 200000 },
                    new TimedUnit { Text = "tenshi", Original = "テンシ", StartTime = 200000, EndTime = 300000 },
                },
            });
            load();
            AddAssert("only original spaces are typeable", () => engine.Lines[0].DisplayText == (originalSpaces ? "月 と テンシ" : "月とテンシ"));
            AddAssert("scoring objects use the same cell stream", () => Player.GameplayState.Beatmap.HitObjects.OfType<TypeBeatHitObject>().Single().NestedHitObjects.OfType<TypeBeatCharObject>().Count()
                == engine.Lines[0].Cells.Count(c => c.IsTypeable));
            press("tuki");
            if (originalSpaces)
                AddStep("type the first original space", () => InputManager.Key(Key.Space));
            press("to");
            if (originalSpaces)
                AddStep("type the second original space", () => InputManager.Key(Key.Space));
            press("tennshi");
            AddAssert("kanji, hiragana and katakana are correct", () => engine.Lines[0].Cells.All(c => c.State == CellState.Correct) && engine.Mistypes == 0);
            AddAssert("spaced or joined play replays exactly", reDerivedMatchesLive);
        }

        [TestCase("typing")]
        [TestCase("space")]
        [TestCase("enter")]
        public void TestRepeatedTenWithManualNewlinesAndWordSkipping(string confirmation)
        {
            AddStep("manual newlines and word skipping on", () =>
            {
                config.SetValue(TypeBeatRulesetSetting.ManualNewlines, true);
                config.SetValue(TypeBeatRulesetSetting.SpaceSkipsWord, true);
                testLine = nextTestLine = new LyricLine
                {
                    RawText = "ten", Original = "天", StartTime = 0, EndTime = 600000, SingEndTime = 300000,
                    Units = new[] { new TimedUnit { Text = "ten", Original = "天", StartTime = 0, EndTime = 300000 } },
                };
            });
            load();
            press("ten");
            AddAssert("single final n is pending confirmation", () => engine.JapanesePending == "ten");
            if (confirmation == "typing")
                press("t");
            else
                AddStep("confirm and move to the next line", () => InputManager.Key(confirmation == "space" ? Key.Space : Key.Enter));
            AddAssert("first 天 is correct and next line is not skipped", () => engine.Lines[0].Cells.Single().State == CellState.Correct
                && engine.ActiveLineIndex == 1 && engine.CaretIndex == 0 && engine.Lines[1].Cells.Single().State == CellState.Untyped && engine.Mistypes == 0);
            press(confirmation == "typing" ? "enn" : "tenn");
            AddAssert("both 天 are correct", () => engine.Lines.SelectMany(l => l.Cells).All(c => c.State == CellState.Correct) && engine.LiveAccuracy == 1);
            AddAssert("confirmation and typed newline replay exactly", reDerivedMatchesLive);
        }

        [Test]
        public void TestExtraSpacesDoNotSkipJapaneseWithWordSkippingEnabled()
        {
            AddStep("word skipping on", () => config.SetValue(TypeBeatRulesetSetting.SpaceSkipsWord, true));
            load();
            press("tuki");
            AddStep("extra space between Japanese characters", () => InputManager.Key(Key.Space));
            AddAssert("next character stays untyped", () => engine.CaretIndex == 1 && engine.Lines[0].Cells[1].State == CellState.Untyped && engine.Mistypes == 0);
            press("totenshi");
            AddAssert("all Japanese remains correct", () => engine.Lines[0].Cells.All(c => c.State == CellState.Correct) && engine.LiveAccuracy == 1);
            AddAssert("denied spaces cannot desync the replay", reDerivedMatchesLive);
        }

        [Test]
        public void TestSpaceSkipsOnlyAnIncompleteJapaneseCharacter()
        {
            AddStep("word skipping on", () => config.SetValue(TypeBeatRulesetSetting.SpaceSkipsWord, true));
            load();
            press("t");
            AddStep("skip the unfinished moon", () => InputManager.Key(Key.Space));
            AddAssert("only 月 was skipped", () => engine.CaretIndex == 1 && engine.JapanesePending.Length == 0
                && engine.Lines[0].Cells[0].State == CellState.Abandoned
                && engine.Lines[0].Cells.Skip(1).All(c => c.State == CellState.Untyped));
            press("to");
            AddStep("confirmation space after と", () => InputManager.Key(Key.Space));
            AddAssert("天 stays untyped", () => engine.CaretIndex == 2 && engine.Lines[0].Cells[2].State == CellState.Untyped);
            press("t");
            AddStep("skip 天 inside its conversion group", () => InputManager.Key(Key.Space));
            AddAssert("使 remains available", () => engine.CaretIndex == 3 && engine.Lines[0].Cells[3].State == CellState.Untyped);
            press("shi");
            AddAssert("the remaining character accepts its own reading", () => engine.Lines[0].Cells[3].State == CellState.Correct && engine.Mistypes == 0);
            AddAssert("the recorder stores the new input rules", () => frames.Single(f => f.IsConfigExtended).JapaneseInputEra2);
            AddAssert("skips and remaining readings replay exactly", reDerivedMatchesLive);
        }

        [TestCase("waoe")]
        [TestCase("hawohe")]
        public void TestParticlePronunciationsThroughGameplayAndReplay(string input)
        {
            AddStep("Japanese particles fixture", () => testLine = new LyricLine
            {
                RawText = "hawohe", Original = "はをへ", StartTime = 0, EndTime = 600000, SingEndTime = 300000,
                Units = new[] { new TimedUnit { Text = "hawohe", Original = "はをへ", StartTime = 0, EndTime = 300000 } },
            });
            load();
            press(input);
            AddAssert("phonetic or literal kana readings are accepted", () => engine.Lines[0].Cells.All(c => c.State == CellState.Correct) && engine.Mistypes == 0);
            AddAssert("kana pronunciations replay exactly", reDerivedMatchesLive);
        }

        [TestCase("きぃてゃてぃでぇふゅ", "kyithat'idhehwyu")]
        [TestCase("っやっちゃ", "yyatcha")]
        [TestCase("とぁどぉぐぅすぇずぉ", "twadwogwuswezwo")]
        public void TestExtendedWapuroSpellingsThroughGameplayAndReplay(string kana, string input)
        {
            AddStep("extended wapuro fixture", () => testLine = new LyricLine
            {
                RawText = input, Original = kana, StartTime = 0, EndTime = 600000, SingEndTime = 300000,
                Units = new[] { new TimedUnit { Text = input, Original = kana, StartTime = 0, EndTime = 300000 } },
            });
            load();
            press(input);
            AddAssert("extended spellings type every kana", () => engine.Lines[0].Cells.All(c => c.State == CellState.Correct) && engine.Mistypes == 0);
            AddAssert("extended spellings replay exactly", reDerivedMatchesLive);
        }

        [Test]
        public void TestPendingBackspaceAndFuriganaLayout()
        {
            load();
            press("ts");
            AddStep("backspace the pending s", () => InputManager.Key(Key.BackSpace));
            AddUntilStep("only t remains", () => engine.JapanesePending == "t");
            press("uki");
            AddAssert("the pending erase is recorded", () => frames.Count(f => f.IsBackspace) == 1);
            AddAssert("ruby is small and above the kanji", () =>
                ruby("つき").Font.Size < display.ChildrenOfType<OsuSpriteText>().Single(t => t.Text.ToString() == "月").Font.Size
                && ruby("つき").ScreenSpaceDrawQuad.BottomLeft.Y < display.CellScreenPosition(0).Y - display.LineHeight * 0.25f);
            AddAssert("the erase replays exactly", reDerivedMatchesLive);
        }

        [Test]
        public void TestRetypeSelectionErasesPendingRomajiAndTheSelectedJapanese()
        {
            load();
            press("zt");
            AddAssert("next reading has a pending t", () => engine.CaretIndex == 1 && engine.JapanesePending == "t");
            AddStep("select back with the recovery shortcut", () =>
            {
                Key modifier = RuntimeInfo.OS == RuntimeInfo.Platform.macOS ? Key.LWin : Key.ControlLeft;
                InputManager.PressKey(modifier);
                InputManager.Key(Key.A);
                InputManager.ReleaseKey(modifier);
            });
            AddAssert("the first Japanese character is selected", () => playfield.CurrentRetypeSelection is TypeBeatPlayfield.RetypeSelection { StartCell: 0, EndCell: 1 });
            press("t");
            AddAssert("typing restarted at the selection anchor", () => engine.CaretIndex == 0 && engine.JapanesePending == "t" && playfield.CurrentRetypeSelection == null);
            press("uki");
            AddAssert("the pending letter and the selected character were both erased", () => frames.Count(f => f.IsBackspace) == 2);
            AddAssert("the recovery replays exactly", reDerivedMatchesLive);
        }

        [Test]
        public void TestReciteAndFlashlightAlsoHideFurigana()
        {
            load(recite: true);
            AddUntilStep("untyped kanji and ruby are hidden", () => display.CellAlpha(0) == 0 && ruby("つき").Alpha == 0 && ruby("てんし").Alpha == 0);
            press("tuki");
            AddUntilStep("the typed kanji reading is revealed", () => ruby("つき").Alpha > 0.8f);
            AddAssert("the unread compound remains hidden", () => ruby("てんし").Alpha == 0);
            AddStep("hide the line for Flashlight", () => display.HideForFlashlight());
            AddUntilStep("ruby hides with its kanji", () => ruby("つき").Alpha == 0);
        }

        private void load(bool recite = false)
        {
            AddStep("load Japanese Polyglot", () => LoadPlayer(recite
                ? new Rulesets.Mods.Mod[] { new TypeBeatModPolyglot(), new TypeBeatModRecite() }
                : new Rulesets.Mods.Mod[] { new TypeBeatModPolyglot() }));
            AddUntilStep("player and line loaded", () => Player.IsLoaded && Player.Alpha == 1 && engine.ActiveLineIndex == 0);
        }

        private void press(string text)
        {
            foreach (char c in text)
                AddStep($"press {c}", () => InputManager.Key(c switch
                {
                    '\'' => Key.Quote,
                    '-' => Key.Minus,
                    ' ' => Key.Space,
                    _ => Enum.Parse<Key>(char.ToUpperInvariant(c).ToString()),
                }));
        }

        private bool reDerivedMatchesLive()
        {
            var map = Player.GameplayState.Beatmap;
            var replayed = TypeBeatReplayScorer.CreateEngine(map, map.HitObjects.OfType<TypeBeatHitObject>().ToList(), Player.GameplayState.Mods, RateWindowRule.ScaledByRate);
            var dummy = new Beatmap();
            foreach (var frame in frames)
            {
                var legacy = frame.ToLegacy(dummy);
                var decoded = new TypeBeatReplayFrame();
                decoded.FromLegacy(new LegacyReplayFrame(legacy.Time, legacy.MouseX, legacy.MouseY, legacy.ButtonState), dummy);
                decoded.Time = legacy.Time;
                ReplayEngineFeed.Apply(replayed, decoded);
            }
            return engine.Lines.SelectMany(l => l.Cells).Zip(replayed.Lines.SelectMany(l => l.Cells)).All(p => p.First.State == p.Second.State && p.First.TypedChar == p.Second.TypedChar && p.First.JudgedDelta == p.Second.JudgedDelta)
                   && engine.Score == replayed.Score && engine.Mistypes == replayed.Mistypes && engine.Combo == replayed.Combo;
        }
    }
}
