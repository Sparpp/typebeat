# 0383: text input vs key timestamp

Backlog 383 moved Latin gameplay typing from the key path (KeyDown, read through KeyCharMap) to the
framework's text input (the characters the OS commits). The engine judges `(char, time)` and the
replay records the same pair, so the one thing that could change a run is the TIME a character is
stamped with. This note records how that was measured and what came out.

## Result

- Systematic difference between the text timestamp and the key timestamp of the same press: **0 ms**.
- Bounded worst case: **one update frame**, only when the update thread happens to run between the
  two dispatches of one keystroke (microseconds apart), and then the text is typed at the next
  frame's time. With the default frame limiter (Limit2x) the update thread runs at 4x the display
  refresh, capped at 1000 Hz: 4.2 ms at 60 Hz, 1.7 ms at 144 Hz, 1 ms at 250 Hz and above. This is
  the same quantisation the key path always had (a key was stamped with the update frame that
  consumed it), it is not systematic, and it is not a new era: **no CONFIG_EXTENDED bit**.

## Method 1: the framework source (osu.Framework 2026.629.0, decompiled)

1. `SDL3Window.pollSDLEvents` runs one `SDL_PumpEvents` and then drains the queue in batches of 64
   with `SDL_PeepEvents`, dispatching each event synchronously on the window thread
   (`HandleEvent`). `SDL_EVENT_KEY_DOWN` goes to `handleKeyboardEvent`, `SDL_EVENT_TEXT_INPUT` to
   `handleTextInputEvent`, `SDL_EVENT_TEXT_EDITING` to `handleTextEditingEvent`.
2. Both halves of one keystroke come from the same OS event and so from the same pump, KEY_DOWN
   first: Windows (`WM_KEYDOWN`, then the `WM_CHAR` that `TranslateMessage` posts behind it, both
   drained by the same `SDL_PumpEvents`), macOS (`keyDown:` sends the key event, then
   `interpretKeyEvents` calls `insertText:` synchronously), X11 without an input method (the
   `KeyPress` handler sends the key, then the `Xutf8LookupString` text).
3. `handleKeyboardEvent` raises `KeyDown(key)` with no timestamp: the SDL event's own `timestamp`
   is discarded. `KeyboardHandler` enqueues a `KeyboardKeyInput` into `PendingInputs`, which the root
   input manager applies on the UPDATE thread; the `KeyDownEvent` a drawable sees carries no time
   either, so the old key path stamped a keystroke with `Time.Current` of the update frame that
   consumed it.
4. `handleTextInputEvent` raises `TextInput(text)`, which `SDLWindowTextInput` forwards to
   `TextInputSource.OnTextInput` (or `OnImeResult` while a composition is open), again with no
   timestamp. The playfield queues it and types it from its own update, with `Time.Current` of
   that update frame.
5. So neither path ever carried the OS timestamp; both are stamped by the update frame that
   consumes them, and the two halves of a keystroke are enqueued microseconds apart on the window
   thread. They meet in the same update frame unless the update thread collects input inside that
   gap, which costs at most one frame (see the pairing in `TypeBeatPlayfield.TypeBeatKeyHandler`).
6. Update rate: `GameHost.updateFrameSyncMode` sets the update thread to twice the draw rate, and
   Limit2x (the default) sets draw to 2x refresh, capped at 1000 Hz unless benchmarking.

## Method 2: the headless harness, through the real playfield

The headless host has no SDL window and its text source never commits anything, so the OS events
themselves cannot be produced headlessly. The test scenes' input manager therefore stands in for
SDL's ordering (`typebeat.Game/Tests/Visual/EmulatedTextInput.cs`): every key press commits its
character in the same frame, after the key, exactly as one pump delivers them.

The harness is `TestSceneTypeBeatTextInput.TestTheTextTimestampMatchesTheKeyTimestamp`
(typebeat.Game.Rulesets.TypeBeat.Tests): 20 pumps of A (typed from its TEXT) followed by Backspace
(still typed by its KEY), through the real `Player`, playfield, recorder and replay frames. It
compares the recorded frame time of each text-typed 'a' with the key-typed backspace that follows
it in the same pump.

```
[0383 timing] pairs=20 max|text - key|=0 ms
```

`TestALateCommitIsTypedAtItsOwnTime` covers the cross-frame case: the commit arrives a frame after
its key, the press waits for it, and it is typed once at the frame it arrived in.

## What could not be verified headlessly, and how it is bounded

- The real SDL3 ordering on each OS (Method 1 is source reading, not a capture). Bounded by the
  pairing: a press waits up to 50 ms of real time for its commit, a commit up to 50 ms for its
  press, whichever arrives first.
- Linux input-method buses (IBus, Fcitx) and Wayland's text-input protocol can hand the commit back
  later than the key. The character is then typed at the frame the commit arrives in, up to the
  50 ms grace, after which a keystroke whose text never came is dropped (the spacebar falls back to
  typing its space). Not measured; worth a check on a Linux box with IBus running.
- That every platform commits a space as text: SDL3 sends `TEXT_INPUT` for every printable
  character (it drops only control characters), and Windows `WM_CHAR`, macOS `insertText:`, X11
  and Wayland's xkb all produce " " for the spacebar. The key is kept as the fallback regardless.
- The ~250 ms floor on OS key repeat that keeps a repeated commit from finding a press: Windows'
  shortest repeat delay is 250 ms, macOS's shortest InitialKeyRepeat is about 225 ms, X11's default
  is 660 ms (configurable lower; a delay under 50 ms would let one repeat through per press).
