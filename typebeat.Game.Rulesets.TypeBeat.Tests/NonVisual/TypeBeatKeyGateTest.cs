// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.UI;
using osuTK.Input;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The key handler's routing (<see cref="TypeBeatKeyGate"/>), arm by arm and in the order the arms
    /// are asked: each case puts a press where two arms could both claim it and checks that the one
    /// earlier in the order does. The browser side of the same gate is pinned in typebeat-web
    /// (KeyHandlerOrderLiveParityTest), which drives this gate rather than a copy of it.
    /// </summary>
    public class TypeBeatKeyGateTest
    {
        private static readonly TypeBeatKeyPlay latin = new TypeBeatKeyPlay(Polyglot: false, Literate: false, UsesJapaneseRomaji: false, ImeComposing: false);

        private static readonly TypeBeatKeyPlay literate = latin with { Literate = true };

        private static readonly TypeBeatKeyPlay ime = latin with { Polyglot = true };

        /// <summary>An active, unfinished line the song is on and the player has typed into, under the shipped live rules.</summary>
        private static readonly TypeBeatKeyLine typing = new TypeBeatKeyLine(
            LineOpen: true,
            FletcherEnabled: true,
            SongIsOnTheCaretsLine: true,
            ActiveLineUntouched: false,
            AllowWrongInput: true,
            RetypeSelection: false,
            IsLineComplete: false,
            ManualNewlines: true,
            NewlineOnTypedLetter: true);

        private static readonly TypeBeatKeyLine finished = typing with { IsLineComplete = true };

        private static TypeBeatKeyPress press(Key key, TypeBeatAction? gesture = null, bool repeat = false, bool shift = false, bool control = false, bool alt = false, bool super = false)
            => new TypeBeatKeyPress(key, repeat, shift, control, alt, super, gesture);

        private static TypeBeatKeyOutcome decide(TypeBeatKeyPlay play, TypeBeatKeyLine line, TypeBeatKeyPress press) => TypeBeatKeyGate.Decide(play, line, press);

        [TestCase(Key.BackSpace)]
        [TestCase(Key.Space)]
        [TestCase(Key.Enter)]
        [TestCase(Key.KeypadEnter)]
        [TestCase(Key.Escape)]
        public void AnOpenCompositionsKeysAreTheImesBeforeAnythingElse(Key key)
        {
            var route = TypeBeatKeyGate.Route(ime with { ImeComposing = true }, press(key, TypeBeatAction.SkipLine));

            Assert.That(route.Settled, Is.EqualTo(TypeBeatKeyOutcome.Swallow), "settled before the engine is advanced");
            Assert.That(route.FreshTextPress, Is.False);
            Assert.That(TypeBeatKeyGate.Decide(typing with { LineOpen = false }, route), Is.EqualTo(TypeBeatKeyOutcome.Swallow), "even with no line open");
        }

        [Test]
        public void ALetterIsNotTheCompositionsAndTheCompositionNeedsPolyglot()
        {
            Assert.That(TypeBeatKeyGate.Route(ime with { ImeComposing = true }, press(Key.A)).Settled, Is.Null);
            Assert.That(TypeBeatKeyPlay.Of(new TypingEngine(map()), imeComposing: true).ImeComposing, Is.False, "a Latin play has no composition to protect");
        }

        [Test]
        public void TypingAlwaysWinsOverAGestureReboundOntoATypeableKey()
        {
            Assert.That(decide(latin, typing, press(Key.A, TypeBeatAction.SelectBackToTypo)), Is.EqualTo(TypeBeatKeyOutcome.Type), "a bare letter types");
            Assert.That(decide(latin, typing, press(Key.A, TypeBeatAction.SelectBackToTypo, shift: true)), Is.EqualTo(TypeBeatKeyOutcome.Type), "so does a shifted one");
            Assert.That(decide(latin, typing, press(Key.A, TypeBeatAction.SelectBackToTypo, control: true)), Is.EqualTo(TypeBeatKeyOutcome.SelectBackToTypo), "a chord is never shadowed");
            Assert.That(decide(latin, typing, press(Key.Comma, TypeBeatAction.EraseWord)), Is.EqualTo(TypeBeatKeyOutcome.Erase), "a punctuation position is not typeable without Literate");
            Assert.That(decide(literate, typing, press(Key.Comma, TypeBeatAction.EraseWord)), Is.EqualTo(TypeBeatKeyOutcome.Type), "and is under it");
        }

        [Test]
        public void AnAltGrChordIsACharacterNeverAGesture()
        {
            var route = TypeBeatKeyGate.Route(latin, press(Key.A, TypeBeatAction.SelectBackToTypo, control: true, alt: true));

            Assert.That(route.Gesture, Is.Null);
            Assert.That(route.Settled, Is.EqualTo(TypeBeatKeyOutcome.FallThroughTyping), "it falls through, and what it commits is typed");

            Assert.That(decide(ime, typing, press(Key.A, TypeBeatAction.SelectBackToTypo, control: true, alt: true)), Is.EqualTo(TypeBeatKeyOutcome.SelectBackToTypo),
                "a Polyglot play's keys stay as they were");
        }

        [Test]
        public void AChordThatIsNoGestureFallsThroughBeforeTheEngineIsAsked()
        {
            Assert.That(TypeBeatKeyGate.Route(latin, press(Key.C, control: true)).Settled, Is.EqualTo(TypeBeatKeyOutcome.FallThrough), "Ctrl commits nothing");
            Assert.That(TypeBeatKeyGate.Route(latin, press(Key.A, super: true)).Settled, Is.EqualTo(TypeBeatKeyOutcome.FallThrough), "nor does Command");
            Assert.That(TypeBeatKeyGate.Route(latin, press(Key.Q, alt: true)).Settled, Is.EqualTo(TypeBeatKeyOutcome.FallThroughTyping), "Option does");
            Assert.That(TypeBeatKeyGate.Route(latin, press(Key.BackSpace, TypeBeatAction.EraseWord, control: true)).Settled, Is.Null, "a gesture goes on to the engine");
        }

        [Test]
        public void NothingOpenMakesEveryKeyInertAheadOfTheGestures()
        {
            var closed = typing with { LineOpen = false };

            Assert.That(decide(latin, closed, press(Key.A)), Is.EqualTo(TypeBeatKeyOutcome.FallThrough));
            Assert.That(decide(latin, closed, press(Key.BackSpace)), Is.EqualTo(TypeBeatKeyOutcome.FallThrough));
            Assert.That(decide(latin, closed, press(Key.Enter, TypeBeatAction.SkipLine)), Is.EqualTo(TypeBeatKeyOutcome.FallThrough));
        }

        [Test]
        public void TheFirstLinesHeadStartOpensTheLine()
        {
            var engine = new TypingEngine(map()) { FirstLineLeadIn = true };
            engine.Update(2700);

            Assert.That(engine.LineIsActive, Is.False);
            Assert.That(TypeBeatKeyLine.Of(engine, 2700, false).LineOpen, Is.True, "a press inside the head start is typed");
            Assert.That(TypeBeatKeyLine.Of(engine, 2699, false).LineOpen, Is.False, "and one before it falls through");
        }

        [Test]
        public void AnUnpinnedCaretsSpaceFallsThroughOnlyOnAnUntouchedLineTheSongIsNotOn()
        {
            var parked = typing with { SongIsOnTheCaretsLine = false, ActiveLineUntouched = true };

            Assert.That(decide(latin, parked, press(Key.Space)), Is.EqualTo(TypeBeatKeyOutcome.FallThrough));
            Assert.That(decide(latin, parked, press(Key.A)), Is.EqualTo(TypeBeatKeyOutcome.Type), "only the spacebar");
            Assert.That(decide(latin, parked with { ActiveLineUntouched = false }, press(Key.Space)), Is.EqualTo(TypeBeatKeyOutcome.TypeSpace), "one keystroke in, it types");
            Assert.That(decide(latin, parked with { SongIsOnTheCaretsLine = true }, press(Key.Space)), Is.EqualTo(TypeBeatKeyOutcome.TypeSpace), "and wherever the song is on the line");
            Assert.That(decide(latin, parked with { FletcherEnabled = false }, press(Key.Space)), Is.EqualTo(TypeBeatKeyOutcome.TypeSpace), "a pinned caret never needed it");
            Assert.That(decide(latin, parked, press(Key.Space, TypeBeatAction.SkipLine, control: true)), Is.EqualTo(TypeBeatKeyOutcome.SkipLine), "a gesture on a Space chord is not swallowed by it");
        }

        [Test]
        public void TheEraseArmsComeBeforeTheFinishedLine()
        {
            Assert.That(decide(latin, finished, press(Key.BackSpace)), Is.EqualTo(TypeBeatKeyOutcome.Erase), "backspacing at line end keeps working");
            Assert.That(decide(latin, finished, press(Key.BackSpace, TypeBeatAction.EraseWord, control: true)), Is.EqualTo(TypeBeatKeyOutcome.Erase));
            Assert.That(decide(latin, finished, press(Key.A, TypeBeatAction.SelectBackToTypo, control: true)), Is.EqualTo(TypeBeatKeyOutcome.SelectBackToTypo));
            Assert.That(decide(latin, finished, press(Key.Enter, TypeBeatAction.SkipLine)), Is.EqualTo(TypeBeatKeyOutcome.SkipLine), "the skip, not the newline");
        }

        [Test]
        public void GatekeeperSwallowsAPlainBackspaceUnlessASelectionIsLive()
        {
            var gatekeeper = typing with { AllowWrongInput = false };

            Assert.That(decide(latin, gatekeeper, press(Key.BackSpace)), Is.EqualTo(TypeBeatKeyOutcome.Swallow));
            Assert.That(decide(latin, gatekeeper with { RetypeSelection = true }, press(Key.BackSpace)), Is.EqualTo(TypeBeatKeyOutcome.Erase));
            Assert.That(decide(latin, gatekeeper, press(Key.A, TypeBeatAction.SelectBackToTypo, control: true)), Is.EqualTo(TypeBeatKeyOutcome.SelectBackToTypo), "the select is not gated on it");
        }

        [Test]
        public void AFinishedLineTakesTheManualAndTheTypedThroughNewline()
        {
            Assert.That(decide(latin, finished, press(Key.Space)), Is.EqualTo(TypeBeatKeyOutcome.NewLine));
            Assert.That(decide(latin, finished, press(Key.Enter)), Is.EqualTo(TypeBeatKeyOutcome.NewLine), "an Enter no gesture is bound to");
            Assert.That(decide(latin, finished, press(Key.KeypadEnter)), Is.EqualTo(TypeBeatKeyOutcome.NewLine));
            Assert.That(decide(latin, finished, press(Key.A)), Is.EqualTo(TypeBeatKeyOutcome.TypedNewLine));
            Assert.That(decide(latin, finished, press(Key.A, repeat: true)), Is.EqualTo(TypeBeatKeyOutcome.FallThrough), "a repeat is no fresh press");
            Assert.That(decide(ime, finished, press(Key.A)), Is.EqualTo(TypeBeatKeyOutcome.FallThrough), "the IME's letters are its commits'");

            var automatic = finished with { ManualNewlines = false, NewlineOnTypedLetter = false };

            Assert.That(decide(latin, automatic, press(Key.Space)), Is.EqualTo(TypeBeatKeyOutcome.FallThrough), "the automatic arm falls through to the skip");
            Assert.That(decide(latin, automatic, press(Key.A)), Is.EqualTo(TypeBeatKeyOutcome.FallThrough));
            Assert.That(decide(latin, finished with { NewlineOnTypedLetter = false }, press(Key.A)), Is.EqualTo(TypeBeatKeyOutcome.FallThrough));
        }

        [Test]
        public void ALiveSelectionSuspendsTheFinishedLine()
        {
            var selected = finished with { RetypeSelection = true };

            Assert.That(decide(latin, selected, press(Key.A)), Is.EqualTo(TypeBeatKeyOutcome.Type));
            Assert.That(decide(latin, selected, press(Key.Space)), Is.EqualTo(TypeBeatKeyOutcome.TypeSpace));
        }

        [Test]
        public void APolyglotImePlayTypesOnlyItsSpacebarByKey()
        {
            Assert.That(decide(ime, typing, press(Key.Space)), Is.EqualTo(TypeBeatKeyOutcome.ImeSpace));
            Assert.That(decide(ime, typing, press(Key.Space, repeat: true)), Is.EqualTo(TypeBeatKeyOutcome.Swallow));
            Assert.That(decide(ime, typing, press(Key.A)), Is.EqualTo(TypeBeatKeyOutcome.Swallow));
            Assert.That(decide(ime, typing, press(Key.Comma)), Is.EqualTo(TypeBeatKeyOutcome.Swallow));
            Assert.That(decide(ime, typing, press(Key.BracketLeft)), Is.EqualTo(TypeBeatKeyOutcome.Swallow));
            Assert.That(decide(ime, typing, press(Key.BracketLeft, shift: true)), Is.EqualTo(TypeBeatKeyOutcome.FallThrough));
            Assert.That(decide(ime, typing, press(Key.Tilde)), Is.EqualTo(TypeBeatKeyOutcome.FallThrough));
            Assert.That(decide(ime, typing, press(Key.Tilde, shift: true)), Is.EqualTo(TypeBeatKeyOutcome.Swallow));
            Assert.That(decide(ime, typing, press(Key.F1)), Is.EqualTo(TypeBeatKeyOutcome.FallThrough));
        }

        [Test]
        public void ALatinTypingKey()
        {
            Assert.That(decide(latin, typing, press(Key.A)), Is.EqualTo(TypeBeatKeyOutcome.Type));
            Assert.That(decide(latin, typing, press(Key.Space)), Is.EqualTo(TypeBeatKeyOutcome.TypeSpace));
            Assert.That(decide(latin, typing, press(Key.A, repeat: true)), Is.EqualTo(TypeBeatKeyOutcome.Swallow), "a repeat types nothing and is swallowed while the key would type");
            Assert.That(decide(latin, typing, press(Key.Comma, repeat: true)), Is.EqualTo(TypeBeatKeyOutcome.FallThrough));
            Assert.That(decide(literate, typing, press(Key.Comma, repeat: true)), Is.EqualTo(TypeBeatKeyOutcome.Swallow));
            Assert.That(decide(latin, typing, press(Key.F1)), Is.EqualTo(TypeBeatKeyOutcome.FallThrough), "a key that commits nothing");
        }

        [Test]
        public void TheDefaultSurfaceKeepsTheDigitRowPositional()
        {
            Assert.That(decide(latin, typing, press(Key.Number1, shift: true)), Is.EqualTo(TypeBeatKeyOutcome.PositionalDigit));
            Assert.That(decide(latin, typing, press(Key.Keypad7)), Is.EqualTo(TypeBeatKeyOutcome.PositionalDigit));
            Assert.That(decide(literate, typing, press(Key.Number1, shift: true)), Is.EqualTo(TypeBeatKeyOutcome.Type), "under Literate the OS's mark is typed");
            Assert.That(decide(latin with { Polyglot = true, UsesJapaneseRomaji = true }, typing, press(Key.Number1)), Is.EqualTo(TypeBeatKeyOutcome.Type), "and under Polyglot");
        }

        private static LyricBeatmap map() => new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata { Artist = "Test", Title = "Song", FolderPath = @"X:\nowhere", AudioFileName = "a.mp3" },
            Lines = new[]
            {
                new LyricLine
                {
                    RawText = "ab",
                    StartTime = 3000,
                    EndTime = 4000,
                    SingEndTime = 3800,
                    Units = new[] { new TimedUnit { Text = "ab", StartTime = 3000, EndTime = 3800 } },
                },
            },
            Granularity = TimingGranularity.Line,
        };
    }
}
