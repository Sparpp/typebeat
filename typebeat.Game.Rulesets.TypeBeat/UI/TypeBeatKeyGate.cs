// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using osuTK.Input;

namespace typebeat.Game.Rulesets.TypeBeat.UI
{
    /// <summary>
    /// One key press as the playfield's key handler reads it: the key, the framework's repeat flag,
    /// the modifiers held, and the word-level gesture the user's CURRENT bindings resolve it to
    /// (<see cref="TypeBeatInputManager.ResolveGesture"/>), before any shadowing.
    /// </summary>
    public readonly record struct TypeBeatKeyPress(Key Key, bool Repeat, bool Shift, bool Control, bool Alt, bool Super, TypeBeatAction? Gesture);

    /// <summary>
    /// What the gate knows about the PLAY before the engine is advanced to a press: the input mods,
    /// fixed for the run, and whether an IME composition is open.
    /// </summary>
    public readonly record struct TypeBeatKeyPlay(bool Polyglot, bool Literate, bool UsesJapaneseRomaji, bool ImeComposing)
    {
        /// <summary>Whether characters arrive through the IME (Polyglot outside its Japanese romaji processor).</summary>
        public bool UsesIme => Polyglot && !UsesJapaneseRomaji;

        /// <summary>The play <paramref name="engine"/> runs, with <paramref name="imeComposing"/> as the handler's composition state.</summary>
        public static TypeBeatKeyPlay Of(TypingEngine engine, bool imeComposing)
            => new TypeBeatKeyPlay(engine.Polyglot, engine.Literate, engine.UsesJapaneseRomaji, engine.Polyglot && imeComposing);
    }

    /// <summary>
    /// What the gate reads off the ENGINE, at the press's own instant: read only after the engine has
    /// been advanced to the press (see <see cref="TypeBeatKeyGate"/>), plus whether a retype selection
    /// is live on the playfield.
    /// </summary>
    public readonly record struct TypeBeatKeyLine(
        bool LineOpen,
        bool FletcherEnabled,
        bool SongIsOnTheCaretsLine,
        bool ActiveLineUntouched,
        bool AllowWrongInput,
        bool RetypeSelection,
        bool IsLineComplete,
        bool ManualNewlines,
        bool NewlineOnTypedLetter)
    {
        /// <summary><paramref name="engine"/> as it stands at <paramref name="time"/>, which it must already have been advanced to.</summary>
        public static TypeBeatKeyLine Of(TypingEngine engine, double time, bool retypeSelection)
            => new TypeBeatKeyLine(
                LineOpen: engine.LineIsActive || engine.FirstLineTypingOpensAt(time),
                FletcherEnabled: engine.FletcherEnabled,
                SongIsOnTheCaretsLine: engine.SongIsOnTheCaretsLine,
                ActiveLineUntouched: engine.ActiveLineUntouched,
                AllowWrongInput: engine.AllowWrongInput,
                RetypeSelection: retypeSelection,
                IsLineComplete: engine.IsLineComplete,
                ManualNewlines: engine.ManualNewlines,
                NewlineOnTypedLetter: engine.NewlineOnTypedLetter);
    }

    /// <summary>What the key handler does with a press (see <see cref="TypeBeatKeyGate"/>).</summary>
    public enum TypeBeatKeyOutcome
    {
        /// <summary>Swallowed, with no engine call.</summary>
        Swallow,

        /// <summary>Not swallowed, so it reaches the global bindings (the instrumental skips, the framework shortcuts), with no engine call.</summary>
        FallThrough,

        /// <summary>A modifier chord that is not a gesture: it falls through, but whatever it COMMITS is still typed.</summary>
        FallThroughTyping,

        /// <summary>A backspace, plain or <see cref="TypeBeatAction.EraseWord"/> (per the routed gesture). Swallowed.</summary>
        Erase,

        /// <summary><see cref="TypeBeatAction.SelectBackToTypo"/>. Swallowed, effective or not.</summary>
        SelectBackToTypo,

        /// <summary><see cref="TypeBeatAction.SkipLine"/>: swallowed only when the engine's skip is effective, otherwise it falls through.</summary>
        SkipLine,

        /// <summary>The manual newline on a finished line, Space or Enter: swallowed only when the engine takes it, otherwise it falls through.</summary>
        NewLine,

        /// <summary>The typed-through newline on a finished line: the letter's commit is typed, and the press is swallowed exactly when it typed.</summary>
        TypedNewLine,

        /// <summary>A Polyglot IME play's spacebar, typed by its key. Swallowed.</summary>
        ImeSpace,

        /// <summary>The default surface's positional digit (<see cref="TypingKeys.TryPositionalDigit"/>), typed by its key. Swallowed.</summary>
        PositionalDigit,

        /// <summary>A Latin typing key: its commit is typed, and it is swallowed when <see cref="TypingKeys.AlwaysSwallowed"/> or when it typed.</summary>
        Type,

        /// <summary>As <see cref="Type"/> for the spacebar, which types its space by the key when the platform commits none.</summary>
        TypeSpace,
    }

    /// <summary>
    /// The routing of the playfield's key handler (<c>TypeBeatKeyHandler.OnKeyDown</c>) as a pure
    /// function: which arm a press takes, decided in the one order the handler has always used. The
    /// handler carries out what this answers, and the cross-repo key-order parity test drives this
    /// same gate against the browser's <c>routeKeyDown</c>, so a change to an arm here is a change
    /// on both sides of that test.
    ///
    /// <para>Two stages, because the engine is advanced to the press in between:
    /// <see cref="Route"/> settles the presses that never reach the engine (an open IME
    /// composition's keys, a modifier chord that is not a gesture) and shadows the gesture, and
    /// <see cref="Decide(TypeBeatKeyLine, TypeBeatKeyRoute)"/> takes the rest on the engine as it
    /// stands at the press. Nothing the first stage reads changes when the engine is advanced, so
    /// <see cref="Decide(TypeBeatKeyPlay, TypeBeatKeyLine, TypeBeatKeyPress)"/> runs both on one
    /// snapshot for a caller that has advanced already.</para>
    /// </summary>
    public static class TypeBeatKeyGate
    {
        /// <summary>
        /// The first stage, before the engine is advanced: either a settled outcome, or the press
        /// with its gesture shadowed and whether it is a fresh text press.
        /// </summary>
        public static TypeBeatKeyRoute Route(TypeBeatKeyPlay play, TypeBeatKeyPress press)
            => imeComposition(play, press) ?? routeGesture(play, press);

        /// <summary>
        /// The second stage, on the engine advanced to the press: the arm the press takes. The order
        /// of the steps is load-bearing (each step's comments say why it sits where it does).
        /// </summary>
        public static TypeBeatKeyOutcome Decide(TypeBeatKeyLine line, TypeBeatKeyRoute route)
            => route.Settled
               ?? inertLine(line, route)
               ?? gesture(line, route)
               ?? finishedLine(line, route)
               ?? typingKey(route);

        /// <summary>Both stages on one snapshot, for a caller that has already advanced the engine to the press.</summary>
        public static TypeBeatKeyOutcome Decide(TypeBeatKeyPlay play, TypeBeatKeyLine line, TypeBeatKeyPress press)
            => Decide(line, Route(play, press));

        /// <summary>
        /// Whether a key counts as a TYPING key before its commit is known: for the gesture
        /// shadowing (typing always wins) and for a framework repeat, which commits nothing of
        /// its own. <see cref="TypingKeys.AlwaysSwallowed"/>, plus a punctuation position under Literate,
        /// where marks are cells.
        /// </summary>
        public static bool TypesHere(TypeBeatKeyPlay play, Key key)
            => TypingKeys.AlwaysSwallowed(key) || ((play.Literate || play.UsesJapaneseRomaji) && TypingKeys.IsPunctuationPosition(key));

        private static TypeBeatKeyRoute? imeComposition(TypeBeatKeyPlay play, TypeBeatKeyPress press)
        {
            // POLYGLOT (backlog 331): while an IME composition is open, the keys that edit it are the
            // IME's (a backspace shortens the composition, a space converts it, enter commits it), so
            // the engine sees none of them. Swallowed, so none reaches a global binding either.
            if (play.ImeComposing
                && (press.Key == Key.BackSpace || press.Key == Key.Space || press.Key == Key.Enter || press.Key == Key.KeypadEnter || press.Key == Key.Escape))
                return new TypeBeatKeyRoute(play, press, TypeBeatKeyOutcome.Swallow, press.Gesture, false);

            return null;
        }

        private static TypeBeatKeyRoute routeGesture(TypeBeatKeyPlay play, TypeBeatKeyPress press)
        {
            // A FRESH press of a key that commits text (backlog 383). The framework's own repeats
            // are not presses, and the OS's repeats never arrive as key events at all; see
            // pairing_grace_ms for how their commits are dropped. A Ctrl or Command chord is not
            // one either: no platform commits text for it (SDL drops the control characters), so
            // letting it wait would only hand it the next key's character. Ctrl+Alt stays one,
            // because that is how Windows reports AltGr, and Option (Alt) alone on macOS commits.
            bool freshTextPress = !play.UsesIme && !press.Repeat && TypingKeys.CommitsText(press.Key)
                                  && !press.Super && (!press.Control || press.Alt);

            // The gesture was resolved before anything else (backlog 183), so a gesture rebound onto
            // a key this handler would otherwise own outright (Backspace, Shift+Backspace) still
            // reaches its gesture.
            var gesture = press.Gesture;

            // TYPING ALWAYS WINS. A gesture rebound onto a bare (or shifted) typeable key is
            // shadowed for as long as the key would type, and types. Nothing else is survivable:
            // the whole lyric surface is typeable, so a letter that silently stopped typing
            // mid-run could not be recovered from without leaving gameplay. No modifier chord
            // types, so no modifier chord is ever shadowed. Since backlog 383 "would type" is the
            // key's place in the block (the OS decides the character itself): a letter, a digit
            // or the spacebar always, a punctuation position under Literate, where marks are cells.
            if (gesture != null
                && !press.Control && !press.Alt && !press.Super
                && TypesHere(play, press.Key))
            {
                gesture = null;
            }

            // ...and an ALTGR chord is a character, never a gesture (backlog 383). Windows reports
            // AltGr as Ctrl+Alt, and the gestures match their modifiers loosely
            // (TypeBeatInputManager's matching mode), so without this a Polish AltGr+A ('ą') would
            // select back to the last typo instead of typing its 'a'. A Latin play only: Polyglot's
            // characters come through the IME path, and its keys stay as they were.
            if (gesture != null && freshTextPress && press.Control && press.Alt)
                gesture = null;

            // Let framework shortcuts (every modifier combo that is not a bound gesture) fall
            // through. Super is in the list for macOS, where it is the platform's editing
            // modifier: the two recovery gestures are chorded with it there by default
            // (TypeBeatRuleset.RecoveryGestureModifier), so leaving it out would have every
            // OTHER Command chord fall through to the typing path and land in the lyric.
            //
            // ...but whatever such a chord COMMITS is still typed (backlog 383): on Windows
            // AltGr arrives as Ctrl+Alt, and on macOS Option is Alt, and both are how a
            // layout reaches the characters it has no plain key for ('@', '[', a Polish 'ę').
            // A real shortcut commits nothing, so its press simply expires.
            TypeBeatKeyOutcome? settled = null;

            if ((press.Control || press.Alt || press.Super) && gesture == null)
                settled = freshTextPress ? TypeBeatKeyOutcome.FallThroughTyping : TypeBeatKeyOutcome.FallThrough;

            return new TypeBeatKeyRoute(play, press, settled, gesture, freshTextPress);
        }

        private static TypeBeatKeyOutcome? inertLine(TypeBeatKeyLine line, TypeBeatKeyRoute route)
        {
            // While the engine has no active line (pre-roll, a dead zone, or after the final
            // line) typing is inert, so DON'T swallow the key; let it fall through to global
            // key bindings so Space reaches GlobalAction.SkipCutscene and the intro / mid-song
            // instrumental skip overlays can act.
            // ...and the map's FIRST line is the one exception: it has no line before it to rush
            // from, so a press inside its head start (FIRST_LINE_LEAD_MS) is consumed and opens it,
            // rather than falling through to a global binding for the whole second before its word.
            if (!line.LineOpen)
                return TypeBeatKeyOutcome.FallThrough;

            // An UNPINNED caret (the default since backlog 208) parks at the head of the next
            // line the instant you finish one, so a line stays "active" straight through an
            // instrumental gap and Space would be eaten as a (wrong, combo-breaking) keystroke
            // instead of reaching the mid-song skip overlay. The pinned game never needed this:
            // there the caret sits past the end of the line and the IsLineComplete fall-through
            // further down carries it. Narrowly restore the fall-through: only Space, only while
            // the SONG is not asking for characters on the caret's own line, and only before the
            // player has started the parked line. One keystroke into the line, or anywhere the
            // song is actually playing the line the caret is on, Space is a typing key again, so
            // rushing into the next line is never blocked.
            //
            // SongIsOnTheCaretsLine rather than SongWindowOpen, and that distinction is the
            // whole feature on a REAL map: decoder-built line windows are contiguous, so through
            // a twelve-second instrumental the playhead is still inside the finished line's
            // window and SongWindowOpen never goes false. It only did on synthetic maps with a
            // hole between lines.
            //
            // (Gated on there being no bound gesture on this press, so binding one onto a Space
            // chord is not swallowed by the skip fall-through.)
            if (line.FletcherEnabled && route.Gesture == null && route.Press.Key == Key.Space && !line.SongIsOnTheCaretsLine && line.ActiveLineUntouched)
                return TypeBeatKeyOutcome.FallThrough;

            return null;
        }

        private static TypeBeatKeyOutcome? gesture(TypeBeatKeyLine line, TypeBeatKeyRoute route)
        {
            // Erase, plain or by word. Handled BEFORE the line-complete fall-through: backspacing at
            // line end must keep working (it is how typed-through wrong chars get fixed in
            // allow-wrong-input mode).
            if (route.Gesture == TypeBeatAction.EraseWord || (route.Gesture == null && route.Press.Key == Key.BackSpace))
            {
                // Gatekeeper rejects wrong keys, including a space inside a word, so a plain
                // backspace has nothing to undo. A retype selection can still be consumed.
                return !line.AllowWrongInput && !line.RetypeSelection ? TypeBeatKeyOutcome.Swallow : TypeBeatKeyOutcome.Erase;
            }

            if (route.Gesture == TypeBeatAction.SelectBackToTypo)
                return TypeBeatKeyOutcome.SelectBackToTypo;

            if (route.Gesture == TypeBeatAction.SkipLine)
                return TypeBeatKeyOutcome.SkipLine;

            return null;
        }

        private static TypeBeatKeyOutcome? finishedLine(TypeBeatKeyLine line, TypeBeatKeyRoute route)
        {
            // The active line is fully typed: the engine is inert for character keys
            // (ProcessKey no-ops at line end), so let them fall through too. This is the state
            // the player holds for the ENTIRE length of a real instrumental gap; the decoder
            // keeps the previous line's window open (and thus active) until the next line
            // starts, so without this fall-through Space could never reach the mid-song skip
            // overlay on any real map. While the line is active and INCOMPLETE every typeable
            // key (Space included) is still consumed for typing, so a skip can never eat a
            // live keystroke.
            //
            // A live retype SELECTION suspends that fall-through (backlog 182): collapsing it
            // re-opens the cells it covers, so the key consuming it is a typing key again rather
            // than a skip, even though the line reads complete at the instant it arrives.
            //
            // A caret the LINE SKIP parked (backlog 241) is covered by this arm without widening
            // it, and that is the whole reason the skip parks the caret rather than inventing a
            // state of its own: IsLineComplete asks where the CARET is, not whether the line was
            // typed out, so a skipped line reaches here exactly as a finished one does. The
            // narrow Space carve-out further up does NOT cover them, since ActiveLineUntouched is
            // false the moment they typed one character of the line they gave up, so this is the
            // arm a player who pressed Enter into an instrumental gap leaves through, and
            // TestSceneTypeBeatInstrumentalSkip drives that end to end.
            if (!line.IsLineComplete || line.RetypeSelection)
                return null;

            // MANUAL NEWLINES: a finished line is the PLAYER's to close, so the two keys that
            // close it are LIVE input rather than a fall-through to the global bindings. They
            // go through the SAME two engine entry points the replay feed feeds a recorded
            // run through (ProcessKey for the space, ProcessEnter for enter), so a re-derived
            // run lands on the same line by construction rather than by a second
            // implementation that has to agree with this one. A press the entry window has
            // not opened for is refused, and then falls through exactly as this state always
            // did; a LETTER keeps falling through untouched, which is what keeps the mid-song
            // skip overlay reachable through an instrumental gap.
            //
            // The newline space stays on the KEY (backlog 383): whether it is taken decides
            // whether the press falls through, and only the engine can answer that, now.
            var key = route.Press.Key;

            if (line.ManualNewlines && (key == Key.Space || key == Key.Enter || key == Key.KeypadEnter))
                return TypeBeatKeyOutcome.NewLine;

            // AND BY TYPING (the typed-through era bit): a typeable letter hands the line on
            // here rather than falling through to the global bindings, so the caret can be
            // moved by typing the next line instead of by space. Asked of the ENGINE rather
            // than decided here (ProcessKey answers whether it consumed the press), because a
            // letter it refuses must still fall through, which is what keeps the mid-song skip
            // overlay reachable through an instrumental gap. This is the arm the engine's own
            // rule needs to be reachable at all: without it a letter never gets past this
            // block, however the engine is configured.
            //
            // Since backlog 383 the letter is the OS's character, typed by TypeLatinText,
            // which asks the engine the same question. When the commit is already here the
            // answer decides the fall-through exactly as before; when it lands a frame later
            // the press has already fallen through, and the letter still hands the line on.
            if (line.NewlineOnTypedLetter && route.FreshTextPress && key != Key.Space)
                return TypeBeatKeyOutcome.TypedNewLine;

            return TypeBeatKeyOutcome.FallThrough;
        }

        private static TypeBeatKeyOutcome typingKey(TypeBeatKeyRoute route)
        {
            var press = route.Press;

            // POLYGLOT (backlog 331): characters arrive as committed TEXT (see TypeCommittedText), so a
            // character key does nothing here; it is swallowed while it would type, exactly as it is
            // without the mod, so a letter never reaches a global binding mid-line. The SPACEBAR is
            // the one typing key that stays a key, because it is the word gesture every layout and
            // IME agrees on.
            if (route.Play.UsesIme)
            {
                if (press.Key == Key.Space)
                    return press.Repeat ? TypeBeatKeyOutcome.Swallow : TypeBeatKeyOutcome.ImeSpace;

                return polyglotSwallows(press) ? TypeBeatKeyOutcome.Swallow : TypeBeatKeyOutcome.FallThrough;
            }

            // A LATIN TYPING KEY on an active, unfinished line (backlog 383). The character is
            // whatever the OS commits for the press, folded to the surface and typed when it
            // arrives (usually this very frame, see pressFor); the key itself decides only
            // whether the press is swallowed, so a letter never reaches a global binding mid-line.
            if (TypingKeys.CommitsText(press.Key))
            {
                // The framework's own auto-repeat is discarded outright: one judgement per
                // physical press, never a machine-gun run at the keyboard's repeat rate.
                if (press.Repeat)
                    return TypesHere(route.Play, press.Key) ? TypeBeatKeyOutcome.Swallow : TypeBeatKeyOutcome.FallThrough;

                // The default surface keeps the digit row POSITIONAL (see TypingKeys.TryPositionalDigit).
                if (!route.Play.Polyglot && !route.Play.Literate && TypingKeys.TryPositionalDigit(press.Key, out _))
                    return TypeBeatKeyOutcome.PositionalDigit;

                return press.Key == Key.Space ? TypeBeatKeyOutcome.TypeSpace : TypeBeatKeyOutcome.Type;
            }

            return TypeBeatKeyOutcome.FallThrough;
        }

        /// <summary>
        /// What a Polyglot play swallows as a character key, unchanged since backlog 331, where it
        /// was "the key would type with the punctuation surface open" on the US table: every
        /// letter and digit (top row or keypad) and the spacebar, the punctuation positions that
        /// carry a supported mark on either legend, and of the three with a mark on only one, the
        /// legend that has it (the brackets unshifted, the tilde shifted).
        /// </summary>
        private static bool polyglotSwallows(TypeBeatKeyPress press)
        {
            if (TypingKeys.AlwaysSwallowed(press.Key))
                return true;

            switch (press.Key)
            {
                case Key.Comma:
                case Key.Period:
                case Key.Quote:
                case Key.Minus:
                case Key.Slash:
                case Key.Semicolon:
                    return true;

                case Key.BracketLeft:
                case Key.BracketRight:
                    return !press.Shift;

                case Key.Tilde:
                    return press.Shift;

                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// A press after <see cref="TypeBeatKeyGate.Route"/>: the play it was routed in, the press, the
    /// outcome when the first stage already settled it, the gesture left after shadowing, and
    /// whether it is a FRESH text press (one that claims exactly one commit from the OS's text input).
    /// </summary>
    public readonly record struct TypeBeatKeyRoute(TypeBeatKeyPlay Play, TypeBeatKeyPress Press, TypeBeatKeyOutcome? Settled, TypeBeatAction? Gesture, bool FreshTextPress);
}
