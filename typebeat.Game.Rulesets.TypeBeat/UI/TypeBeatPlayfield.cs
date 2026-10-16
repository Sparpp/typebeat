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

        /// <summary>
        /// How long the key handler pairs a key press with the text the OS commits for it (backlog 383;
        /// the reasoning is on the handler's own <c>pairing_grace_ms</c>). Internal for the tests that wait it out.
        /// </summary>
        internal const double PAIRING_GRACE_MS = 50;

        // Tracks the user's background dim so a 100% dim restores the flat serika-dark backdrop
        // over the (then fully-black) beatmap image/video.
        private readonly Bindable<double> backgroundDim = new Bindable<double>();

        private FramedOffsetClock lyricClock = null!;

        private LyricStage stage = null!;

        private TypeBeatKeyHandler keyHandler = null!;

        /// <summary>
        /// Test seam: stops (or restarts) the key handler's real-time clock for the text pairing's
        /// grace (see <see cref="PAIRING_GRACE_MS"/>), so a test handing over a commit frames after
        /// its key cannot lose the press to a slow machine. Gameplay time is unaffected.
        /// </summary>
        internal void FreezePairingClock(bool frozen) => keyHandler.FreezePairingClock(frozen);

        /// <summary>Urgency of the actual forced-line cutoff, on the same offset clock as typing.</summary>
        internal float LinePushWarningProgress
        {
            get
            {
                if (lyricClock.IsNull() || Engine.DragCutoffAt is not double cutoff || lyricClock.CurrentTime >= cutoff)
                    return 0;

                return (float)Math.Clamp(1 - (cutoff - lyricClock.CurrentTime) / TypingEngine.FLETCHER_DRAG_GRACE_MS, 0, 1);
            }
        }

        /// <summary>
        /// A live RETYPE SELECTION (backlog 182): the half-open cell range
        /// [<see cref="StartCell"/>, <see cref="EndCell"/>) of line <see cref="LineIndex"/> that a
        /// Ctrl+A has offered to erase and retype. <see cref="EndCell"/> is always the caret index it
        /// was taken at, which is what makes the selection self-invalidating: any caret move that did
        /// not go through the consume path leaves it stale, and the playfield drops it.
        /// </summary>
        public readonly record struct RetypeSelection(int LineIndex, int StartCell, int EndCell);

        private RetypeSelection? retypeSelection;

        /// <summary>
        /// The retype selection currently held, or null. Pure UI state: the engine knows nothing
        /// about it, and consuming it is composed out of ordinary engine calls (see
        /// <see cref="TypeBeatKeyHandler"/>). Public for cross-assembly tests.
        /// </summary>
        public RetypeSelection? CurrentRetypeSelection => retypeSelection;

        /// <summary>
        /// Set or clear the retype selection, repainting whichever line displays are affected. The
        /// one write site: the highlight and the state it is drawn from cannot drift apart.
        /// </summary>
        private void applyRetypeSelection(RetypeSelection? selection)
        {
            var previous = retypeSelection;
            retypeSelection = selection;

            if (stage.IsNull())
                return;

            // Clear the old line's highlight when the selection has moved off it (or gone entirely).
            if (previous is RetypeSelection old && (selection is not RetypeSelection now || now.LineIndex != old.LineIndex))
                stage.DisplayAt(old.LineIndex)?.SetSelection(0, 0);

            if (selection is RetypeSelection current)
                stage.DisplayAt(current.LineIndex)?.SetSelection(current.StartCell, current.EndCell);
        }

        /// <summary>
        /// Shows <paramref name="composition"/> (a Polyglot play's live IME composition, backlog 331) at
        /// the caret, or clears it when empty. Display only: nothing is judged until the IME commits.
        /// </summary>
        private void setImeComposition(string composition)
        {
            if (stage.IsNotNull())
                stage.SetImeComposition(composition);
        }

        /// <summary>Where the IME's candidate window should sit: the typing caret, in screen space.</summary>
        private RectangleF imeRectangle => stage.IsNotNull() ? stage.PlayerCaretScreenQuad.AABBFloat : default;

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

            // The wrong-input model is fixed for the play and is no longer a setting (backlog 107):
            // typing wrong chars through is the default, and TypeBeatModGatekeeper is the only thing
            // that turns it off, via its ApplyToEngine.

            // Space-to-skip-a-word IS a setting, and is read ONCE here rather than bound live: it
            // decides how a space is judged, and the replay CONFIG frame stamps whatever the engine
            // holds at the first keystroke, so a value that could change mid-play would leave the
            // header describing only part of the run. Absent config (a bare test scene) leaves the
            // engine's own default, which is off.
            if (config != null)
            {
                Engine.SpaceSkipsWord = config.Get<bool>(TypeBeatRulesetSetting.SpaceSkipsWord);

                // MANUAL NEWLINES is read once for the same reason, and matches it in every other
                // way: one engine flag, stamped on the CONFIG frame, nobody able to change it
                // mid-run. OFF leaves the automatic hand-over of a finished line exactly as it was.
                Engine.ManualNewlines = config.Get<bool>(TypeBeatRulesetSetting.ManualNewlines);
                // THE TYPED-THROUGH NEWLINE RIDES THE SAME SETTING: with Manual Newlines armed, a
                // letter at a finished caret hands the line on as well (see
                // TypingEngine.NewlineOnTypedLetter), so there is no second switch for the player to
                // find. Its own replay bit still records which rule a stored run was played under.
                Engine.NewlineOnTypedLetter = Engine.ManualNewlines;
            }

            // The Player already renders the beatmap background image (dimmed) and, when
            // "beatmap storyboard/video" is on, the video, both BELOW the ruleset. Historically
            // this playfield painted an opaque serika-dark box over all of it (the monkeytype flat
            // look), which blacked the real background out. Only cover it when there is nothing to
            // show: reveal the image/video behind a readability scrim, else keep the flat panel.
            // The video half of that question is asked of the FILE, not of the [Events] line: see
            // StageBackdrop.HasRenderableContent. Load time only, no per-frame cost.
            bool showStoryboard = osuConfig?.Get<bool>(OsuSetting.ShowStoryboard) ?? true;
            bool hasBackdrop = StageBackdrop.HasBackdrop(
                beatmap?.Value.BackgroundFile, beatmap?.Value.Storyboard, showStoryboard);

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

            keyHandler = new TypeBeatKeyHandler(Engine, drawableRuleset, this);

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
                        new EngineTicker(Engine, drawableRuleset, () => keyHandler.HeldPressTime),
                        stage = new LyricStage(Engine),
                        new TypeBeatHudOverlay(drawableRuleset?.Ruleset.RulesetInfo),
                        keyHandler,
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

            Engine.Rewound += onRewound;

            // The cell RESULTS and COMBO's result-less writes (the typo, rush-cap, word-skip and
            // back-dated seal breaks, the restore, the mistype count and the combo-neutral marks) go
            // through TypeBeatComboFeed, the same call TypeBeatReplayScorer makes, so live play and
            // recalculation cannot write combo differently. The results themselves are this
            // playfield's: the drawables apply them and the Player hands them to the processors.
            // Live play is always judged under TypoRule.Deferred.
            comboFeed = TypeBeatComboFeed.Attach(Engine, () => scoreProcessor, TypoRule.Deferred, applyCharJudgement, applySealResults);

            // HEALTH's six result-less seams (the typo drain and its refund, the word skip's drain
            // and its two refunds, the mash guard) go through TypeBeatHealthFeed, the one place they
            // live, so a headless driver can put a real processor behind an engine with none of
            // this playfield around it. Attached AFTER the combo feed, which keeps each shared
            // event's old order (the result or the combo write first, then the health move); the two
            // accounts are independent anyway. The cell RESULTS still reach health the ordinary way,
            // through the drawables and the Player.
            healthFeed = TypeBeatHealthFeed.Attach(Engine, () => healthProcessor as TypeBeatHealthProcessor);
        }

        /// <summary>The <see cref="TypeBeatComboFeed"/> subscription, released on dispose.</summary>
        private IDisposable? comboFeed;

        /// <summary>The <see cref="TypeBeatHealthFeed"/> subscription, released on dispose.</summary>
        private IDisposable? healthFeed;

        protected override void Update()
        {
            base.Update();

            // The playfield updates before the lyric subtree's children, so fixing the source
            // here guarantees the engine never consumes a frame of the stale load-time clock.
            if (lyricClock.Source != Clock)
                lyricClock.ChangeSource(Clock);

            // Live-applied (the setting already works end-to-end; it has no settings UI yet).
            lyricClock.Offset = -lyricOffset.Value;

            // A retype selection is a gesture held open on the ACTIVE line between two keystrokes,
            // so anything that moves out from under it drops it: the line deactivating or sealing
            // (the index changes, or goes to -1), and any caret move that did not go through the
            // consume path. Checked per frame because both of those can happen on a plain clock
            // tick, with no key event to notice them.
            if (retypeSelection is RetypeSelection selection
                && (Engine.ActiveLineIndex != selection.LineIndex || Engine.CaretIndex != selection.EndCell))
            {
                applyRetypeSelection(null);
            }
        }

        protected override void OnNewDrawableHitObject(DrawableHitObject drawableHitObject)
        {
            base.OnNewDrawableHitObject(drawableHitObject);

            if (drawableHitObject is DrawableTypeBeatHitObject line)
                lineDrawables[line.HitObject.LineIndex] = line;
        }

        /// <summary>
        /// A cell judgement's osu result, through the line's drawable: the half of
        /// <see cref="TypingEngine.CharJudged"/> that rides on a result. The rush cap's hand-written
        /// break on the same event is <see cref="TypeBeatComboFeed"/>'s, called after this.
        /// </summary>
        private void applyCharJudgement(CharJudgement judgement)
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
            // reporting. The drain, and its refund if the player backspaces the character away, are
            // TypeBeatHealthFeed's (attached in LoadComplete, on this same event), which is what keeps
            // a FIXED typo costing exactly what it did before: nothing beyond what the corrected
            // retype earns.
        }

        // A backspace taking a wrong character back out of its cell (TypingEngine.TypoErased, backlog
        // 140's other half, made visible by backlog 166) is HEALTH only: the mistype count is spent,
        // and the combo the keypress broke is restored by the corrected RETYPE
        // (TypeBeatComboFeed.OnComboRestored), not by the erase. So nothing here handles it;
        // TypeBeatHealthFeed refunds the drain. The refund rides on the ERASE rather than on the fix
        // so that erasing a typo and leaving the cell empty is priced as the miss it then is (one
        // drain at the seal) instead of as a typo plus a miss. The same is true of
        // TypingEngine.AbandonReclaimed (backlog 167): the combo a skip broke comes back at the
        // RETYPE, and the refund is the health feed's.

        /// <summary>
        /// The line ran out of time: every cell the play never resolved takes its result now, through
        /// <see cref="TypeBeatComboFeed.SealedCellResult"/>, which decides between a MISS and an
        /// unfixed TYPO and writes the combo-neutral mark, unless the cell was due before the play
        /// began. The feed calls this AFTER it has written the seal's back-dated break
        /// (<see cref="TypeBeatComboFeed.OnLineSealed"/>), so every result here is weighted by the
        /// combo the break left.
        ///
        /// <para>The typo's hit is HP-neutral since backlog 166: its HP was taken at the keypress,
        /// so <see cref="TypeBeatHealthProcessor"/> gives its result no health increase either way.
        /// The seal still owns the MISS drain, because a cell nobody typed cannot be known missed
        /// before its line runs out of time on it. A cell a word skip ABANDONED (backlog 167) arrives
        /// here as an ordinary Miss, which is exactly what it turned out to be, and its two
        /// corrections were made a moment earlier at <see cref="TypingEngine.AbandonSealed"/>: the HP
        /// the skip charged is refunded into this drain, and the cell is marked combo-neutral so the
        /// Miss cannot take a break the skip already took.</para>
        /// </summary>
        private void applySealResults(LineSealResult sealResult)
        {
            if (!lineDrawables.TryGetValue(sealResult.LineIndex, out var line))
                return;

            line.ApplySealResults(cellIndex =>
            {
                // GRANTED: a character that was already due before this play BEGAN was never in front of
                // the player. A type!beat play can start anywhere in its map - the editor's gameplay
                // test starts at the mapper's playhead - and the part it jumped past is not a miss
                // anybody earned, so it resolves as the perfect hit the run is credited with. The engine
                // reads the same start time to place its caret past exactly these characters
                // (see TypingEngine.PlayStartTime), and both compare it against the ENGINE's cell
                // target, so the two agree about what the play reached.
                if (cellWasDueBeforeThePlayStarted(sealResult.LineIndex, line, cellIndex))
                    return line.CellAt(cellIndex)!.HitObject.Judgement.MaxResult;

                return TypeBeatComboFeed.SealedCellResult(scoreProcessor, Engine, sealResult.LineIndex, cellIndex, TypoRule.Deferred);
            });
        }

        /// <summary>
        /// Whether one of <paramref name="line"/>'s cells was already due before this play began, i.e.
        /// whether the play reached it at all (see the seal above, and
        /// <see cref="TypingEngine.PlayStartTime"/>).
        ///
        /// <para>The time it is measured against is the engine's own <see cref="TypingCell.TargetTime"/>
        /// for the cell, the one its caret walk reads, and NOT the cell object's start. The nested
        /// objects are flattened by <c>TypingLine.ForMods</c> with its defaults, which leave
        /// subdivision targets unaligned, while the live engine aligns them
        /// (<see cref="TypingEngine.AlignSubdivisionTargets"/>), so on a subdivided word without
        /// <c>split_chars</c> the two times can differ. Only the editor's gameplay test declares a
        /// play start, so this decides nothing a submitted or re-derived score reads.</para>
        /// </summary>
        private bool cellWasDueBeforeThePlayStarted(int lineIndex, DrawableTypeBeatHitObject line, int cellIndex)
            => Engine.PlayStartTime is double start
               && line.CellAt(cellIndex) != null
               && lineIndex >= 0 && lineIndex < Engine.Lines.Count
               && cellIndex >= 0 && cellIndex < Engine.Lines[lineIndex].Cells.Count
               && Engine.Lines[lineIndex].Cells[cellIndex].TargetTime < start;

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
        /// result at all (see <see cref="TypeBeatComboFeed.OnMistyped"/>): the MISTYPE COUNT, a pure counter, and the
        /// combo-neutral ledger. The count is re-derived from the rebuilt engine, which is
        /// authoritative for exactly the interval that survives the seek
        /// (<see cref="TypingEngine.Mistypes"/> counts the same wrong keypresses
        /// <see cref="TypingEngine.Mistyped"/> announces, one for one). The ledger is dropped, since
        /// every entry that is still owed will be re-marked at its line's seal.</para>
        ///
        /// <para>KNOWN RESIDUE: the hand-mirrored combo BREAKS are not undone, so
        /// <see cref="ScoreProcessor.Combo"/> can read low between the seek target and the first
        /// break after it, at which point the next hand-mirrored break (an absolute write of 0, or
        /// since backlog 259 a seal's write of the run it left standing) puts
        /// it back on the engine's value. It is not overwritten from the engine here on purpose: the
        /// two counters are kept equal by mirroring every move, never by one dictating to the other,
        /// and a watched replay's HUD combo is the only thing this reaches. Nothing here can mutate
        /// or re-submit the stored score being watched.</para>
        ///
        /// <para>THE SAME RESIDUE APPLIES TO THE TYPO HP DRAIN (backlog 166) AND TO THE ABANDONED
        /// CELLS' (backlog 167), for the same reason: neither rides on a result (see
        /// <see cref="TypeBeatHealthFeed.OnCharJudged"/> and <see cref="TypeBeatHealthFeed.OnWordAbandoned"/>), so the framework's revert
        /// cannot give them back, and a seek backwards past a stretch containing typos or skips
        /// leaves the bar reading one drain low per cell in it until they are re-typed on the way
        /// forward.
        /// Health is not re-derived here because, unlike the mistype count, the engine does not hold
        /// an authoritative total to copy: HP is the health processor's own running account.</para>
        /// </summary>
        private void onRewound() => (scoreProcessor as TypeBeatScoreProcessor)?.ResyncAfterRewind(Engine.Mistypes);

        protected override void Dispose(bool isDisposing)
        {
            Engine.Rewound -= onRewound;
            comboFeed?.Dispose();
            healthFeed?.Dispose();
            base.Dispose(isDisposing);
        }

        /// <summary>
        /// The speed-adjusting-mod rate to hand <see cref="TypingEngine.Update"/> so its WPM clock
        /// counts REAL seconds rather than beatmap ones: without it Half Time overstates WPM by 1/0.75
        /// and Double Time understates it by 1/1.5.
        ///
        /// <para><see cref="GameplayClockExtensions.GetTrueGameplayRate"/> is the right source because it
        /// reads the aggregate frequency/tempo actually in force, so it covers DT/NC/HT/DC at ANY custom
        /// slider value, both ramp mods and any future rate mod without enumerating them. Deliberately
        /// NOT <c>PerformancePoints.EligibleRate</c>: that answers a pp-eligibility question and returns
        /// null for a custom rate, but a custom-rate play still has a real typing speed worth showing.</para>
        ///
        /// <para>Editor Test Play also includes its playback tempo.</para>
        ///
        /// <para>MUST be sampled per frame, never cached at load: ModWindUp / ModWindDown ramp the rate
        /// across the run. Null (no <see cref="IGameplayClock"/> in the hierarchy) means a bare
        /// drawable-ruleset test scene with no <c>Player</c>, hence no rate mods, hence 1.</para>
        /// </summary>
        private static double wpmClockRate(IGameplayClock? clock, EditorTestPlayback? editorPlayback)
            => editorPlayback?.GetRateForStatistics(clock) ?? clock?.GetTrueGameplayRate() ?? 1;

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

            /// <summary>
            /// The second-word eras the live factory gave the engine, taken when a replay first
            /// attaches and put back when it detaches (the editor's autoplay toggle), so a mapper
            /// typing on after autoplay plays on the live rules again rather than on whatever the
            /// replay's headers selected (backlog 363: above all the grouping, which the lyric stack
            /// re-lays when it flips back). Taken and put back through <see cref="ReplayEraBits"/>, so
            /// every second-word era is covered.
            /// </summary>
            private bool[]? liveExtendedEras;
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

            [Resolved]
            private EditorTestPlayback? editorPlayback { get; set; }

            /// <summary>
            /// The key time of the oldest live press still waiting for its text commit, if any (see
            /// <see cref="TypeBeatKeyHandler.HeldPressTime"/>): the engine is not ticked past it.
            /// </summary>
            private readonly Func<double?> heldPressTime;

            public EngineTicker(TypingEngine engine, DrawableTypeBeatRuleset? drawableRuleset, Func<double?> heldPressTime)
            {
                this.engine = engine;
                this.drawableRuleset = drawableRuleset;
                this.heldPressTime = heldPressTime;
            }

            protected override void Update()
            {
                base.Update();

                var replay = drawableRuleset?.ReplayScore?.Replay;
                double clockRate = wpmClockRate(gameplayClock, editorPlayback);

                if (replay != null)
                {
                    var frames = replay.Frames;

                    // The replay can be swapped mid-play (editor autoplay toggle); restart feeding.
                    if (!ReferenceEquals(replay, activeReplay))
                    {
                        liveExtendedEras ??= ReplayEraBits.Capture(engine, EraWord.ConfigExtended);
                        activeReplay = replay;
                        nextFrameIndex = 0;
                        lastFedTime = double.NegativeInfinity;

                        // The live factory built this engine with the second-word eras on. A replay
                        // with no extended header (or none at all) predates them, so clear them before
                        // priming; the headers below set them again for a replay that carries them.
                        ReplayEngineFeed.ClearExtendedEras(engine);

                        // Prime the judgement flags from the recorded CONFIG frame BEFORE the first
                        // tick rather than when the frame's own time comes round. The playfield's
                        // load put the WATCHER's settings on the engine (SpaceSkipsWord,
                        // ManualNewlines), and ManualNewlines is read by Update itself (whether a
                        // finished line is held open, whether the caret snaps onto a starting
                        // line), so every tick between the song's start and the first keystroke
                        // would otherwise run under the watcher's arm rather than the recorded one.
                        // Apply on a CONFIG frame only sets flags and ticks nothing, and applying
                        // it again at its own time below is idempotent, so it is not consumed here.
                        for (int i = 0; i < frames.Count; i++)
                        {
                            if (frames[i] is TypeBeatReplayFrame { IsConfig: true } config)
                            {
                                ReplayEngineFeed.Apply(engine, config, clockRate);

                                // ...and the SECOND header (backlog 347), which the recorder writes
                                // straight after it: the CONFIG frame has just cleared its flags.
                                if (i + 1 < frames.Count && frames[i + 1] is TypeBeatReplayFrame { IsConfigExtended: true } extended)
                                    ReplayEngineFeed.Apply(engine, extended, clockRate);

                                break;
                            }

                            // A replay that opens on an extended header with NO CONFIG frame ahead of
                            // it is autoplay's (backlog 363, TypeBeatAutoGenerator): it keeps the live
                            // engine's first word and carries only the second-word era it presses
                            // for, so that header is primed on its own.
                            if (frames[i] is TypeBeatReplayFrame { IsConfigExtended: true } lone)
                            {
                                ReplayEngineFeed.Apply(engine, lone, clockRate);
                                break;
                            }
                        }
                    }

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
                else if (activeReplay != null)
                {
                    // DETACHED (the editor's autoplay toggle handing the play back): the live eras
                    // return, and the next attach starts from a clean feed.
                    activeReplay = null;

                    if (liveExtendedEras is { } live)
                        ReplayEraBits.Restore(engine, EraWord.ConfigExtended, live);

                    liveExtendedEras = null;
                }

                // THE PLAY DECLARES WHERE IT BEGAN (see TypingEngine.SetPlayStart): a play can start
                // part-way through a map - the editor's gameplay test resets its clock to the mapper's
                // playhead - and everything the map asks for before that moment was never in front of
                // the player.
                //
                // The time is the gameplay clock's own START time, i.e. where the play's clock was
                // started or RESET to, and deliberately not the time of this first frame: the clock of a
                // scene that drives a ruleset by hand, or one that has merely been seeked, has not
                // started a play where it happens to be standing. (A bare drawable-ruleset scene has no
                // gameplay clock at all, which is the same "no play to declare" case.)
                //
                // A REPLAY-driven play declares nothing either: it re-enacts a run that was already
                // played and scored, so its characters are the tape's to judge, not ours to grant.
                //
                // And ONLY the editor's gameplay test declares at all (DrawableRuleset
                // .IsEditorGameplayTest, set by EditorPlayer). The declaration makes the seal grant
                // every cell due before the start as a perfect hit, so on a play that submits a score
                // it would be free accuracy for whatever the clock's start time happened to skip. A
                // normal play, a spectated one and a replay start where the map does, and a late
                // clock start there changes nothing about what the player is charged for.
                if (engine.PlayStartTime is null && replay == null && gameplayClock != null && drawableRuleset?.IsEditorGameplayTest == true)
                    engine.SetPlayStart(gameplayClock.StartTime);

                // A live press still waiting for its text commit HOLDS the engine at its key time
                // (KeyTimeJudgement): the press is then judged on Update(keyTime) followed by the key
                // at keyTime, the call sequence a replay feeds for its frame, rather than on an engine
                // that has already sealed or activated whatever the song reached in between. The hold
                // lasts until the commit arrives or the pairing gives up on it (PAIRING_GRACE_MS of
                // real time), and the next tick catches up in one segment.
                double tickTime = Time.Current;

                if (heldPressTime() is double held && held < tickTime)
                    tickTime = held;

                engine.Update(tickTime, clockRate);
            }
        }
    }
}
