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
using typebeat.Game.Screens.Play;
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

            // The wrong-input model is fixed for the play and is no longer a setting (backlog 107):
            // typing wrong chars through is the default, and TypeBeatModGatekeeper is the only thing
            // that turns it off, via ApplyToDrawableRuleset.

            // Space-to-skip-a-word IS a setting, and is read ONCE here rather than bound live: it
            // decides how a space is judged, and the replay CONFIG frame stamps whatever the engine
            // holds at the first keystroke, so a value that could change mid-play would leave the
            // header describing only part of the run. Absent config (a bare test scene) leaves the
            // engine's own default, which is off.
            if (config != null)
                Engine.SpaceSkipsWord = config.Get<bool>(TypeBeatRulesetSetting.SpaceSkipsWord);

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
                        new EngineTicker(Engine, drawableRuleset),
                        stage = new LyricStage(Engine),
                        new TypeBeatHudOverlay(Engine),
                        new TypeBeatKeyHandler(Engine, keyboardLayout, drawableRuleset),
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
            Engine.Mistyped += onMistyped;
            Engine.ComboRestored += onComboRestored;
            Engine.TypoErased += onTypoErased;
            Engine.Rewound += onRewound;
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
            // An accepted char reaches the health processor as its own Great/Ok/Meh result via
            // ApplyCharJudgement below, which is what recovers HP. A WRONG char reaches it as no
            // result at all (backlog 109): its cell's result is deferred until the cell is corrected
            // or sealed on, so there is nothing there to carry HP either.
            if (lineDrawables.TryGetValue(judgement.LineIndex, out var line))
                line.ApplyCharJudgement(judgement);

            // So HP is settled for a typo separately from its result (backlog 166), and at the same
            // moment every other judgement settles it: the keypress. Waiting for the seal made the
            // one account a typist watches while typing lag a line behind the mistake it was
            // reporting. The drain is given back if the player backspaces the character away (see
            // onTypoErased), which is what keeps a FIXED typo costing exactly what it did before:
            // nothing beyond what the corrected retype earns.
            //
            // The rejection model needs nothing here: a rejected key writes no cell, raises no
            // CharJudged, and already drains at its own keypress (see onWrongKeyRejected).
            if (judgement.Type == JudgementType.WrongChar)
                (healthProcessor as TypeBeatHealthProcessor)?.ApplyTypoDrain();

            // Fletcher's rush cap breaks combo on a press that is still judged Great/Ok/Meh, so the
            // hit result alone (a Great/Ok/Meh, which INCREMENTS osu's combo) cannot carry the break.
            // Mirror the engine's own combo by hand, after the result has been applied, exactly as
            // onMistyped does for a wrong keypress. Gated on the mod so the default path is untouched:
            // there every ComboAfter == 0 judgement either maps to a Miss (which breaks osu's combo
            // itself) or is a WrongChar, whose break onMistyped has already carried.
            if (Engine.FletcherEnabled && judgement.ComboAfter == 0 && scoreProcessor != null)
                scoreProcessor.Combo.Value = 0;
        }

        /// <summary>
        /// Every wrong KEYPRESS, in both input modes (see <see cref="TypingEngine.Mistyped"/>), and
        /// since backlog 109 the single seam carrying BOTH of the consequences a wrong keypress has
        /// on the submitted account: the mistype count and the combo break.
        ///
        /// <para>Neither model raises a judgement RESULT for a wrong keypress any more. A rejected
        /// key never did, and a typed-through wrong char now defers its cell's result until the cell
        /// is corrected or sealed on. osu's <see cref="ScoreProcessor.Combo"/> is maintained
        /// incrementally off results, so with no result to carry the break it has to be mirrored by
        /// hand here, or the submitted <c>max_combo</c> would count straight on through the rest of
        /// the line after a break the engine has already taken.</para>
        ///
        /// <para>Setting the bindable directly, rather than folding the reset into
        /// <see cref="TypeBeatScoreProcessor.RecordMistype"/>, is the same choice the rejection path
        /// has always made: <c>Combo</c> is a plain bindable, whereas a result would also move
        /// <c>HighestCombo</c>, the judged count and accuracy. <c>HighestCombo</c> needs no update
        /// because it only ever grows and this only shrinks <c>Combo</c>.</para>
        ///
        /// <para>This is the ONLY break a wrong keypress costs (backlog 122), and since backlog 124
        /// it is the cell's whole combo consequence in both directions: the result the cell resolves
        /// with at the seal is a hit, which would otherwise EXTEND the run by one, so
        /// <see cref="onLineSealed"/> applies it combo-neutral
        /// (<see cref="TypeBeatScoreProcessor.MarkComboNeutral"/>).</para>
        /// </summary>
        private void onMistyped()
        {
            if (scoreProcessor != null)
                scoreProcessor.Combo.Value = 0;

            (scoreProcessor as TypeBeatScoreProcessor)?.RecordMistype();
        }

        /// <summary>
        /// The player went back and corrected a typo, so the streak that typo's keypress broke
        /// resumes (backlog 140, see <see cref="TypingEngine.ComboRestored"/>). Exactly the mirror
        /// image of <see cref="onMistyped"/>, at the same seam and for the same reason: no
        /// judgement result carries a restore, so osu's incrementally-maintained combo has to be
        /// moved by hand or the submitted <c>max_combo</c> would keep counting from zero.
        ///
        /// <para>The engine raises this BEFORE the corrected retype's own judgement, so the result
        /// applied a moment later by <see cref="onCharJudged"/> is weighted by the resumed streak.
        /// That is what makes fixing a typo worth SCORE and not only accuracy.</para>
        /// </summary>
        private void onComboRestored(int streak) => (scoreProcessor as TypeBeatScoreProcessor)?.RestoreCombo(streak);

        /// <summary>
        /// A backspace took a wrong character back out of its cell (backlog 140's other half, made
        /// visible by backlog 166): refund the HP its keypress drained. HEALTH only, and health is
        /// the only account with anything to give back here: the mistype count is spent, and the
        /// combo the keypress broke is restored by the corrected RETYPE (see
        /// <see cref="onComboRestored"/>), not by the erase.
        ///
        /// <para>The refund rides on the ERASE rather than on the fix so that erasing a typo and
        /// leaving the cell empty is priced as the miss it then is (one drain at the seal) instead
        /// of as a typo plus a miss.</para>
        /// </summary>
        private void onTypoErased() => (healthProcessor as TypeBeatHealthProcessor)?.RefundTypoDrain();

        private void onWrongKeyRejected(char c)
        {
            // The combo break rides on Mistyped (see onMistyped), which fires for this key too, one
            // event earlier and in both input models. The mash guard is all that is left here,
            // because only the rejection model ever accrues the consecutive-wrong-key streak.
            (healthProcessor as TypeBeatHealthProcessor)?.ApplyWrongKeyStreak(Engine.ConsecutiveWrongKeys);
        }

        /// <summary>
        /// The line ran out of time: every cell the play never resolved takes its result now. Two
        /// results, not one (backlog 124): a cell nobody typed is a MISS, a cell left holding a
        /// typed-through wrong character is an unfixed TYPO, which is a hit. Only the engine knows
        /// which, so the decision is made here and handed down.
        ///
        /// <para>The typo's hit is applied COMBO-NEUTRAL. Its combo break was taken at the keypress
        /// (see <see cref="onMistyped"/>), and a hit landing at the seal, after the player has
        /// rebuilt a run through the rest of the line, would otherwise hand back an increment on top
        /// of it. The mark is written immediately before the result is applied, and
        /// <see cref="DrawableTypeBeatHitObject.ApplySealResults"/> only asks about cells it is
        /// actually going to resolve.</para>
        ///
        /// <para>It is HP-neutral for the same shape of reason, and since backlog 166: the typo's HP
        /// was taken at the keypress too (see <see cref="onCharJudged"/>), so
        /// <see cref="TypeBeatHealthProcessor"/> gives its result no health increase either way. The
        /// seal still owns the MISS drain, because a cell nobody typed cannot be known missed before
        /// its line runs out of time on it.</para>
        /// </summary>
        private void onLineSealed(LineSealResult sealResult)
        {
            if (!lineDrawables.TryGetValue(sealResult.LineIndex, out var line))
                return;

            line.ApplySealResults(cellIndex =>
            {
                var result = TypeBeatResultMapping.UnresolvedCellResult(Engine.CellLeftWrong(sealResult.LineIndex, cellIndex), TypoRule.Deferred);

                if (result == TypeBeatResultMapping.UNFIXED_TYPO)
                    (scoreProcessor as TypeBeatScoreProcessor)?.MarkComboNeutral(sealResult.LineIndex, cellIndex);

                return result;
            });
        }

        /// <summary>
        /// The engine has been re-derived to an earlier time after a backwards seek during replay or
        /// autoplay playback (see <see cref="TypingEngine.Rebuild"/>). Reconciles the only part of
        /// the submitted account the framework's own rewind cannot reach.
        ///
        /// <para>ORDERING, which is what makes this safe rather than a double-count.
        /// <see cref="Playfield.Update"/> pops <c>judgedEntries</c> and reverts every result whose
        /// <c>JudgementResult.RawTime</c> is now in the future, and a composite drawable's own
        /// <c>Update</c> runs before its children's, so that revert loop has already fully drained
        /// by the time the engine ticker (a child of this
        /// playfield's lyric-clock subtree) can notice the seek and rebuild. Reverting resets the
        /// result (<c>JudgementResult.Reset</c>), so the cells that were rewound past read
        /// <c>Judged == false</c> again and take a fresh result when playback reaches them a second
        /// time, while the cells BEFORE the seek target keep the one they already have. That is why
        /// the rebuild itself must stay silent: its judgements would be dropped for the first group
        /// and duplicated for the second.</para>
        ///
        /// <para>What the revert cannot reach is the pair of quantities that never travelled on a
        /// result at all (see <see cref="onMistyped"/>): the MISTYPE COUNT, a pure counter, and the
        /// combo-neutral ledger. The count is re-derived from the rebuilt engine, which is
        /// authoritative for exactly the interval that survives the seek
        /// (<see cref="TypingEngine.Mistypes"/> counts the same wrong keypresses
        /// <see cref="TypingEngine.Mistyped"/> announces, one for one). The ledger is dropped, since
        /// every entry that is still owed will be re-marked at its line's seal.</para>
        ///
        /// <para>KNOWN RESIDUE: the hand-mirrored combo BREAKS are not undone, so
        /// <see cref="ScoreProcessor.Combo"/> can read low between the seek target and the first
        /// break after it, at which point the next hand-mirrored break (an absolute write of 0) puts
        /// it back on the engine's value. It is not overwritten from the engine here on purpose: the
        /// two counters are kept equal by mirroring every move, never by one dictating to the other,
        /// and a watched replay's HUD combo is the only thing this reaches. Nothing here can mutate
        /// or re-submit the stored score being watched.</para>
        ///
        /// <para>THE SAME RESIDUE APPLIES TO THE TYPO HP DRAIN (backlog 166), for the same reason:
        /// it does not ride on a result either (see <see cref="onCharJudged"/>), so the framework's
        /// revert cannot give it back, and a seek backwards past a stretch containing typos leaves
        /// the bar reading one drain low per typo in it until they are re-typed on the way forward.
        /// Health is not re-derived here because, unlike the mistype count, the engine does not hold
        /// an authoritative total to copy: HP is the health processor's own running account.</para>
        /// </summary>
        private void onRewound() => (scoreProcessor as TypeBeatScoreProcessor)?.ResyncAfterRewind(Engine.Mistypes);

        protected override void Dispose(bool isDisposing)
        {
            Engine.CharJudged -= onCharJudged;
            Engine.LineSealed -= onLineSealed;
            Engine.WrongKeyRejected -= onWrongKeyRejected;
            Engine.Mistyped -= onMistyped;
            Engine.ComboRestored -= onComboRestored;
            Engine.TypoErased -= onTypoErased;
            Engine.Rewound -= onRewound;
            base.Dispose(isDisposing);
        }

        /// <summary>
        /// The speed-adjusting-mod rate to hand <see cref="TypingEngine.Update"/> so its WPM clock
        /// counts REAL seconds rather than beatmap ones: without it Half Time overstates WPM by 1/0.75
        /// and Double Time understates it by 1/1.5.
        ///
        /// <para><see cref="GameplayClockExtensions.GetTrueGameplayRate"/> is the right source because it
        /// reads the aggregate frequency/tempo actually in force, so it covers DT/NC/HT at ANY custom
        /// slider value, both ramp mods and any future rate mod without enumerating them. Deliberately
        /// NOT <c>PerformancePoints.EligibleRate</c>: that answers a pp-eligibility question and returns
        /// null for a custom rate, but a custom-rate play still has a real typing speed worth showing.</para>
        ///
        /// <para>MUST be sampled per frame, never cached at load: ModWindUp / ModWindDown ramp the rate
        /// across the run. Null (no <see cref="IGameplayClock"/> in the hierarchy) means a bare
        /// drawable-ruleset test scene with no <c>Player</c>, hence no rate mods, hence 1.</para>
        /// </summary>
        private static double wpmClockRate(IGameplayClock? clock) => clock?.GetTrueGameplayRate() ?? 1;

        /// <summary>
        /// Ticks the <see cref="TypingEngine"/> from inside the lyric-offset clock subtree so it
        /// reads this frame's freshly-processed lyric time (via <c>Time.Current</c>). Placed
        /// before the visual children so they see fresh engine state the same frame.
        ///
        /// <para>Doubles as the REPLAY FEEDER: when the drawable ruleset has a replay score
        /// attached (watching a replay, or the Autoplay mod), every due frame is fed straight into
        /// the engine as <c>Update(frame.Time)</c> followed by the recorded keystroke at that exact
        /// time, which is the identical call sequence live play makes (see
        /// <see cref="TypeBeatKeyHandler"/>). That sequence lives in
        /// <see cref="ReplayEngineFeed.Apply"/>, shared with the headless recalculation harness so
        /// the two cannot drift. Judgement therefore depends only on the recorded (char, time)
        /// sequence, never on playback frame rate or the local lyric-offset setting. The lyric clock
        /// only schedules WHEN due frames are applied and drives the visuals.</para>
        ///
        /// <para>A BACKWARDS SEEK is handled by rebuilding rather than by unwinding: see the comment
        /// on <see cref="lastFedTime"/> and <see cref="ReplayEngineFeed.RebuildTo"/>.</para>
        /// </summary>
        private partial class EngineTicker : Drawable
        {
            private readonly TypingEngine engine;
            private readonly DrawableTypeBeatRuleset? drawableRuleset;

            private Game.Replays.Replay? activeReplay;
            private int nextFrameIndex;

            /// <summary>
            /// The lyric time the last fed frame was clocked at, i.e. the high-water mark
            /// <see cref="nextFrameIndex"/> was advanced under. Anything earlier arriving next frame
            /// is a BACKWARDS SEEK. Negative infinity until the first tick, so the first frame of a
            /// play is never mistaken for one.
            /// </summary>
            private double lastFedTime = double.NegativeInfinity;

            // Cached by Player (via GameplayClockContainer / FrameStabilityContainer); absent in bare
            // drawable-ruleset test scenes, where there is no rate mod to report anyway.
            [Resolved]
            private IGameplayClock? gameplayClock { get; set; }

            public EngineTicker(TypingEngine engine, DrawableTypeBeatRuleset? drawableRuleset)
            {
                this.engine = engine;
                this.drawableRuleset = drawableRuleset;
            }

            protected override void Update()
            {
                base.Update();

                var replay = drawableRuleset?.ReplayScore?.Replay;
                double clockRate = wpmClockRate(gameplayClock);

                if (replay != null)
                {
                    // The replay can be swapped mid-play (editor autoplay toggle); restart feeding.
                    if (!ReferenceEquals(replay, activeReplay))
                    {
                        activeReplay = replay;
                        nextFrameIndex = 0;
                        lastFedTime = double.NegativeInfinity;
                    }

                    var frames = replay.Frames;

                    // BACKWARDS SEEK. Both this index and the engine only ever move forwards, so a
                    // clock that has gone back leaves every keystroke between the new time and the
                    // old one already consumed and every cell, the caret and the active line frozen
                    // at their pre-seek values while the song plays on. Rebuilding is the only way
                    // back, and it is exact rather than an unwind (see ReplayEngineFeed.RebuildTo).
                    //
                    // Reachable only with a replay attached, which is the whole of the "replay and
                    // autoplay only" scope: live play cannot seek, so TypeBeatKeyHandler is not in
                    // this at all.
                    if (Time.Current < lastFedTime)
                        nextFrameIndex = ReplayEngineFeed.RebuildTo(engine, frames, Time.Current, clockRate);

                    lastFedTime = Time.Current;

                    while (nextFrameIndex < frames.Count && frames[nextFrameIndex].Time <= Time.Current)
                    {
                        if (frames[nextFrameIndex] is TypeBeatReplayFrame frame)
                            ReplayEngineFeed.Apply(engine, frame, clockRate);

                        nextFrameIndex++;
                    }
                }

                engine.Update(Time.Current, clockRate);
            }
        }

        /// <summary>
        /// Full-keyboard typing input, taken via raw <see cref="OnKeyDown"/> inside the ruleset
        /// input manager's subtree (raw key events pass through RulesetInputManager to its
        /// children; children receive them before the key-binding container, so typing letters
        /// wins over the vestigial Z/X action bindings). OS/framework key-repeat is honoured ONLY
        /// for backspace (hold to erase); a held character key never machine-guns judgements at the
        /// keyboard's own repeat rate, and holding it produces nothing at all beyond the initial
        /// press. Ctrl/Alt combos fall through to framework shortcuts.
        ///
        /// <para>Backspace is live ONLY in allow-wrong-input mode, the only model where a wrong char
        /// lands in a cell and is thus worth erasing; under Gatekeeper the key is swallowed and does
        /// nothing at all. Since backlog 107 allow-wrong-input is the default, so backspace is live
        /// by default: the predicate is unchanged, it simply resolves the other way now. Replay
        /// playback is unaffected: recorded backspace frames go straight to the engine (see
        /// <see cref="EngineTicker"/>).</para>
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

            // Cached by Player (via GameplayClockContainer / FrameStabilityContainer); absent in bare
            // drawable-ruleset test scenes, where there is no rate mod to report anyway.
            [Resolved]
            private IGameplayClock? gameplayClock { get; set; }

            public TypeBeatKeyHandler(TypingEngine engine, IBindable<KeyboardLayout> keyboardLayout, DrawableTypeBeatRuleset? drawableRuleset)
            {
                this.engine = engine;
                this.keyboardLayout = keyboardLayout;
                this.drawableRuleset = drawableRuleset;
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
                engine.Update(time, wpmClockRate(gameplayClock));

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
                    // Erasing only ever has something to undo in ALLOW-WRONG-INPUT mode: that is the
                    // one model where a wrong char lands in a cell. Under GATEKEEPER a wrong
                    // key is rejected outright, so nothing erasable is ever written, and re-typing an
                    // already-correct cell (freestyle cells included, whose press is a CORRECT hit) is
                    // scoring-inert. Backspace is therefore inert-by-design under Gatekeeper, and is
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
                // Literate (case-sensitive) mod; folded away harmlessly in normal play. The
                // punctuation surface opens for the same mod, and ONLY for it: without it a comma
                // key stays inert (no wrong-key combo break for a habitual comma) and Shift+digit
                // still produces the digit, exactly as before.
                if (KeyCharMap.TryMap(e.Key, keyboardLayout.Value, e.ShiftPressed, engine.Literate, out char c))
                {
                    // The framework's own auto-repeat is discarded outright: one judgement per
                    // physical press, never a machine-gun run at the keyboard's repeat rate.
                    if (!e.Repeat)
                    {
                        if (engine.ProcessKey(c, time))
                            drawableRuleset?.RecordTypingInput(c, time);
                    }

                    return true;
                }

                return false;
            }
        }
    }
}
