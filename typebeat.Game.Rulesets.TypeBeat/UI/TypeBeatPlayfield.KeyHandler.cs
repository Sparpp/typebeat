// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Input;
using osu.Framework.Input.Bindings;
using osu.Framework.Input.Events;
using osu.Framework.Timing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Configuration;
using typebeat.Game.Input;
using typebeat.Game.Rulesets.Objects.Drawables;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects.Drawables;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Rulesets.UI;
using typebeat.Game.Screens.Edit.GameplayTest;
using typebeat.Game.Screens.Play;
using osuTK.Graphics;
using osuTK.Input;

namespace typebeat.Game.Rulesets.TypeBeat.UI
{
    public partial class TypeBeatPlayfield
    {
        /// <summary>
        /// Full-keyboard typing input, taken via raw <see cref="OnKeyDown"/> inside the ruleset
        /// input manager's subtree (raw key events pass through RulesetInputManager to its
        /// children; children receive them before the key-binding container, so typing letters
        /// wins over the vestigial Z/X action bindings). OS/framework key-repeat is honoured ONLY
        /// for backspace (hold to erase); a held character key never machine-guns judgements at the
        /// keyboard's own repeat rate, and holding it produces nothing at all beyond the initial
        /// press. Ctrl/Alt combos fall through to framework shortcuts, EXCEPT the two word-level
        /// editing gestures every typing site has and backlog 182 brought here: ERASE WORD (default
        /// Ctrl+Backspace, Command+Backspace on macOS; key repeat honoured like the plain key) and
        /// SELECT BACK TO TYPO (default Ctrl+A, Command+A on macOS; select back to the mistake that
        /// has to be retyped). Which modifier it is, is
        /// <see cref="TypeBeatRuleset.RecoveryGestureModifier"/>'s to say.
        ///
        /// <para>Since backlog 383 the CHARACTERS come from the OS, not from the key: a Latin play
        /// activates the framework's text input (IME off) and types what the player's own layout
        /// commits, folded to the surface by <see cref="TextInputFold"/>, through
        /// <see cref="TypeLatinText"/>. The key path still owns everything that is not a character
        /// (Backspace, the gestures, Enter, the instrumental-skip and manual-newline fall-throughs)
        /// and decides, per press, whether the press is swallowed and what becomes of the text it
        /// commits (<see cref="PressPlan"/>); each fresh press claims exactly one commit, which is
        /// also what drops the OS's own key repeat. The engine and the replay format are untouched:
        /// the engine already consumed (char, time), and the recorder already recorded the produced
        /// character, so a stored run re-derives exactly as it did.</para>
        ///
        /// <para>Since backlog 183 those two are REBINDABLE ruleset actions
        /// (<see cref="TypeBeatAction.EraseWord"/> / <see cref="TypeBeatAction.SelectBackToTypo"/>),
        /// so the chords above are defaults rather than constants, and every press is resolved
        /// against the user's current bindings through
        /// <see cref="TypeBeatInputManager.ResolveGesture"/>. This handler stays the single owner of
        /// gameplay input: the actions are never PRESSED (nothing implements
        /// <c>IKeyBindingHandler</c> for them), so the replay recorder, the key counters and the
        /// clicks-per-second counter cannot see them, and one precedence order decides everything.
        /// That order, top down: a gesture rebound onto a bare or shifted TYPEABLE key is shadowed
        /// and types (nothing else is survivable mid-run: the whole lyric surface is typeable);
        /// otherwise a matched gesture wins, ahead of the plain backspace and ahead of the
        /// instrumental-skip fall-throughs; anything unmatched behaves exactly as it did before.</para>
        ///
        /// <para>Backlog 241 adds a third, the LINE SKIP (<see cref="TypeBeatAction.SkipLine"/>,
        /// default Enter and Keypad Enter), which is the one gesture that is not composed: it is a
        /// single <see cref="TypingEngine.ProcessEnter"/> call recorded as one new sentinel frame.
        /// Swallowing Enter mid-line costs nothing, because the ruleset input manager receives no
        /// input at all while the pause or fail overlay is up (<c>DrawableRuleset</c> sets
        /// <c>UseParentInput</c> from the paused state), so their <c>GlobalAction.Select</c> is
        /// untouched, and the in-game <c>ToggleChatFocus</c> the key otherwise carries has no
        /// consumer in this game. An INEFFECTIVE skip is not swallowed: the press falls through
        /// exactly as it did before the gesture existed.</para>
        ///
        /// <para>Both gestures are COMPOSED out of engine calls that already exist: a run of
        /// <see cref="TypingEngine.ProcessBackspace"/> plus at most one
        /// <see cref="TypingEngine.ProcessKey"/>, each recorded through the same seam a single
        /// keystroke is. The engine gains only two PURE QUERIES saying where to stop
        /// (<see cref="TypingEngine.WordBackspaceTarget"/> and
        /// <see cref="TypingEngine.RetypeSelectionAnchor"/>). That is what lets a whole word
        /// disappear with no new replay frame vocabulary and no new era bit: a stored run holds
        /// exactly the calls the live engine made, in the order it made them, stamped with the one
        /// timestamp it judged them at. The ERASE width is gated on
        /// <see cref="TypingEngine.AllowWrongInput"/> exactly as the plain backspace is; the SELECT
        /// width is not, since backlog 244 (a word skip is orthogonal to the input model, so a
        /// Gatekeeper run has abandoned cells to select back over even though it can have no typos).
        /// Both are LIVE input only: replay playback feeds recorded frames straight into the engine
        /// and never reaches this class.</para>
        ///
        /// <para>The SELECTION a Ctrl+A computes is pure UI state held on the playfield
        /// (<see cref="TypeBeatPlayfield.CurrentRetypeSelection"/>); the engine never learns it
        /// exists. The next effective input CONSUMES it: a typeable key collapses it (a mass
        /// backspace to the anchor) and then types at the anchor through the normal
        /// <see cref="TypingEngine.ProcessKey"/> path, a plain backspace collapses it and types
        /// nothing.</para>
        ///
        /// <para>Backspace is live in allow-wrong-input mode. Under Gatekeeper it can still consume
        /// a retype selection. Replay playback feeds recorded backspace frames straight to the
        /// engine (see <see cref="EngineTicker"/>).</para>
        ///
        /// <para>Replay determinism: every keystroke is stamped with the ROUNDED (integral ms)
        /// lyric time, the engine is advanced to that exact time first, and every EFFECTIVE input
        /// (one that mutated engine state) is forwarded to the active replay recorder as
        /// (char, time). Replay playback repeats the identical <c>Update(t)</c> + keystroke call
        /// sequence, and integral times survive the legacy .osr encoding losslessly, so a stored
        /// replay reproduces the score bit-exactly. While a replay is attached the ruleset input
        /// manager stops forwarding real input (<c>UseParentInput = false</c>), so this handler is
        /// naturally inert during playback.</para>
        /// </summary>
        private partial class TypeBeatKeyHandler : Drawable
        {
            private readonly TypingEngine engine;
            private readonly DrawableTypeBeatRuleset? drawableRuleset;
            private readonly TypeBeatPlayfield playfield;

            // Cached by Player (via GameplayClockContainer / FrameStabilityContainer); absent in bare
            // drawable-ruleset test scenes, where there is no rate mod to report anyway.
            [Resolved]
            private IGameplayClock? gameplayClock { get; set; }

            [Resolved]
            private EditorTestPlayback? editorPlayback { get; set; }

            public TypeBeatKeyHandler(TypingEngine engine, DrawableTypeBeatRuleset? drawableRuleset, TypeBeatPlayfield playfield)
            {
                this.engine = engine;
                this.drawableRuleset = drawableRuleset;
                this.playfield = playfield;
                RelativeSizeAxes = Axes.Both;
            }

            public override bool HandleNonPositionalInput => true;

            public override bool AcceptsFocus => true;

            public override bool RequestsFocus => true;

            /// <summary>
            /// The ruleset input manager this handler sits inside, which owns the live (realm-backed)
            /// binding list the two gestures are resolved against. Found once at load;
            /// <c>DrawableRuleset</c> always wraps the playfield in one.
            /// </summary>
            private TypeBeatInputManager? rulesetInput;

            /// <summary>Ruleset defaults, used only if this playfield were ever hosted outside a
            /// <see cref="TypeBeatInputManager"/>: a dead gesture would be a far worse failure than
            /// an unconfigurable one.</summary>
            private IEnumerable<IKeyBinding>? fallbackBindings;

            private readonly IBindable<bool> isPaused = new BindableBool();

            protected override void LoadComplete()
            {
                base.LoadComplete();
                rulesetInput = this.FindClosestParent<TypeBeatInputManager>();
                puppeteer = drawableRuleset?.Mods.OfType<TypeBeatModPuppeteer>().FirstOrDefault();

                // Text input is live exactly while key handling is (backlog 383): the ruleset input
                // manager stops forwarding keys while the pause or fail overlay is up, and the text
                // source is released for the same span, so a paused game never swallows text a menu
                // or a chat box wanted. Both kinds of play ride it now: Polyglot for its scripts and
                // IME, a Latin play for the OS layout's own characters.
                if (drawableRuleset != null)
                    isPaused.BindTo(drawableRuleset.IsPaused);

                isPaused.BindValueChanged(paused =>
                {
                    if (paused.NewValue)
                        deactivateTextInput();
                    else
                        activateTextInput();
                }, true);
            }

            #region Text input (backlog 331, backlog 383)

            /// <summary>
            /// The framework's TEXT INPUT source, the path text boxes use, and since backlog 383 the
            /// ONLY source of typed characters. Under the Polyglot mod (backlog 331) whatever the
            /// player's OS layout commits (a dead key's "é", Cyrillic, Greek, Georgian, Armenian,
            /// direct kana, a hangul key) is the character judged, and an IME (hanzi, kanji) composes
            /// before it commits. In a LATIN play the OS's character goes through
            /// <see cref="TextInputFold"/> to the ASCII surface, which is what retired the hand-written
            /// layout tables (the deleted <c>KeyCharMap</c>) and the keyboard-layout setting with them:
            /// Shift, Caps Lock, AltGr, dead keys and every national layout come from the OS. Null in a
            /// bare test scene that caches none.
            /// </summary>
            [Resolved]
            private TextInputSource? textInput { get; set; }

            private bool textInputActive;
            private bool usesIme => engine.Polyglot && !engine.UsesJapaneseRomaji;

            /// <summary>The live IME composition, empty when none. Backspace, space and enter belong to the IME while it is not.</summary>
            private string imeComposition = string.Empty;

            /// <summary>
            /// The text input type a LATIN play activates with: <see cref="TextInputType.Code"/>, with IME
            /// disallowed. Code is the framework's one plain-text type that turns the IME off on Windows
            /// (<c>SupportsIme</c> is false for it, so the framework calls <c>ImmAssociateContext</c>
            /// with nothing) and turns SDL's autocorrect off; it is NOT a password type, so macOS never
            /// enters secure event input for it. A Latin play wants every key straight from the
            /// layout: an IME left on would hold a Latin letter in a composition window rather than
            /// commit it. Japanese Polyglot uses this path for its internal romaji processor. Other Polyglot languages keep <see cref="TextInputType.Text"/> with the IME allowed.
            /// </summary>
            internal static readonly TextInputProperties LATIN_TEXT_INPUT = new TextInputProperties(TextInputType.Code, AllowIme: false);

            /// <summary>Polyglot's activation, unchanged since backlog 331: plain text, IME allowed.</summary>
            internal static readonly TextInputProperties POLYGLOT_TEXT_INPUT = new TextInputProperties(TextInputType.Text, true);

            private void activateTextInput()
            {
                // A replay's characters are the tape's, not the keyboard's: nothing is activated for playback.
                if (textInputActive || textInput == null || drawableRuleset?.ReplayScore != null)
                    return;

                if (usesIme)
                {
                    textInput.OnTextInput += onTextCommitted;
                    textInput.OnImeResult += onTextCommitted;
                    textInput.OnImeComposition += onImeComposition;
                    textInput.Activate(POLYGLOT_TEXT_INPUT, playfield.imeRectangle);
                }
                else
                {
                    // An IME RESULT is subscribed too: macOS composes a dead key through the text
                    // input client (the accent arrives as marked text, then the composed letter as a
                    // result rather than as plain text), and the framework routes a commit that ends
                    // a composition to OnImeResult. The composition itself types nothing.
                    textInput.OnTextInput += onLatinTextCommitted;
                    textInput.OnImeResult += onLatinTextCommitted;
                    textInput.Activate(LATIN_TEXT_INPUT, playfield.imeRectangle);
                }

                textInputActive = true;
            }

            private void deactivateTextInput()
            {
                if (!textInputActive || textInput == null)
                    return;

                if (usesIme)
                {
                    textInput.OnTextInput -= onTextCommitted;
                    textInput.OnImeResult -= onTextCommitted;
                    textInput.OnImeComposition -= onImeComposition;

                    // A composition the pause cut off is gone; the IME starts clean on resume.
                    imeComposition = string.Empty;
                    playfield.setImeComposition(imeComposition);
                }
                else
                {
                    textInput.OnTextInput -= onLatinTextCommitted;
                    textInput.OnImeResult -= onLatinTextCommitted;
                }

                textInput.Deactivate();
                textInputActive = false;

                // Nothing typed before the pause may land after it.
                lock (committedLock)
                    committed.Clear();

                waitingPresses.Clear();
            }

            protected override void Update()
            {
                base.Update();

                // The IME's candidate window follows the caret while a composition is open.
                if (textInputActive && imeComposition.Length > 0)
                    textInput?.SetImeRectangle(playfield.imeRectangle);

                if (!usesIme)
                    pairCommittedText();

                // The engine completed the hold itself (a Great at its end): its key no longer ends anything.
                if (engine.ActiveHold == null)
                    heldKey = null;

                if (engine.UsesJapaneseRomaji)
                    playfield.setImeComposition(engine.JapanesePending);
            }

            protected override void Dispose(bool isDisposing)
            {
                deactivateTextInput();
                base.Dispose(isDisposing);
            }

            private void onImeComposition(string text, int selectionStart, int selectionLength) => Schedule(() =>
            {
                imeComposition = text ?? string.Empty;
                playfield.setImeComposition(imeComposition);
            });

            private void onTextCommitted(string text) => Schedule(() =>
            {
                imeComposition = string.Empty;
                playfield.setImeComposition(imeComposition);
                TypeCommittedText(text, Math.Round(Time.Current));
            });

            /// <summary>
            /// Types a piece of COMMITTED text under the Polyglot mod: every character it carries
            /// (<see cref="PolyglotText.InputCharacters"/>: NFC, a hangul block cut into its keys,
            /// nothing outside the BMP) is one ordinary <see cref="TypingEngine.ProcessKey"/> call at
            /// the one timestamp the commit arrived at, recorded exactly as a key press is, so a
            /// multi-character commit replays as the same run of frames. A SPACE from text input is
            /// ignored: the spacebar is a gesture and arrives through <see cref="OnKeyDown"/>. The
            /// rest goes through the same gates as Latin text (<see cref="typeCharacters"/>).
            /// </summary>
            internal void TypeCommittedText(string text, double time)
            {
                // Nothing while paused (the ruleset input manager hands the key path nothing then either),
                // and nothing while another control holds focus (a chat box has activated text input too).
                if (!engine.Polyglot || drawableRuleset?.ReplayScore != null || drawableRuleset?.IsPaused.Value == true || !HasFocus)
                    return;

                engine.Update(time, wpmClockRate(gameplayClock, editorPlayback));

                typeCharacters(PolyglotText.InputCharacters(text).Where(c => c != ' '), time);
            }

            #endregion

            #region Latin text input (backlog 383)

            /// <summary>
            /// What a key press does with the text the OS commits for it, decided where the press is
            /// judged (<see cref="OnKeyDown"/>), which is the only place every gate a typing key has
            /// always had (the dead zones, the instrumental-skip fall-throughs, the manual newline,
            /// the gestures) can still be asked.
            /// </summary>
            private enum PressPlan
            {
                /// <summary>Type the folded text, through the same gates a typing key had.</summary>
                Type,

                /// <summary>
                /// Type the folded text, and if the platform committed none for the press, type the
                /// SPACE it stands for: the spacebar's fallback, so a platform that does not emit a
                /// space as text still skips words.
                /// </summary>
                Space,

                /// <summary>
                /// The key already did everything its press means (it fell through to a global binding,
                /// typed a digit by position, or was a gesture), so the text it committed is consumed and
                /// discarded rather than left for the next press to claim.
                /// </summary>
                Drop,
            }

            private sealed class PendingPress
            {
                public PressPlan Plan;

                /// <summary>The physical key behind the press, whose release ends a hold it opens.</summary>
                public Key? Key;

                /// <summary>When the press was made, on <see cref="pairingClock"/>.</summary>
                public double Since;

                /// <summary>The gameplay time the key went down at, the instant it is judged at under <see cref="TypingEngine.KeyTimeJudgement"/>.</summary>
                public double KeyTime;

                /// <summary>The Puppeteer wall stamp when the key went down (see <see cref="TypeBeatModPuppeteer.WallStampMs"/>), null without a tape.</summary>
                public double? WallStamp;
            }

            private sealed class CommittedText
            {
                public readonly string Text;

                /// <summary>When an update first saw this commit, on <see cref="pairingClock"/>; NaN until one has.</summary>
                public double Since = double.NaN;

                /// <summary>Whether no update has seen it yet, i.e. it arrived in the same pump as the keys being handled now.</summary>
                public bool Fresh = true;

                public CommittedText(string text)
                {
                    Text = text;
                }
            }

            /// <summary>
            /// How long a press may wait for its commit, and a commit for its press, before the
            /// pairing gives up on it, in REAL milliseconds.
            ///
            /// <para>WHY THE PAIRING EXISTS. A text event carries no key and no timestamp, and the
            /// framework delivers the two halves of one keystroke on different paths: the SDL pump
            /// (one batch on the window thread) hands the KEY_DOWN to the keyboard handler's pending
            /// inputs and, immediately after, the TEXT_INPUT to the text source, and the update thread
            /// applies the first through the input managers and this handler's queue through
            /// <see cref="Update"/>. On Windows, macOS and X11 without an input method the two are
            /// produced by the same OS event and dispatched microseconds apart, so they almost always
            /// meet in the same update frame, and otherwise one frame apart (measured and bounded in
            /// <c>docs/drafts/0383-text-input-timing.md</c>). An input-method bus (IBus or Fcitx on
            /// Linux, Wayland's text-input protocol) can hand the commit back a little later. So each
            /// fresh press claims ONE commit and waits for it up to this long, and each commit waits
            /// as long for a press.</para>
            ///
            /// <para>THAT IS ALSO WHAT DROPS OS KEY REPEAT. A held key makes the OS commit its character
            /// again and again, but the framework never reports those repeats as new presses (the key
            /// is already down, so no fresh KeyDownEvent is raised; its own timer-driven repeats carry
            /// <see cref="KeyDownEvent.Repeat"/> and are not presses either). A repeated commit
            /// therefore finds no press to claim it and is discarded once its grace is over: one
            /// judgement per physical press, exactly the rule the key path always had (backlog 105
            /// removed the game's own hold-to-repeat outright, so there is no second repeat to keep).
            /// No OS starts repeating a held key sooner than about 250 ms after the press, five times
            /// this grace, so a press that committed nothing (a dead key) has expired long before a
            /// repeat could claim it. Backspace is a key, not text, and keeps its hold-to-erase.</para>
            ///
            /// <para>REAL time rather than gameplay time or frames: the frame-stability container
            /// above the playfield can run this subtree several times in one real frame while it
            /// catches up, and a rate mod stretches gameplay time, and neither is any time at all for
            /// the window thread delivering the other half of a keystroke.</para>
            /// </summary>
            private const double pairing_grace_ms = PAIRING_GRACE_MS;

            /// <summary>Real time for the pairing's grace, independent of every gameplay clock.</summary>
            private readonly Stopwatch pairingClock = Stopwatch.StartNew();

            private double pairingNow => pairingClock.Elapsed.TotalMilliseconds;

            /// <summary>See <see cref="TypeBeatPlayfield.FreezePairingClock"/>.</summary>
            internal void FreezePairingClock(bool frozen)
            {
                if (frozen)
                    pairingClock.Stop();
                else
                    pairingClock.Start();
            }

            /// <summary>Commits from the text source, in arrival order. Filled on the window thread (live) or the update thread (tests), so locked.</summary>
            private readonly List<CommittedText> committed = new List<CommittedText>();

            private readonly object committedLock = new object();

            /// <summary>Fresh presses still waiting for their commit, oldest first. Update thread only.</summary>
            private readonly List<PendingPress> waitingPresses = new List<PendingPress>();

            private bool latinTextInputActive => textInputActive && !usesIme;

            private void onLatinTextCommitted(string text)
            {
                if (string.IsNullOrEmpty(text))
                    return;

                lock (committedLock)
                    committed.Add(new CommittedText(text));
            }

            /// <summary>
            /// The commit a NEW press produced, if it has already arrived: the oldest one that arrived
            /// since the last update (the same pump as this key, so almost surely its own), else the
            /// oldest one still in its grace. Preferring the fresh one is what keeps a press made while
            /// another key is HELD from claiming a repeat of the held key, and a press after a dead key
            /// (whose own press committed nothing and is still waiting) from leaving its character to
            /// that older press.
            /// </summary>
            private string? claimCommittedText()
            {
                lock (committedLock)
                {
                    int index = committed.FindIndex(t => t.Fresh);

                    if (index < 0 && committed.Count > 0)
                        index = 0;

                    if (index < 0)
                        return null;

                    string text = committed[index].Text;
                    committed.RemoveAt(index);
                    return text;
                }
            }

            /// <summary>Pairs every press still waiting with the commits that have arrived, oldest with oldest.</summary>
            private List<(PendingPress press, string? text)>? pairWaitingPresses()
            {
                List<(PendingPress press, string? text)>? resolved = null;

                lock (committedLock)
                {
                    while (waitingPresses.Count > 0 && committed.Count > 0)
                    {
                        (resolved ??= new List<(PendingPress, string?)>()).Add((waitingPresses[0], committed[0].Text));
                        waitingPresses.RemoveAt(0);
                        committed.RemoveAt(0);
                    }
                }

                return resolved;
            }

            private void resolveAll(List<(PendingPress press, string? text)>? resolved)
            {
                if (resolved == null)
                    return;

                foreach (var (press, text) in resolved)
                {
                    if (!engine.KeyTimeJudgement)
                    {
                        resolvePress(press.Plan, text, Math.Round(Time.Current), press.Key);
                        continue;
                    }

                    // A Puppeteer run is recorded on the wall axis, so the frames this press makes
                    // carry the stamp of the instant it is judged at too, not the one the pairing
                    // resolved in, or the transform would re-derive it at a later track time.
                    resolvingWallStamp = judgedWallStampOf(press);

                    try
                    {
                        resolvePress(press.Plan, text, judgedTimeOf(press), press.Key);
                    }
                    finally
                    {
                        resolvingWallStamp = null;
                    }
                }
            }

            /// <summary>The Puppeteer mod in this play's stack, whose wall axis a run under it is recorded on; null without one.</summary>
            private TypeBeatModPuppeteer? puppeteer;

            /// <summary>The Puppeteer wall stamp a frame recorded now would carry, null without an anchored tape.</summary>
            private double? wallStampNow => puppeteer?.WallStampMs;

            /// <summary>The wall stamp of <see cref="lastKeyTime"/>'s instant, for <see cref="judgedWallStampOf"/>.</summary>
            private double lastKeyWallStamp = double.NegativeInfinity;

            /// <summary>
            /// The wall stamp every frame recorded while a waiting press is resolved carries (the
            /// press itself and any erase a retype selection makes ahead of it); null otherwise,
            /// when the recorder reads the mod's current stamp.
            /// </summary>
            private double? resolvingWallStamp;

            /// <summary>The wall stamp of <see cref="judgedTimeOf"/>'s instant, clamped the same way so stamps stay in order.</summary>
            private double? judgedWallStampOf(PendingPress press) => press.WallStamp is double stamp ? Math.Max(stamp, lastKeyWallStamp) : null;

            /// <summary>Records one effective input at <paramref name="time"/> (see <see cref="resolvingWallStamp"/> for its wall stamp).</summary>
            private void record(char character, double time) => drawableRuleset?.RecordTypingInput(character, time, resolvingWallStamp);

            /// <summary>
            /// The highest time this handler has advanced the engine to on a key's behalf (see
            /// <see cref="advanceEngineTo"/>). Negative infinity before the first.
            /// </summary>
            private double lastKeyTime = double.NegativeInfinity;

            /// <summary>
            /// The time a waiting press is judged and recorded at under
            /// <see cref="TypingEngine.KeyTimeJudgement"/>: its key time, unless a LATER key has
            /// advanced the engine past it in the meantime (Backspace, a gesture, or another press
            /// whose commit came first), in which case that key's time. The engine never moves
            /// backwards for a press: <see cref="TypingEngine.Update"/> is monotone, a sealed line
            /// cannot unseal, and a press judged at a time the engine has already left would be fed
            /// on a call sequence no replay can repeat. Clamped this way the recorded frames stay in
            /// time order and in the order they were processed, and each one re-derives exactly.
            /// </summary>
            private double judgedTimeOf(PendingPress press) => Math.Max(press.KeyTime, lastKeyTime);

            /// <summary>
            /// The time the engine is held at for the oldest press still waiting for its commit, or
            /// null when none is (or the run predates <see cref="TypingEngine.KeyTimeJudgement"/>).
            /// Read by the engine ticker, which does not tick past it, so the press is judged on the
            /// engine as it stood at its key time; at most <see cref="pairing_grace_ms"/> of real time,
            /// after which the pairing resolves or gives up on the press. A press that will only
            /// discard its commit (<see cref="PressPlan.Drop"/>) judges nothing and holds nothing.
            /// </summary>
            public double? HeldPressTime
            {
                get
                {
                    if (!engine.KeyTimeJudgement)
                        return null;

                    foreach (var press in waitingPresses)
                    {
                        if (press.Plan != PressPlan.Drop)
                            return judgedTimeOf(press);
                    }

                    return null;
                }
            }

            /// <summary>Advances the engine to a key's <paramref name="time"/> and notes it for <see cref="judgedTimeOf"/>.</summary>
            private void advanceEngineTo(double time)
            {
                engine.Update(time, wpmClockRate(gameplayClock, editorPlayback));
                lastKeyTime = Math.Max(lastKeyTime, time);

                if ((resolvingWallStamp ?? wallStampNow) is double stamp)
                    lastKeyWallStamp = Math.Max(lastKeyWallStamp, stamp);
            }

            /// <summary>
            /// The per-update half of the pairing: presses still waiting claim the commits that have
            /// arrived, then whatever has outlived its grace is given up on. A commit given up on was
            /// an OS repeat (or a stray with no press); a press given up on committed nothing (a dead
            /// key, a key with no character on the layout), except that the spacebar then types its
            /// space.
            /// </summary>
            private void pairCommittedText()
            {
                if (!latinTextInputActive)
                    return;

                double now = pairingNow;
                var resolved = pairWaitingPresses();

                lock (committedLock)
                {
                    foreach (var text in committed)
                    {
                        text.Fresh = false;

                        if (double.IsNaN(text.Since))
                            text.Since = now;
                    }

                    committed.RemoveAll(t => now - t.Since > pairing_grace_ms);
                }

                for (int i = 0; i < waitingPresses.Count; i++)
                {
                    var press = waitingPresses[i];

                    if (now - press.Since <= pairing_grace_ms)
                        continue;

                    waitingPresses.RemoveAt(i--);

                    if (press.Plan == PressPlan.Space)
                        (resolved ??= new List<(PendingPress, string?)>()).Add((press, null));
                }

                resolveAll(resolved);
            }

            /// <summary>
            /// Carries out what <paramref name="plan"/> says with the commit a press produced
            /// (<paramref name="text"/>, null when it committed nothing), at <paramref name="time"/>.
            /// Returns whether anything was typed.
            /// </summary>
            private bool resolvePress(PressPlan plan, string? text, double time, Key? key = null)
            {
                switch (plan)
                {
                    case PressPlan.Drop:
                        return false;

                    case PressPlan.Space when text == null:
                        return TypeLatinText(" ", time, key);

                    default:
                        return text != null && TypeLatinText(text, time, key);
                }
            }

            /// <summary>
            /// Registers one fresh press for the pairing and resolves it on the spot when its commit
            /// is already here. Returns whether anything was typed.
            /// </summary>
            private bool pressFor(PressPlan plan, double time, Key? key = null)
            {
                if (!latinTextInputActive)
                {
                    // No text source at all (a bare scene) or none live: the spacebar alone still works,
                    // by its key, which is the fallback the text path promises.
                    return plan == PressPlan.Space && TypeLatinText(" ", time, key);
                }

                string? text = claimCommittedText();

                if (text != null)
                    return resolvePress(plan, text, time, key);

                waitingPresses.Add(new PendingPress { Plan = plan, Key = key, Since = pairingNow, KeyTime = time, WallStamp = wallStampNow });
                return false;
            }

            /// <summary>
            /// Types a piece of COMMITTED text in a LATIN play (backlog 383): every press
            /// <see cref="TextInputFold.Fold"/> makes of it (a base letter for an accented one, "ss"
            /// for 'ß', marks only under Literate, nothing for a character the surface cannot hold) is
            /// one ordinary <see cref="TypingEngine.ProcessKey"/> call at <paramref name="time"/>,
            /// recorded exactly as a key press always was, so the replay format and the engine are
            /// untouched and a stored run re-derives cell for cell. The gates are the ones a typing key
            /// had on the key path (<see cref="typeCharacters"/>). Returns whether anything was typed.
            ///
            /// <para><paramref name="key"/> is the physical key behind the press: its release ends a hold the press opens.</para>
            /// </summary>
            internal bool TypeLatinText(string text, double time, Key? key = null)
            {
                // Nothing while paused, during playback, or while another control holds focus (a chat
                // box can have activated text input too, and its characters are its own).
                if (usesIme || drawableRuleset?.ReplayScore != null || drawableRuleset?.IsPaused.Value == true || !HasFocus)
                    return false;

                advanceEngineTo(time);

                bool typed = typeCharacters(engine.UsesJapaneseRomaji ? PolyglotText.InputCharacters(text) : TextInputFold.Fold(text, engine.Literate), time, key, text);

                // The key went up before its commit resolved, so end the hold now.
                if (heldKey is Key held && !isDown(held))
                    releaseHold(time);

                return typed;
            }

            /// <summary>
            /// Types committed characters at <paramref name="time"/>, on the engine already advanced
            /// to it, for both text paths (<see cref="TypeLatinText"/> and the Polyglot IME's
            /// <see cref="TypeCommittedText"/>): each is one <see cref="TypingEngine.ProcessKey"/> call,
            /// recorded when it took. The gates are the ones a typing key had on the key path: nothing
            /// before the line (or the first line's head start) opens, and on a finished line only the
            /// typed-through newline may take a letter. Returns whether anything was typed.
            ///
            /// <para><paramref name="key"/> and <paramref name="text"/> are the Latin press behind the
            /// characters (null on the IME path): a press that opens a hold remembers them, so the key's
            /// release ends it and its OS repeats are dropped.</para>
            /// </summary>
            private bool typeCharacters(IEnumerable<char> characters, double time, Key? key = null, string? text = null)
            {
                if (!engine.LineIsActive && !engine.FirstLineTypingOpensAt(time))
                    return false;

                bool typed = false;

                foreach (char c in characters)
                {
                    // A FINISHED line takes a letter only as the typed-through newline (the era bit
                    // engine.NewlineOnTypedLetter carries), and never a space: the space that closes a
                    // line is the manual newline, which the spacebar's key decides (OnKeyDown).
                    if (engine.IsLineComplete && playfield.CurrentRetypeSelection is null)
                    {
                        var finishedHoldBefore = engine.ActiveHold;

                        if (c != ' ' && engine.NewlineOnTypedLetter && engine.ProcessKey(c, time))
                        {
                            record(c, time);
                            typed = true;
                            rememberHeldKey(key, text, finishedHoldBefore);
                        }

                        continue;
                    }

                    // A retype selection is consumed FIRST, so this press lands on the anchor cell:
                    // mass backspace, then the ordinary judged keypress (backlog 182).
                    collapseSelection(time);

                    var holdBefore = engine.ActiveHold;

                    if (engine.ProcessKey(c, time))
                    {
                        record(c, time);
                        typed = true;
                        rememberHeldKey(key, text, holdBefore);
                    }
                }

                return typed;
            }

            #endregion

            #region Holds

            /// <summary>
            /// The key whose press opened the active hold; its key-up ends it. A key let go during a pause
            /// is seen as a key-up on resume.
            /// </summary>
            private Key? heldKey;

            /// <summary>After a press: if it opened a new hold, remember its key and what that key's OS repeat commits.</summary>
            private void rememberHeldKey(Key? key, string? text, TypingEngine.ActiveHoldInfo? holdBefore)
            {
                if (key != null && engine.ActiveHold is TypingEngine.ActiveHoldInfo hold && hold != holdBefore)
                {
                    heldKey = key;

                    // Only a key still down can repeat.
                    if (text != null && isDown(key.Value))
                    {
                        repeatingKey = key;
                        repeatingText = text;
                    }
                }
            }

            /// <summary>
            /// The held key and its OS-repeated text while it is down. When it stops repeating, its pending
            /// commits are dropped, so the hand-off key pressed during the rollover cannot claim one of them.
            /// </summary>
            private Key? repeatingKey;

            /// <inheritdoc cref="repeatingKey"/>
            private string? repeatingText;

            private void dropHeldKeyRepeats()
            {
                if (repeatingText != null)
                {
                    lock (committedLock)
                        committed.RemoveAll(t => t.Text == repeatingText);
                }

                repeatingKey = null;
                repeatingText = null;
            }

            private bool isDown(Key key) => GetContainingInputManager()?.CurrentState.Keyboard.Keys.IsPressed(key) == true;

            /// <summary>Modifiers never end a hold (Shift for capitals; Windows reports AltGr as Ctrl+Alt).</summary>
            private static bool isModifier(Key k) => k is Key.LShift or Key.RShift or Key.LControl or Key.RControl or Key.LAlt or Key.RAlt or Key.LWin or Key.RWin;

            /// <summary>
            /// Ends the active hold at <paramref name="time"/> and records a release frame. The engine is advanced
            /// first, as a key's own advance (<see cref="advanceEngineTo"/>, so a press still waiting for its
            /// commit is judged no earlier than this release and the recorded frames stay in time order), so a
            /// hold already past its end completes as Great with nothing recorded. Inert during playback.
            /// </summary>
            private void releaseHold(double time)
            {
                heldKey = null;

                if (engine.ActiveHold == null || drawableRuleset?.ReplayScore != null)
                    return;

                advanceEngineTo(time);

                if (engine.ProcessRelease(time))
                    record(TypeBeatReplayFrame.RELEASE, time);
            }

            protected override void OnKeyUp(KeyUpEvent e)
            {
                if (heldKey != null && e.Key == heldKey)
                    releaseHold(Math.Round(Time.Current));

                if (repeatingKey != null && e.Key == repeatingKey)
                    dropHeldKeyRepeats();

                base.OnKeyUp(e);
            }

            #endregion

            /// <summary>The play's half of the key gate's state (see <see cref="TypeBeatKeyGate"/>).</summary>
            private TypeBeatKeyPlay keyPlay => TypeBeatKeyPlay.Of(engine, imeComposition.Length > 0);

            /// <summary>
            /// The key path: <see cref="TypeBeatKeyGate"/> decides which arm a press takes, in the one
            /// order the arms have always had, and this carries it out. The engine is advanced to the
            /// press between the gate's two stages, so the second one reads the engine as it stands at
            /// the press.
            /// </summary>
            protected override bool OnKeyDown(KeyDownEvent e)
            {
                // Millisecond-quantised keystroke time: what the engine judges at, what gets
                // recorded, and what the .osr format can store exactly. Sub-ms quantisation is far
                // below input timing noise (Time.Current is already frame-quantised).
                double time = Math.Round(Time.Current);

                // Another key going down ends a hold first, as its own recorded frame, and drops the held key's repeats.
                if (engine.ActiveHold != null && !e.Repeat && e.Key != heldKey && !isModifier(e.Key))
                    releaseHold(time);

                if (repeatingKey != null && !e.Repeat && e.Key != repeatingKey && !isModifier(e.Key))
                    dropHeldKeyRepeats();

                var route = TypeBeatKeyGate.Route(keyPlay, pressOf(e));

                if (route.Settled is TypeBeatKeyOutcome settled)
                    return perform(settled, route, time);

                // Advance the engine to the keystroke's timestamp BEFORE gating/judging, so the
                // outcome depends only on (char, time), not on where the last engine tick happened
                // to fall. This is what lets replay playback reproduce the run exactly.
                advanceEngineTo(time);

                var line = TypeBeatKeyLine.Of(engine, time, playfield.CurrentRetypeSelection is not null);
                return perform(TypeBeatKeyGate.Decide(line, route), route, time);
            }

            /// <summary>
            /// <paramref name="e"/> as the gate reads it, with the word-level gesture (if any) it
            /// triggers under the user's CURRENT bindings (backlog 183; backlog 182 hardcoded
            /// Ctrl+Backspace and Ctrl+A here).
            /// </summary>
            private TypeBeatKeyPress pressOf(KeyDownEvent e)
            {
                var gesture = TypeBeatInputManager.ResolveGesture(e, rulesetInput?.CurrentGestureBindings
                                                                    ?? (fallbackBindings ??= new TypeBeatRuleset().GetDefaultKeyBindings()));

                return new TypeBeatKeyPress(e.Key, e.Repeat, e.ShiftPressed, e.ControlPressed, e.AltPressed, e.SuperPressed, gesture);
            }

            /// <summary>Carries out the arm the gate chose for a press at <paramref name="time"/>. Returns whether the press is swallowed.</summary>
            private bool perform(TypeBeatKeyOutcome outcome, TypeBeatKeyRoute route, double time)
            {
                bool freshTextPress = route.FreshTextPress;

                switch (outcome)
                {
                    case TypeBeatKeyOutcome.Swallow:
                        return dropPress(freshTextPress, time, true);

                    case TypeBeatKeyOutcome.FallThroughTyping:
                        pressFor(PressPlan.Type, time, route.Press.Key);
                        return false;

                    case TypeBeatKeyOutcome.Erase:
                        return erase(route.Gesture == TypeBeatAction.EraseWord, freshTextPress, time);

                    case TypeBeatKeyOutcome.SelectBackToTypo:
                        return selectBackToTypo(freshTextPress, time);

                    case TypeBeatKeyOutcome.SkipLine:
                        return skipLine(freshTextPress, time);

                    case TypeBeatKeyOutcome.NewLine:
                        return newLine(route.Press.Key == Key.Space, freshTextPress, time);

                    case TypeBeatKeyOutcome.TypedNewLine:
                        return pressFor(PressPlan.Type, time, route.Press.Key);

                    case TypeBeatKeyOutcome.ImeSpace:
                        collapseSelection(time);

                        if (engine.ProcessKey(' ', time))
                            record(' ', time);

                        return true;

                    case TypeBeatKeyOutcome.PositionalDigit:
                        TypingKeys.TryPositionalDigit(route.Press.Key, out char digit);
                        dropPress(true, time);
                        TypeLatinText(digit.ToString(), time, route.Press.Key);
                        return true;

                    case TypeBeatKeyOutcome.Type:
                    case TypeBeatKeyOutcome.TypeSpace:
                        bool typed = pressFor(outcome == TypeBeatKeyOutcome.TypeSpace ? PressPlan.Space : PressPlan.Type, time, route.Press.Key);

                        // A letter, a digit and the spacebar are swallowed whatever they committed, as
                        // they always were. The rest of the block (the punctuation positions, the keypad
                        // operators) is swallowed exactly when its commit TYPED: under Literate a mark is
                        // a cell and is kept from its global binding, while an inert legend (a US '`' or
                        // '=') falls through to it as it always did; and without the mod a German 'ß' on
                        // the US Minus position types "ss" and must not also nudge the offset, while a US
                        // hyphen there stays inert and still reaches that binding.
                        return TypingKeys.AlwaysSwallowed(route.Press.Key) || typed;

                    case TypeBeatKeyOutcome.FallThrough:
                    default:
                        return dropPress(freshTextPress, time);
                }
            }

            /// <summary>A backspace, plain or by word (<paramref name="word"/>), on a play that has something to erase. Swallowed.</summary>
            private bool erase(bool word, bool freshTextPress, double time)
            {
                // Repeat honoured: hold to erase, monkeytype-style. Only an erase that actually
                // changed state is recorded.
                //
                // The ERASE WORD binding takes the whole word (backlog 182). A live SELECTION
                // takes precedence over either width: an erase key over one collapses it and
                // types nothing, which is the same mass erase a letter would do before landing.
                if (!collapseSelection(time))
                {
                    if (word)
                        eraseBackTo(engine.WordBackspaceTarget, time);
                    else if (engine.ProcessBackspace())
                        record(TypeBeatReplayFrame.BACKSPACE, time);
                }

                return dropPress(freshTextPress, time, true);
            }

            /// <summary>The select-back-to-typo gesture. Swallowed, effective or not.</summary>
            private bool selectBackToTypo(bool freshTextPress, double time)
            {
                // Offer the run back to the earliest unfixed mistake for retyping. NOT gated on
                // AllowWrongInput. The query itself is the gate: it answers -1 when there is
                // no wrong or abandoned cell behind the caret to retype.
                //
                // The key is still SWALLOWED either way, effective or not, which is unchanged:
                // the default Ctrl+A carries meaning elsewhere in the game that gameplay must
                // not start triggering.
                int anchor = engine.RetypeSelectionAnchor;

                // Nothing behind the caret: a genuine no-op, nothing to select and nothing to
                // clear (a selection can only exist where the query just answered). Pressing it
                // again with one already open simply recomputes the same range.
                if (anchor >= 0)
                    playfield.applyRetypeSelection(new RetypeSelection(engine.ActiveLineIndex, anchor, engine.CaretIndex));

                return dropPress(freshTextPress, time, true);
            }

            /// <summary>The line skip gesture. Swallowed only when it was effective.</summary>
            private bool skipLine(bool freshTextPress, double time)
            {
                // GIVE UP THE REST OF THE LINE (backlog 241). One engine call, which parks the
                // caret past the last cell and lets the roll or the snap carry it onward; the
                // cells left behind are judged by the seal at the line's own deadline, exactly as
                // they would be for a player who just stopped typing. Nothing is gated on
                // AllowWrongInput here, unlike the two erasing gestures: a skip writes nothing
                // into a cell, so it means the same thing under Gatekeeper.
                //
                // A live retype SELECTION is deliberately NOT collapsed first: collapsing erases
                // back to the anchor, and a player abandoning the line is not asking to unmake
                // the characters they got right. Moving the caret makes it stale, and the
                // playfield's own staleness check drops it on the next frame.
                //
                // Only an EFFECTIVE press is swallowed and recorded. On a caret that is already
                // parked (or on a line typed out) the engine no-ops and the press falls through
                // to its global binding, which is what it did before this gesture existed.
                if (!engine.ProcessEnter(time))
                    return dropPress(freshTextPress, time);

                record(TypeBeatReplayFrame.ENTER, time);
                return dropPress(freshTextPress, time, true);
            }

            /// <summary>
            /// The manual newline on a finished line, by the spacebar (<paramref name="space"/>) or
            /// Enter, through the same engine entry point the replay feed uses for it. Swallowed when
            /// the engine took it; a press the entry window has not opened for falls through.
            /// </summary>
            private bool newLine(bool space, bool freshTextPress, double time)
            {
                if (space ? engine.ProcessKey(' ', time) : engine.ProcessEnter(time))
                {
                    record(space ? ' ' : TypeBeatReplayFrame.ENTER, time);
                    return dropPress(freshTextPress, time, true);
                }

                return dropPress(freshTextPress, time);
            }

            /// <summary>
            /// The press did everything it means on the key (it fell through, or was a gesture), so
            /// its commit is claimed and discarded rather than left for the next press. Returns
            /// <paramref name="handled"/>, the key's own answer.
            /// </summary>
            private bool dropPress(bool freshTextPress, double time, bool handled = false)
            {
                if (freshTextPress)
                    pressFor(PressPlan.Drop, time);

                return handled;
            }


            /// <summary>
            /// Erase back to <paramref name="target"/> with ordinary
            /// <see cref="TypingEngine.ProcessBackspace"/> calls, recording every one that mutated
            /// state at <paramref name="time"/>: the same (char, time) pairs a player holding the
            /// plain key down would have produced, so a replay re-derives the run bit for bit.
            ///
            /// <para>All of them carry the ONE timestamp the live engine judged them at, which is
            /// what makes the equal-time run safe: the legacy .osr encoding stores integral frame
            /// deltas (a run of zeroes here) and its decoder keeps frames of equal time in the order
            /// they were written, so playback performs the identical call sequence.</para>
            /// </summary>
            private void eraseBackTo(int target, double time)
            {
                while (engine.CaretIndex > target || engine.JapanesePending.Length > 0)
                {
                    int before = engine.CaretIndex;
                    bool pendingRomaji = engine.JapanesePending.Length > 0;
                    // A PARKED typo is cleared where it sits (see TypingEngine.ProcessBackspace), so the
                    // press that removes it mutates the run without moving the caret. Read BEFORE the
                    // press: that is the only iteration the guard below has to let through.
                    bool parked = engine.CaretOnParkedTypo;

                    if (!engine.ProcessBackspace())
                        break;

                    record(TypeBeatReplayFrame.BACKSPACE, time);

                    // Defensive termination only. Every erase that reports a mutation moves the
                    // caret back, but one that reclaimed abandoned cells at the head of a line can
                    // land on 0 and be auto-skipped forward again, and a gesture must never spin.
                    //
                    // THE PARKED EXCEPTION IS NOT THAT CASE: clearing a parked typo is a real
                    // mutation of the cell the caret is on, with the caret deliberately unmoved, and
                    // breaking here would leave the rest of the selection standing - a retype
                    // selection opened over a word skip ends on exactly such a cell whenever the
                    // player has since typed characters into the gap. The next press steps back
                    // normally, so the run terminates on its own. Pending romaji is likewise a
                    // finite series of erases without caret movement, before the selected cells.
                    if (engine.CaretIndex >= before && !parked && !pendingRomaji)
                        break;
                }
            }

            /// <summary>
            /// Collapse a live retype selection: a mass backspace to its anchor, recorded like any
            /// other erase run. Returns whether there was one to collapse, so the caller can tell
            /// "the selection ate this key" from "there was nothing there". The selection is dropped
            /// BEFORE the erases so the playfield's own staleness check cannot race them.
            /// </summary>
            private bool collapseSelection(double time)
            {
                if (playfield.CurrentRetypeSelection is not RetypeSelection selection)
                    return false;

                playfield.applyRetypeSelection(null);
                eraseBackTo(selection.StartCell, time);
                return true;
            }
        }
    }
}
