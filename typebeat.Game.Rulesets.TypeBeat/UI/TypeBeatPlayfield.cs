// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Input.Events;
using osu.Framework.Timing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Configuration;
using typebeat.Game.Rulesets.Objects.Drawables;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects.Drawables;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Rulesets.UI;
using osuTK.Graphics;
using osuTK.Input;

namespace typebeat.Game.Rulesets.TypeBeat.UI
{
    /// <summary>
    /// Hosts the monkeytype lyric stage. The regression-anchored <see cref="TypingEngine"/> is
    /// the gameplay/judgement authority; invisible <see cref="DrawableTypeBeatHitObject"/>s
    /// mirror its judgements into osu's scoring pipeline.
    ///
    /// The LyricOffsetMs config value is applied at a single seam: an offset clock container
    /// wrapping the engine ticker, the stage, the HUD extras AND the key handler, so
    /// judgement, sung sweep, approach cue and HUD shift together (gameplay time = audio - offset),
    /// exactly like the standalone game's clock-layer offset.
    /// </summary>
    [Cached]
    public partial class TypeBeatPlayfield : Playfield
    {
        /// <summary>The gameplay/judgement authority. Public for cross-assembly tests.</summary>
        public TypingEngine Engine { get; }

        private readonly Dictionary<int, DrawableTypeBeatHitObject> lineDrawables = new Dictionary<int, DrawableTypeBeatHitObject>();

        private readonly BindableDouble lyricOffset = new BindableDouble();

        private readonly Bindable<KeyboardLayout> keyboardLayout = new Bindable<KeyboardLayout>(KeyboardLayout.Qwerty);

        // Tracks the user's background dim so a 100% dim restores the flat serika-dark backdrop
        // over the (then fully-black) beatmap image/video.
        private readonly Bindable<double> backgroundDim = new Bindable<double>();

        private FramedOffsetClock lyricClock = null!;

        private LyricStage stage = null!;

        /// <summary>Screen-space centre of the typing caret when it is visible: the Flashlight mod's
        /// reveal point. Returns false while no line is active (caret hidden), so the mod can fade.</summary>
        public bool TryGetCaretScreenPosition(out osuTK.Vector2 position)
        {
            if (stage.IsNotNull() && stage.PlayerCaretVisible)
            {
                position = stage.PlayerCaretScreenPosition;
                return true;
            }

            position = default;
            return false;
        }

        /// <summary>Screen-space point where the upcoming line's caret will appear while its boundary
        /// cue counts in; the Flashlight mod snaps ahead to it before the caret arrives.</summary>
        public bool TryGetUpcomingCaretScreenPosition(out osuTK.Vector2 position)
        {
            if (stage.IsNotNull() && stage.TryGetUpcomingCaretScreenPosition(out position))
                return true;

            position = default;
            return false;
        }

        // Both cached by Player; absent in bare drawable-ruleset test scenes.
        [Resolved]
        private ScoreProcessor? scoreProcessor { get; set; }

        [Resolved]
        private HealthProcessor? healthProcessor { get; set; }

        // Cached by DrawableTypeBeatRuleset for its subtree; absent when the playfield is
        // constructed bare in tests. Carries the replay seams: ReplayScore (playback source)
        // and RecordTypingInput (recording sink).
        [Resolved]
        private DrawableTypeBeatRuleset? drawableRuleset { get; set; }

        public TypeBeatPlayfield(TypingEngine engine)
        {
            Engine = engine;
        }

        [BackgroundDependencyLoader(true)]
        private void load(TypeBeatRulesetConfigManager? config, IBindable<WorkingBeatmap>? beatmap, OsuConfigManager? osuConfig)
        {
            config?.BindWith(TypeBeatRulesetSetting.LyricOffsetMs, lyricOffset);
            config?.BindWith(TypeBeatRulesetSetting.KeyboardLayout, keyboardLayout);

            // The wrong-input model is fixed for the play; the engine reads the flag on every key.
            if (config != null)
                Engine.AllowWrongInput = config.Get<bool>(TypeBeatRulesetSetting.AllowWrongInput);

            // The Player already renders the beatmap background image (dimmed) and, when
            // "beatmap storyboard/video" is on, the video, both BELOW the ruleset. Historically
            // this playfield painted an opaque serika-dark box over all of it (the monkeytype flat
            // look), which blacked the real background out. Only cover it when there is nothing to
            // show: reveal the image/video behind a readability scrim, else keep the flat panel.
            bool showStoryboard = osuConfig?.Get<bool>(OsuSetting.ShowStoryboard) ?? true;
            bool hasImage = !string.IsNullOrEmpty(beatmap?.Value.BeatmapInfo.Metadata.BackgroundFile);
            bool hasVideo = beatmap?.Value.Storyboard.HasDrawable == true;
            bool hasBackdrop = hasImage || (hasVideo && showStoryboard);

            Drawable backdrop;

            if (hasBackdrop)
            {
                // Reveal the image/video behind a readability scrim, but keep an opaque flat panel
                // ready on top: at 100% background dim the video/image is fully black, so fade the
                // classic monkeytype backdrop back in. Bound live to DimLevel so the settings slider
                // toggles it without re-entering gameplay.
                var dimCover = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = TypeBeatStyle.Background,
                    Alpha = 0f,
                };

                backdrop = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Children = new Drawable[] { createReadabilityScrim(), dimCover },
                };

                osuConfig?.BindWith(OsuSetting.DimLevel, backgroundDim);
                backgroundDim.BindValueChanged(e => dimCover.FadeTo(e.NewValue >= 1 ? 1f : 0f, 150, Easing.OutQuint), true);
            }
            else
            {
                backdrop = new Box { RelativeSizeAxes = Axes.Both, Colour = TypeBeatStyle.Background };
            }

            // Positive offset = lyrics later relative to the music => lyric time runs behind audio.
            // The source set here is provisional: the playfield's Clock is swapped after load
            // (FrameStabilityContainer installs the frame-stable gameplay clock on itself), so a
            // load-time capture can be a stale non-gameplay clock whose time is app uptime,
            // which ran the engine seconds ahead of the audio. Update() re-points the source at
            // the current Clock every frame, before any child of the lyric subtree ticks.
            lyricClock = new FramedOffsetClock(Clock, processSource: false) { Offset = -lyricOffset.Value };

            // Song-paced held-key repeat (see HeldKeyRepeater). The key handler owns the hold
            // state; the engine ticker pumps it, so due repeats are applied BEFORE the frame's own
            // engine tick and the engine is never handed a timestamp it has already passed.
            var repeater = new HeldKeyRepeater(Engine, (c, t) => drawableRuleset?.RecordTypingInput(c, t));

            AddRangeInternal(new Drawable[]
            {
                backdrop,
                // Invisible scoring drawables (results-only; the stage does the rendering).
                HitObjectContainer,
                new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Clock = lyricClock,
                    Children = new Drawable[]
                    {
                        // Ticks the engine FIRST in this subtree so the stage and HUD read
                        // fresh engine state for the same lyric-clock frame. Doubles as the
                        // replay feeder when a replay score is attached.
                        new EngineTicker(Engine, drawableRuleset, repeater),
                        stage = new LyricStage(Engine),
                        new TypeBeatHudOverlay(Engine),
                        new TypeBeatKeyHandler(Engine, keyboardLayout, drawableRuleset, repeater),
                    },
                },
            });
        }

        /// <summary>
        /// Sits above the (already dimmed) beatmap image/video and below the lyrics: a light
        /// full-bleed tint so a bright video frame never blows out the text, plus a soft dark band
        /// centred on the 3-line lyric stack that fades out top and bottom, keeping the words
        /// legible on any footage while leaving most of the video visible.
        /// </summary>
        private static Drawable createReadabilityScrim() => new Container
        {
            RelativeSizeAxes = Axes.Both,
            Children = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = Color4.Black.Opacity(0.2f),
                },
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Anchor = Anchor.Centre,
                    Origin = Anchor.BottomCentre,
                    Height = 0.3f,
                    Colour = ColourInfo.GradientVertical(Color4.Black.Opacity(0f), Color4.Black.Opacity(0.5f)),
                },
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Anchor = Anchor.Centre,
                    Origin = Anchor.TopCentre,
                    Height = 0.3f,
                    Colour = ColourInfo.GradientVertical(Color4.Black.Opacity(0.5f), Color4.Black.Opacity(0f)),
                },
            },
        };

        protected override void LoadComplete()
        {
            base.LoadComplete();

            Engine.CharJudged += onCharJudged;
            Engine.LineSealed += onLineSealed;
            Engine.WrongKeyRejected += onWrongKeyRejected;
        }

        protected override void Update()
        {
            base.Update();

            // The playfield updates before the lyric subtree's children, so fixing the source
            // here guarantees the engine never consumes a frame of the stale load-time clock.
            if (lyricClock.Source != Clock)
                lyricClock.ChangeSource(Clock);

            // Live-applied (M7 will surface a slider; the setting already works end-to-end).
            lyricClock.Offset = -lyricOffset.Value;
        }

        protected override void OnNewDrawableHitObject(DrawableHitObject drawableHitObject)
        {
            base.OnNewDrawableHitObject(drawableHitObject);

            if (drawableHitObject is DrawableTypeBeatHitObject line)
                lineDrawables[line.HitObject.LineIndex] = line;
        }

        private void onCharJudged(CharJudgement judgement)
        {
            // The accepted char reaches the health processor as its own Great/Ok/Meh (or, for a
            // wrong char in allow-wrong-input mode, Miss) result via ApplyCharJudgement below, which
            // is what recovers HP; no separate reset needed.
            if (lineDrawables.TryGetValue(judgement.LineIndex, out var line))
                line.ApplyCharJudgement(judgement);

            // Fletcher's rush cap breaks combo on a press that is still judged Perfect/Good/Ok, so the
            // hit result alone (a Great/Ok/Meh, which INCREMENTS osu's combo) cannot carry the break.
            // Mirror the engine's own combo by hand, after the result has been applied, exactly as
            // onWrongKeyRejected does for a rejected key. Gated on the mod so the default path, where
            // every ComboAfter == 0 judgement already maps to a Miss, is untouched.
            if (Engine.FletcherEnabled && judgement.ComboAfter == 0 && scoreProcessor != null)
                scoreProcessor.Combo.Value = 0;
        }

        private void onWrongKeyRejected(char c)
        {
            // A rejected key produces no hit result, so mirror the engine's combo break into
            // osu's score processor by hand (its combo bindable is maintained incrementally).
            if (scoreProcessor != null)
                scoreProcessor.Combo.Value = 0;

            (healthProcessor as TypeBeatHealthProcessor)?.ApplyWrongKeyStreak(Engine.ConsecutiveWrongKeys);
        }

        private void onLineSealed(LineSealResult result)
        {
            if (lineDrawables.TryGetValue(result.LineIndex, out var line))
                line.ApplySealResults();
        }

        protected override void Dispose(bool isDisposing)
        {
            Engine.CharJudged -= onCharJudged;
            Engine.LineSealed -= onLineSealed;
            Engine.WrongKeyRejected -= onWrongKeyRejected;
            base.Dispose(isDisposing);
        }

        /// <summary>
        /// Ticks the <see cref="TypingEngine"/> from inside the lyric-offset clock subtree so it
        /// reads this frame's freshly-processed lyric time (via <c>Time.Current</c>). Placed
        /// before the visual children so they see fresh engine state the same frame.
        ///
        /// <para>Doubles as the REPLAY FEEDER: when the drawable ruleset has a replay score
        /// attached (watching a replay, or the Autoplay mod), every due frame is fed straight into
        /// the engine as <c>Update(frame.Time)</c> followed by the recorded keystroke at that exact
        /// time, which is the identical call sequence live play makes (see
        /// <see cref="TypeBeatKeyHandler"/>). Judgement therefore depends only on the recorded
        /// (char, time) sequence, never on playback frame rate or the local lyric-offset setting.
        /// The lyric clock only schedules WHEN due frames are applied and drives the visuals.</para>
        /// </summary>
        private partial class EngineTicker : Drawable
        {
            private readonly TypingEngine engine;
            private readonly DrawableTypeBeatRuleset? drawableRuleset;
            private readonly HeldKeyRepeater repeater;

            private Game.Replays.Replay? activeReplay;
            private int nextFrameIndex;

            public EngineTicker(TypingEngine engine, DrawableTypeBeatRuleset? drawableRuleset, HeldKeyRepeater repeater)
            {
                this.engine = engine;
                this.drawableRuleset = drawableRuleset;
                this.repeater = repeater;
            }

            protected override void Update()
            {
                base.Update();

                var replay = drawableRuleset?.ReplayScore?.Replay;

                if (replay != null)
                {
                    // Playback owns the engine: the recorded frames already CONTAIN whatever repeats
                    // the run held, so a live hold must never overlay them.
                    repeater.Cancel();

                    // The replay can be swapped mid-play (editor autoplay toggle); restart feeding.
                    if (!ReferenceEquals(replay, activeReplay))
                    {
                        activeReplay = replay;
                        nextFrameIndex = 0;
                    }

                    var frames = replay.Frames;

                    while (nextFrameIndex < frames.Count && frames[nextFrameIndex].Time <= Time.Current)
                    {
                        if (frames[nextFrameIndex] is TypeBeatReplayFrame frame)
                            applyFrame(frame);

                        nextFrameIndex++;
                    }
                }
                else
                {
                    // Held-key repeats due this frame, applied before the tick below so the engine is
                    // only ever advanced forwards (an Update at an already-passed time would
                    // re-accrue that stretch of active time into the WPM clock).
                    repeater.Pump(Time.Current);
                }

                engine.Update(Time.Current);
            }

            private void applyFrame(TypeBeatReplayFrame frame)
            {
                if (frame.IsConfig)
                {
                    // Recorded machine's judgement-relevant setting wins over local config.
                    engine.AllowWrongInput = frame.AllowWrongInput;
                    return;
                }

                // Exactly the live sequence: state advances to the keystroke's timestamp, then the
                // keystroke applies. Update is monotonic/idempotent, so the ticker's own per-frame
                // updates interleaving at other times cannot change any judgement outcome.
                engine.Update(frame.Time);

                if (frame.IsBackspace)
                    engine.ProcessBackspace();
                else
                    engine.ProcessKey(frame.Character, frame.Time);
            }
        }

        /// <summary>
        /// Full-keyboard typing input, taken via raw <see cref="OnKeyDown"/> inside the ruleset
        /// input manager's subtree (raw key events pass through RulesetInputManager to its
        /// children; children receive them before the key-binding container, so typing letters
        /// wins over the vestigial Z/X action bindings). OS/framework key-repeat is honoured ONLY
        /// for backspace (hold to erase); a held character key never machine-guns judgements at the
        /// keyboard's own repeat rate. Ctrl/Alt combos fall through to framework shortcuts.
        ///
        /// <para>A held character key instead re-fires at the SONG's cadence, one press per
        /// upcoming cell target, via <see cref="HeldKeyRepeater"/> (pumped by the
        /// <see cref="EngineTicker"/>): the handler's job is only to capture the hold, drop it on
        /// key-up/focus-loss/backspace, and keep the framework's own repeat out.</para>
        ///
        /// <para>Backspace is live ONLY in allow-wrong-input mode, the only model where a wrong char
        /// lands in a cell and is thus worth erasing; in strict (default) play the key is swallowed
        /// and does nothing at all. Replay playback is unaffected: recorded backspace frames go
        /// straight to the engine (see <see cref="EngineTicker"/>).</para>
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
            private readonly IBindable<KeyboardLayout> keyboardLayout;
            private readonly DrawableTypeBeatRuleset? drawableRuleset;
            private readonly HeldKeyRepeater repeater;

            public TypeBeatKeyHandler(TypingEngine engine, IBindable<KeyboardLayout> keyboardLayout, DrawableTypeBeatRuleset? drawableRuleset, HeldKeyRepeater repeater)
            {
                this.engine = engine;
                this.keyboardLayout = keyboardLayout;
                this.drawableRuleset = drawableRuleset;
                this.repeater = repeater;
                RelativeSizeAxes = Axes.Both;
            }

            public override bool HandleNonPositionalInput => true;

            public override bool AcceptsFocus => true;

            public override bool RequestsFocus => true;

            protected override bool OnKeyDown(KeyDownEvent e)
            {
                // Let framework shortcuts (Ctrl/Alt combos) fall through.
                if (e.ControlPressed || e.AltPressed)
                    return false;

                // Millisecond-quantised keystroke time: what the engine judges at, what gets
                // recorded, and what the .osr format can store exactly. Sub-ms quantisation is far
                // below input timing noise (Time.Current is already frame-quantised).
                double time = Math.Round(Time.Current);

                // Advance the engine to the keystroke's timestamp BEFORE gating/judging, so the
                // outcome depends only on (char, time), not on where the last engine tick happened
                // to fall. This is what lets replay playback reproduce the run exactly.
                engine.Update(time);

                // While the engine has no active line (pre-roll, a dead zone, or after the final
                // line) typing is inert, so DON'T swallow the key; let it fall through to global
                // key bindings so Space reaches GlobalAction.SkipCutscene and the intro / mid-song
                // instrumental skip overlays can act.
                if (!engine.LineIsActive)
                    return false;

                // Fletcher parks the caret at the head of the next line the instant you finish one,
                // so unlike default play a line stays "active" straight through an instrumental gap
                // and Space would be eaten as a (wrong, combo-breaking) keystroke instead of reaching
                // the mid-song skip overlay. Narrowly restore the fall-through: only Space, only while
                // the SONG itself is in a dead zone, and only before the player has started the parked
                // line. One keystroke into the line, or anywhere the song is actually playing a line,
                // Space is a typing key again, so rushing into the next line is never blocked.
                if (engine.FletcherEnabled && e.Key == Key.Space && !engine.SongWindowOpen && engine.ActiveLineUntouched)
                    return false;

                if (e.Key == Key.BackSpace)
                {
                    // Correcting, not sustaining: end any hold rather than let it keep typing over
                    // the erase. (Backspace's own OS repeat is untouched, see below.)
                    repeater.Cancel();

                    // Erasing only ever has something to undo in ALLOW-WRONG-INPUT mode: that is the
                    // one model where a wrong char lands in a cell. In strict (default) play a wrong
                    // key is rejected outright, so nothing erasable is ever written, and re-typing an
                    // already-correct cell (freestyle cells included, whose press is a CORRECT hit) is
                    // scoring-inert. Backspace is therefore inert-by-design in strict play, and is
                    // gated off entirely: no engine call, nothing recorded. Gated at the INPUT layer,
                    // not in the engine, so the JS port of TypingEngine stays byte-compatible.
                    //
                    // The gate reads the ENGINE flag, the same value the replay CONFIG frame carries,
                    // so it can never disagree with the model the play is judged under. It applies to
                    // LIVE input only: replay playback feeds recorded backspace frames straight into
                    // the engine (see EngineTicker.applyFrame), so an old replay still plays back
                    // exactly as recorded.
                    //
                    // The key is still swallowed rather than passed on: backspace carries a global
                    // binding (GlobalAction.DeselectAllMods) and editor semantics that gameplay must
                    // not start triggering just because the setting is off.
                    if (!engine.AllowWrongInput)
                        return true;

                    // Repeat honoured: hold to erase, monkeytype-style. Handled BEFORE the
                    // line-complete fall-through: backspacing at line end must keep working (it is
                    // how typed-through wrong chars get fixed in allow-wrong-input mode). Only an
                    // erase that actually changed state is recorded.
                    if (engine.ProcessBackspace())
                        drawableRuleset?.RecordTypingInput(TypeBeatReplayFrame.BACKSPACE, time);

                    return true;
                }

                // The active line is fully typed: the engine is inert for character keys
                // (ProcessKey no-ops at line end), so let them fall through too. This is the state
                // the player holds for the ENTIRE length of a real instrumental gap; the decoder
                // keeps the previous line's window open (and thus active) until the next line
                // starts, so without this fall-through Space could never reach the mid-song skip
                // overlay on any real map. While the line is active and INCOMPLETE every typeable
                // key (Space included) is still consumed for typing, so a skip can never eat a
                // live keystroke.
                if (engine.IsLineComplete)
                    return false;

                // Pass Shift through so held-Shift keys produce capitals, required for the
                // Literate (case-sensitive) mod; folded away harmlessly in normal play.
                if (KeyCharMap.TryMap(e.Key, keyboardLayout.Value, e.ShiftPressed, out char c))
                {
                    // The framework's own auto-repeat is discarded outright: a held character key is
                    // re-fired by the SONG, not by the keyboard's repeat rate.
                    if (!e.Repeat)
                    {
                        if (engine.ProcessKey(c, time))
                            drawableRuleset?.RecordTypingInput(c, time);

                        // Arm the song-paced repeat from the post-press caret. The captured char is
                        // the EFFECTIVE one (layout-mapped, Shift applied), so a Literate-mod
                        // Shift+A hold repeats 'A' even if Shift is let go mid-hold.
                        repeater.BeginHold(e.Key, c, time);
                    }

                    return true;
                }

                return false;
            }

            protected override void OnKeyUp(KeyUpEvent e)
            {
                repeater.Release(e.Key);
                base.OnKeyUp(e);
            }

            protected override void OnFocusLost(FocusLostEvent e)
            {
                // Pause overlay, alt-tab, anything that steals focus: the matching key-up may never
                // arrive, so drop the hold instead of letting it type on unattended.
                repeater.Cancel();
                base.OnFocusLost(e);
            }
        }
    }
}
