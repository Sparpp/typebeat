// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Input;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Replays.Legacy;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Tests.Visual;
using osuTK.Input;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Backlog 383 through the REAL playfield: a Latin play types the characters the OS COMMITS,
    /// folded to the surface, rather than physical keys read through layout tables. The scene's input
    /// manager stands in for the OS (<see cref="EmulatedTextInput"/>): every key press commits what a
    /// US layout commits, in the same frame and after the key, unless a test queues a different
    /// commit for the press (an accented letter, 'ß', a dead key's nothing), and an OS key REPEAT is a
    /// commit with no press behind it.
    ///
    /// <para>The pins: what the activation is; that characters land at the time their key went down,
    /// including one whose commit arrives frames after its key and the spacebar's fallback when none
    /// does (and at the pairing time without that era); the é / ß / inert cases; a dead key then its
    /// vowel typing the base letter once (on both the plain-text and the composition route); OS
    /// repeats dropped while Backspace keeps its hold-to-erase; the spacebar's fallback when no space
    /// is committed; the default surface's positional digit row against Literate's marks; pause
    /// releasing the source; and a run recorded through text input re-deriving cell for cell
    /// through the legacy replay encoding.</para>
    /// </summary>
    public partial class TestSceneTypeBeatTextInput : PlayerTestScene
    {
        protected override Ruleset CreatePlayerRuleset() => new TypeBeatRuleset();

        protected override bool HasCustomSteps => true;

        /// <summary>A player that may pause (the scene base builds one that may not), for the pause pin.</summary>
        protected override TestPlayer CreatePlayer(Ruleset ruleset) => new TestPlayer(allowPause: true);

        private TypeBeatRulesetConfigManager config => (TypeBeatRulesetConfigManager)RulesetConfigs.GetConfigFor(new TypeBeatRuleset())!;

        private EmulatedTextInput os => ((KeycapManualInputManager)InputManager).TextInput;

        private TypeBeatPlayfield playfield => (TypeBeatPlayfield)Player.DrawableRuleset.Playfield;

        private TypingEngine engine => playfield.Engine;

        private List<TypeBeatReplayFrame> frames => Player.GameplayState.Score.Replay.Frames.OfType<TypeBeatReplayFrame>().ToList();

        /// <summary>The recorded keystrokes, without the CONFIG header frames the recorder stamps ahead of the first one.</summary>
        private List<TypeBeatReplayFrame> keystrokes => frames.Where(f => !f.IsConfig && !f.IsConfigExtended).ToList();

        /// <summary>The playfield's key handler (private to it), whose clock is the one every keystroke is stamped with.</summary>
        private Drawable handler => playfield.ChildrenOfType<Drawable>().Single(d => d.GetType().Name == "TypeBeatKeyHandler");

        private TypingCell cell(int index) => engine.Lines[0].Cells[index];

        public override void SetUpSteps()
        {
            // The arms every pin here was written on: a mid-word space is a typo rather than a word
            // skip, and a finished line is handed on automatically.
            AddStep("word skipping off", () => config.SetValue(TypeBeatRulesetSetting.SpaceSkipsWord, false));
            AddStep("manual newlines off", () => config.SetValue(TypeBeatRulesetSetting.ManualNewlines, false));
            AddStep("the default map", () => sealingMap = false);
            base.SetUpSteps();
        }

        /// <summary>
        /// "ab ess 1!": without Literate the cells are a b ' ' e s s ' ' 1 (the '!' leaves the default
        /// stream), under Literate a b ' ' e s s ' ' 1 !. The windows are enormous so nothing here is
        /// timing-sensitive; a far-away second line keeps the map from ending under the test.
        /// </summary>
        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var beatmap = new Beatmap { HitObjects = new List<Rulesets.Objects.HitObject>() };

            if (sealingMap)
                return createSealingBeatmap(beatmap, ruleset);

            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.BeatmapInfo.Metadata.Artist = "Test";
            beatmap.BeatmapInfo.Metadata.Title = "TextInput";

            var line = new LyricLine
            {
                RawText = "ab ess 1!",
                StartTime = 0,
                EndTime = 600000,
                SingEndTime = 300000,
                Units = new[]
                {
                    new TimedUnit { Text = "ab", StartTime = 0, EndTime = 100000 },
                    new TimedUnit { Text = "ess", StartTime = 100000, EndTime = 200000 },
                    new TimedUnit { Text = "1!", StartTime = 200000, EndTime = 300000 },
                },
            };

            var trailing = new LyricLine
            {
                RawText = "g",
                StartTime = 700000,
                EndTime = 1200000,
                SingEndTime = 800000,
                Units = new[] { new TimedUnit { Text = "g", StartTime = 750000, EndTime = 800000 } },
            };

            beatmap.HitObjects.Add(new TypeBeatHitObject { StartTime = line.StartTime, LineIndex = 0, Line = line, Granularity = TimingGranularity.Word });
            beatmap.HitObjects.Add(new TypeBeatHitObject { StartTime = trailing.StartTime, LineIndex = 1, Line = trailing, Granularity = TimingGranularity.Word });

            return beatmap;
        }

        [Test]
        public void TestALatinPlayActivatesTextInputWithTheImeOff()
        {
            load();

            AddAssert("the OS text source is active", () => os.IsActive);
            AddAssert("as Code with the IME disallowed", () => os.ActiveProperties, () => Is.EqualTo(new TextInputProperties(TextInputType.Code, AllowIme: false)));
        }

        [Test]
        public void TestPauseReleasesTheSourceAndResumeTakesItBack()
        {
            load();

            AddUntilStep("pause", () => Player.Pause());
            AddUntilStep("the source is released", () => !os.IsActive);

            AddStep("resume", () => Player.Resume());
            AddUntilStep("the source is taken back", () => os.IsActive);
        }

        [Test]
        public void TestTheCommittedCharacterIsWhatIsTyped()
        {
            load();

            press(Key.A);
            press(Key.B);
            press(Key.Space);

            // The 'e' cell from an accented commit, and both 's' cells from one 'ß' press.
            press(Key.E, "é");
            press(Key.S, "ß");

            AddAssert("a b ' ' e s s all landed correct", () => Enumerable.Range(0, 6).All(i => cell(i).State == CellState.Correct) && engine.CaretIndex == 6);
            AddAssert("the eszett is two frames at one timestamp", () =>
            {
                var last = keystrokes.TakeLast(2).ToList();
                return last.All(f => f.Character == 's') && last[0].Time == last[1].Time;
            });
            AddAssert("the accented commit recorded the base letter", () => keystrokes.Any(f => f.Character == 'e') && keystrokes.All(f => f.Character != 'é'));
            AddAssert("the run re-derives exactly", reDerivedMatchesLive);
        }

        [Test]
        public void TestACharacterTheSurfaceCannotHoldIsInert()
        {
            load();

            press(Key.A);
            AddStep("commit a euro sign on the E key", () =>
            {
                os.CommitOnNextPress("€");
                InputManager.Key(Key.E);
            });
            press(Key.Comma);

            AddAssert("nothing was judged, no wrong key, no frame", () =>
                engine.CaretIndex == 1 && cell(1).State == CellState.Untyped && engine.Mistypes == 0 && keystrokes.Count == 1);
        }

        [Test]
        public void TestADeadKeyThenItsVowelTypesTheBaseLetterOnce()
        {
            load();

            typeAbSpace();

            // Windows and X11: the dead key commits nothing, the vowel commits the composed letter.
            AddStep("press the dead circumflex (commits nothing)", () =>
            {
                os.CommitOnNextPress(null);
                InputManager.Key(Key.BracketLeft);
            });
            AddAssert("nothing typed yet", () => engine.CaretIndex == 3 && keystrokes.Count == 3);

            press(Key.E, "ê");
            AddAssert("one 'e', correct", () => engine.CaretIndex == 4 && cell(3).State == CellState.Correct && keystrokes.Count(f => f.Character == 'e') == 1);

            // macOS: the accent is a COMPOSITION (marked text), the vowel arrives as its result.
            AddStep("compose an accent", () =>
            {
                os.CommitOnNextPress(null);
                InputManager.Key(Key.Quote);
                os.TriggerImeComposition("´", 1, 0);
            });
            AddAssert("the composition typed nothing", () => engine.CaretIndex == 4);

            AddStep("the vowel ends the composition", () =>
            {
                os.CommitOnNextPress(null);
                InputManager.Key(Key.S);
                os.TriggerImeResult("ś");
            });
            AddAssert("one 's', correct", () => engine.CaretIndex == 5 && cell(4).State == CellState.Correct && keystrokes.Count(f => f.Character == 's') == 1);
        }

        /// <summary>
        /// A held key makes the OS commit its character again and again with no new press behind
        /// it; none of those is typed. The game's own hold-to-repeat was removed outright in backlog
        /// 105, so this is the whole repeat rule: one judgement per physical press. Backspace is a
        /// key and keeps its hold-to-erase.
        /// </summary>
        [Test]
        public void TestOsKeyRepeatIsDroppedAndBackspaceStillRepeats()
        {
            load();

            AddStep("press and hold A", () => InputManager.PressKey(Key.A));
            AddStep("the OS repeats it", () => os.Text("a"));
            AddStep("and again", () => os.Text("a"));
            AddWaitStep("let the framework's own repeat run", 10);
            AddStep("and again", () => os.Text("a"));
            AddStep("release A", () => InputManager.ReleaseKey(Key.A));

            AddAssert("exactly one 'a' was typed", () => engine.CaretIndex == 1 && keystrokes.Count == 1);

            press(Key.B);
            press(Key.Space);
            AddAssert("the next press is unaffected", () => engine.CaretIndex == 3 && Enumerable.Range(0, 3).All(i => cell(i).State == CellState.Correct));

            AddStep("hold backspace", () => InputManager.PressKey(Key.BackSpace));
            AddUntilStep("it erases back to the head of the line", () => engine.CaretIndex == 0);
            AddStep("release backspace", () => InputManager.ReleaseKey(Key.BackSpace));
        }

        [Test]
        public void TestAStrayCommitWithNoPressIsDropped()
        {
            Stopwatch sinceStray = null!;

            load();

            AddStep("commit 'a' with no key behind it", () =>
            {
                os.Text("a");
                sinceStray = Stopwatch.StartNew();
            });
            AddUntilStep("let the pairing give up on it", () => sinceStray.Elapsed.TotalMilliseconds > 2 * TypeBeatPlayfield.PAIRING_GRACE_MS);
            AddAssert("nothing typed", () => engine.CaretIndex == 0 && keystrokes.Count == 0);

            // A dead key's press commits nothing and expires the same way: the next press is not
            // handed the stray, and the stray is not handed to it.
            AddStep("press a dead key, committing nothing", () =>
            {
                os.CommitOnNextPress(null);
                InputManager.Key(Key.BracketLeft);
            });

            press(Key.A);
            AddAssert("a real press types exactly once", () => engine.CaretIndex == 1 && keystrokes.Count == 1);
        }

        /// <summary>
        /// A commit can land one frame after its key (the two halves of a keystroke reach the update
        /// thread on different paths, and the update thread can run between them); the press waits
        /// for it and it is typed, once, at the time its KEY went down (TypingEngine.KeyTimeJudgement,
        /// which the recorder writes on the extended header).
        /// </summary>
        [Test]
        public void TestALateCommitIsJudgedAtItsKeyTime()
        {
            double pressed = 0;

            load();

            AddStep("press A, its commit a frame late", () =>
            {
                pressed = handler.Time.Current;
                os.CommitLateOnNextPress("a");
                InputManager.Key(Key.A);
            });
            AddUntilStep("typed once", () => engine.CaretIndex == 1 && keystrokes.Count == 1);
            AddAssert("stamped with the key's time", () => keystrokes[0].Time, () => Is.EqualTo(Math.Round(pressed)));
            AddAssert("correct", () => cell(0).State == CellState.Correct);
            AddAssert("the recorder writes the era", () => frames.Single(f => f.IsConfigExtended).KeyTimeJudgement);
            AddAssert("the run re-derives exactly", reDerivedMatchesLive);
        }

        /// <summary>
        /// The commit arrives two or more frames after its key, with gameplay time moving on in
        /// between (an input-method bus handing the text back late). The engine is held at the key's
        /// time while the press waits, so the press is judged at that time on the engine as it stood
        /// then, which is exactly what playback re-derives from the recorded frame.
        /// </summary>
        [Test]
        public void TestACommitFramesLateIsJudgedAtItsKeyTime()
        {
            double pressed = 0;
            double arrived = 0;

            load();

            holdThePairingGrace();
            pressAwaitingItsCommit(Key.A, () => pressed = handler.Time.Current);
            waitFramesAfter(() => pressed);
            AddStep("the OS commits 'a' now", () =>
            {
                arrived = handler.Time.Current;
                os.Text("a");
            });
            AddUntilStep("typed once", () => engine.CaretIndex == 1 && keystrokes.Count == 1);
            releaseThePairingGrace();
            AddAssert("gameplay time moved on before the commit came", () => Math.Round(arrived), () => Is.GreaterThan(Math.Round(pressed)));
            AddAssert("stamped with the key's time, not the commit's", () => keystrokes[0].Time, () => Is.EqualTo(Math.Round(pressed)));
            AddAssert("correct", () => cell(0).State == CellState.Correct);
            AddAssert("the run re-derives exactly", reDerivedMatchesLive);
        }

        /// <summary>
        /// The engine never moves backwards for a waiting press: a LATER key that advances the engine
        /// before the commit arrives (here a Backspace with nothing to erase) moves the press's
        /// judgement to that key's time, so the frames stay in time order and each one re-derives.
        /// </summary>
        [Test]
        public void TestAPressOvertakenByALaterKeyIsJudgedAtThatKey()
        {
            double pressed = 0;
            double overtaken = 0;

            load();

            holdThePairingGrace();
            pressAwaitingItsCommit(Key.A, () => pressed = handler.Time.Current);
            waitFramesAfter(() => pressed);
            AddStep("press Backspace (nothing to erase), and the OS commits 'a'", () =>
            {
                overtaken = handler.Time.Current;
                InputManager.Key(Key.BackSpace);
                os.Text("a");
            });
            AddUntilStep("typed once", () => engine.CaretIndex == 1 && keystrokes.Count == 1);
            releaseThePairingGrace();
            AddAssert("the later key moved the engine on", () => Math.Round(overtaken), () => Is.GreaterThan(Math.Round(pressed)));
            AddAssert("stamped with the later key's time", () => keystrokes[0].Time, () => Is.EqualTo(Math.Round(overtaken)));
            AddAssert("the run re-derives exactly", reDerivedMatchesLive);
        }

        /// <summary>
        /// A run without the era (every replay recorded before it, and a live engine with the flag
        /// cleared) keeps the pairing-time stamp: the late press is typed at the frame its commit was
        /// paired in, and the recorder writes the bit clear.
        /// </summary>
        [Test]
        public void TestBeforeTheEraALateCommitIsTypedAtItsOwnTime()
        {
            double pressed = 0;
            double arrived = 0;

            load();

            AddStep("leave the era", () => engine.KeyTimeJudgement = false);
            holdThePairingGrace();
            pressAwaitingItsCommit(Key.A, () => pressed = handler.Time.Current);
            waitFramesAfter(() => pressed);
            AddStep("the OS commits 'a' now", () =>
            {
                arrived = handler.Time.Current;
                os.Text("a");
            });
            AddUntilStep("typed once", () => engine.CaretIndex == 1 && keystrokes.Count == 1);
            releaseThePairingGrace();
            AddAssert("stamped when the pairing resolved", () => keystrokes[0].Time, () => Is.GreaterThanOrEqualTo(Math.Round(arrived)));
            AddAssert("the recorder writes the bit clear", () => !frames.Single(f => f.IsConfigExtended).KeyTimeJudgement);
            AddAssert("the run re-derives exactly", reDerivedMatchesLive);
        }

        /// <summary>
        /// THE HOLD. The key goes down inside a line (with room to spare: the headless clock can move
        /// a couple of hundred milliseconds a frame) and its commit arrives only after the line's
        /// seal deadline (its end plus the drag grace) has passed. The engine ticker holds the engine
        /// at the key's time while the press waits, so the press lands on its cell; without the hold
        /// the engine would seal the line first, the cell would be a Miss, and the press would land
        /// on the next line, which no replay of the recorded frame could reproduce.
        /// </summary>
        [Test]
        public void TestAPressWaitingAcrossItsLinesSealIsJudgedBeforeIt()
        {
            double pressed = 0;

            AddStep("a line ending at 3000", () => sealingMap = true);
            load();

            holdThePairingGrace();
            AddUntilStep("press A well before the line ends, committing nothing yet", () =>
            {
                if (handler.Time.Current < sealing_line_end - 1500)
                    return false;

                pressed = handler.Time.Current;
                os.CommitOnNextPress(null);
                InputManager.Key(Key.A);
                return true;
            });
            AddUntilStep("the song passes the line's seal deadline", () => handler.Time.Current > sealing_line_end + TypingEngine.FLETCHER_DRAG_GRACE_MS + 200);
            AddStep("the OS commits 'a' now", () => os.Text("a"));
            AddUntilStep("typed once", () => keystrokes.Count == 1);
            releaseThePairingGrace();
            AddAssert("the key went down inside the line", () => Math.Round(pressed), () => Is.LessThan(sealing_line_end));
            AddAssert("stamped with the key's time", () => keystrokes[0].Time, () => Is.EqualTo(Math.Round(pressed)));
            AddAssert("the cell is correct, not sealed as a miss", () => cell(0).State == CellState.Correct);
            AddAssert("the run re-derives exactly, the seal included", () => reDerivedMatchesLiveAt(handler.Time.Current));
        }

        [Test]
        public void TestTheSpacebarTypesItsSpaceEvenWhenNoneIsCommitted()
        {
            load();

            press(Key.A);
            press(Key.B);
            AddStep("press space, committing nothing", () =>
            {
                os.CommitOnNextPress(null);
                InputManager.Key(Key.Space);
            });
            AddUntilStep("the key's fallback typed the space", () => engine.CaretIndex == 3 && cell(2).State == CellState.Correct);
            AddAssert("one space frame", () => keystrokes.Count(f => f.Character == ' ') == 1);
        }

        /// <summary>
        /// On a platform that commits no space as text, the spacebar's fallback can only fire once
        /// the pairing has given up on a commit (<see cref="TypeBeatPlayfield.PAIRING_GRACE_MS"/> of
        /// real time), but the space is judged at the time its key went down, not that much later.
        /// </summary>
        [Test]
        public void TestASpaceWithNoCommitIsJudgedAtItsKeyTime()
        {
            double pressed = 0;
            double typed = 0;

            load();

            press(Key.A);
            press(Key.B);
            pressAwaitingItsCommit(Key.Space, () => pressed = handler.Time.Current);
            AddUntilStep("the key's fallback typed the space", () =>
            {
                typed = handler.Time.Current;
                return engine.CaretIndex == 3 && cell(2).State == CellState.Correct;
            });
            AddAssert("the fallback waited out the grace", () => typed - pressed, () => Is.GreaterThanOrEqualTo(TypeBeatPlayfield.PAIRING_GRACE_MS / 2));
            AddAssert("stamped with the key's time", () => keystrokes.Single(f => f.Character == ' ').Time, () => Is.EqualTo(Math.Round(pressed)));
            AddAssert("the run re-derives exactly", reDerivedMatchesLive);
        }

        [Test]
        public void TestShiftedDigitIsTheDigitWithoutLiterate()
        {
            load();

            typeToTheDigit();
            AddStep("Shift+1 (commits '!')", () =>
            {
                InputManager.PressKey(Key.LShift);
                InputManager.Key(Key.Number1);
                InputManager.ReleaseKey(Key.LShift);
            });
            AddAssert("the default surface typed the digit", () => cell(7).State == CellState.Correct && cell(7).TypedChar == '1');
            AddAssert("the run re-derives exactly", reDerivedMatchesLive);
        }

        [Test]
        public void TestUnderLiterateTheOsMarkIsTyped()
        {
            load(new TypeBeatModLiterate());

            typeToTheDigit();
            press(Key.Number1);
            AddStep("Shift+1 (commits '!')", () =>
            {
                InputManager.PressKey(Key.LShift);
                InputManager.Key(Key.Number1);
                InputManager.ReleaseKey(Key.LShift);
            });
            AddAssert("'1' then '!' landed", () => cell(7).State == CellState.Correct && cell(8).State == CellState.Correct && cell(8).TypedChar == '!');
            AddAssert("the run re-derives exactly", reDerivedMatchesLive);
        }

        [Test]
        public void TestAltGrCharactersType()
        {
            load();

            AddStep("AltGr+A commits 'ą' (Polish)", () =>
            {
                os.CommitOnNextPress("ą");
                InputManager.PressKey(Key.LControl);
                InputManager.PressKey(Key.LAlt);
                InputManager.Key(Key.A);
                InputManager.ReleaseKey(Key.LAlt);
                InputManager.ReleaseKey(Key.LControl);
            });
            AddAssert("it typed 'a'", () => engine.CaretIndex == 1 && cell(0).State == CellState.Correct);

            AddStep("Ctrl+B, a shortcut, commits nothing", () =>
            {
                InputManager.PressKey(Key.LControl);
                InputManager.Key(Key.B);
                InputManager.ReleaseKey(Key.LControl);
            });
            AddAssert("nothing typed", () => engine.CaretIndex == 1);
        }

        /// <summary>
        /// THE TIMING HARNESS (docs/drafts/0383-text-input-timing.md). Each step is one SDL pump as
        /// the framework delivers it: KEY_DOWN for A, the TEXT_INPUT it produced, then KEY_DOWN for
        /// Backspace, which is still typed by its key. The text-typed 'a' and the key-typed backspace
        /// are recorded at the times the playfield stamped them, so their difference is exactly what
        /// moving characters from the key path to the text path costs in timestamp, through the real
        /// playfield, recorder and replay frames. Measured: 0 ms on every press.
        /// </summary>
        [Test]
        public void TestTheTextTimestampMatchesTheKeyTimestamp()
        {
            const int presses = 20;
            var differences = new List<double>();

            load();

            for (int i = 0; i < presses; i++)
            {
                AddStep($"pump {i}: A (as text), then Backspace (as a key)", () =>
                {
                    InputManager.Key(Key.A);
                    InputManager.Key(Key.BackSpace);
                });
            }

            AddAssert("every press recorded a pair", () =>
            {
                differences.Clear();

                for (int i = 0; i + 1 < keystrokes.Count; i++)
                {
                    if (keystrokes[i].Character == 'a' && keystrokes[i + 1].IsBackspace)
                        differences.Add(keystrokes[i].Time - keystrokes[i + 1].Time);
                }

                TestContext.Progress.WriteLine($"[0383 timing] pairs={differences.Count} max|text - key|={differences.Select(Math.Abs).DefaultIfEmpty().Max()} ms");
                return differences.Count == presses;
            });
            AddAssert("the text stamp equals the key stamp, every time", () => differences.All(d => d == 0));
        }

        /// <summary>Whether the next load uses <see cref="createSealingBeatmap"/> rather than the default map.</summary>
        private bool sealingMap;

        private const double sealing_line_end = 3000;

        /// <summary>
        /// "a" sung from 1500 on a line that ends at <see cref="sealing_line_end"/>, and a far-away second
        /// line, so the first line seals (end plus drag grace) long before the second one starts.
        /// </summary>
        private static IBeatmap createSealingBeatmap(Beatmap beatmap, RulesetInfo ruleset)
        {
            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.BeatmapInfo.Metadata.Artist = "Test";
            beatmap.BeatmapInfo.Metadata.Title = "TextInputSeal";

            var line = new LyricLine
            {
                RawText = "a",
                StartTime = 0,
                EndTime = sealing_line_end,
                SingEndTime = 2950,
                Units = new[] { new TimedUnit { Text = "a", StartTime = 1500, EndTime = 2950 } },
            };

            var trailing = new LyricLine
            {
                RawText = "g",
                StartTime = 60000,
                EndTime = 120000,
                SingEndTime = 80000,
                Units = new[] { new TimedUnit { Text = "g", StartTime = 75000, EndTime = 80000 } },
            };

            beatmap.HitObjects.Add(new TypeBeatHitObject { StartTime = line.StartTime, LineIndex = 0, Line = line, Granularity = TimingGranularity.Word });
            beatmap.HitObjects.Add(new TypeBeatHitObject { StartTime = trailing.StartTime, LineIndex = 1, Line = trailing, Granularity = TimingGranularity.Word });

            return beatmap;
        }

        private void load(params Mod[] mods)
        {
            AddStep($"load with [{string.Join(' ', mods.Select(m => m.Acronym))}]", () => LoadPlayer(mods));
            AddUntilStep("player loaded", () => Player.IsLoaded && Player.Alpha == 1);
            AddUntilStep("line 0 active", () => engine.ActiveLineIndex == 0);
        }

        /// <summary>One press of <paramref name="key"/>, committing <paramref name="commit"/> instead of its US character when given.</summary>
        private void press(Key key, string? commit = null) => AddStep(commit == null ? $"press {key}" : $"press {key} committing \"{commit}\"", () =>
        {
            if (commit != null)
                os.CommitOnNextPress(commit);

            InputManager.Key(key);
        });

        /// <summary>One press of <paramref name="key"/> that commits nothing, so it waits for a commit; <paramref name="note"/> runs in the same step.</summary>
        private void pressAwaitingItsCommit(Key key, Action note) => AddStep($"press {key}, committing nothing yet", () =>
        {
            note();
            os.CommitOnNextPress(null);
            InputManager.Key(key);
        });

        /// <summary>
        /// Stops the pairing's real-time clock, so a press waiting for a commit the test hands over
        /// frames later cannot outlive its grace however slowly those frames run on a loaded machine.
        /// Gameplay time keeps running.
        /// </summary>
        private void holdThePairingGrace() => AddStep("hold the pairing's real-time grace", () => playfield.FreezePairingClock(true));

        private void releaseThePairingGrace() => AddStep("release the pairing's grace", () => playfield.FreezePairingClock(false));

        /// <summary>
        /// Lets two more frames run, with gameplay time at least 10 ms past <paramref name="since"/>,
        /// so a commit handed over next is at least two frames late while staying inside the
        /// pairing's real-time grace.
        /// </summary>
        private void waitFramesAfter(Func<double> since)
        {
            var seen = new HashSet<double>();

            AddStep("start counting frames", () => seen.Clear());
            AddUntilStep("two frames later", () =>
            {
                seen.Add(handler.Time.Current);
                return seen.Count >= 2 && handler.Time.Current >= since() + 10;
            });
        }

        private void typeAbSpace()
        {
            press(Key.A);
            press(Key.B);
            press(Key.Space);
        }

        private void typeToTheDigit()
        {
            typeAbSpace();
            press(Key.E);
            press(Key.S);
            press(Key.S);
            press(Key.Space);
        }

        /// <summary>The recorded run through the legacy .osr encoding and back, fed to a fresh engine, against the live one.</summary>
        private bool reDerivedMatchesLive() => reDerivedMatchesLiveAt(null);

        /// <summary>As <see cref="reDerivedMatchesLive"/>, with the fresh engine then ticked to <paramref name="time"/> (where the live one has got to) when given.</summary>
        private bool reDerivedMatchesLiveAt(double? time)
        {
            var dummy = new Beatmap();
            var map = Player.GameplayState.Beatmap;
            var replayed = TypeBeatReplayScorer.CreateEngine(map, map.HitObjects.OfType<TypeBeatHitObject>().ToList(), Player.GameplayState.Mods, RateWindowRule.ScaledByRate);

            foreach (var frame in frames)
            {
                var legacy = frame.ToLegacy(dummy);
                double storedTime = Math.Round(legacy.Time);

                var decoded = new TypeBeatReplayFrame();
                decoded.FromLegacy(new LegacyReplayFrame(storedTime, legacy.MouseX, legacy.MouseY, legacy.ButtonState), dummy);
                decoded.Time = storedTime;

                ReplayEngineFeed.Apply(replayed, decoded);
            }

            if (time is double t)
                replayed.Update(t);

            return engine.Lines[0].Cells.Zip(replayed.Lines[0].Cells)
                         .All(pair => pair.First.State == pair.Second.State
                                      && pair.First.TypedChar == pair.Second.TypedChar
                                      && Nullable.Equals(pair.First.JudgedDelta, pair.Second.JudgedDelta))
                   && replayed.CaretIndex == engine.CaretIndex
                   && replayed.Score == engine.Score
                   && replayed.Combo == engine.Combo
                   && replayed.Mistypes == engine.Mistypes;
        }
    }
}
