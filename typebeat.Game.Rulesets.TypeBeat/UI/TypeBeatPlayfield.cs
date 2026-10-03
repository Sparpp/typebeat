// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
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
            // that turns it off, via ApplyToDrawableRuleset.

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
                        new TypeBeatHudOverlay(drawableRuleset?.Ruleset.RulesetInfo),
                        new TypeBeatKeyHandler(Engine, drawableRuleset, this),
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
            Engine.Mistyped += onMistyped;
            Engine.ComboRestored += onComboRestored;
            Engine.WordAbandoned += onWordAbandoned;
            Engine.AbandonSealed += onAbandonSealed;
            Engine.Rewound += onRewound;

            // HEALTH's six result-less seams (the typo drain and its refund, the word skip's drain
            // and its two refunds, the mash guard) go through TypeBeatHealthFeed, the one place they
            // live, so a headless driver can put a real processor behind an engine with none of
            // this playfield around it. Attached AFTER the handlers above, which keeps each shared
            // event's old order (the result or the combo write first, then the health move); the two
            // accounts are independent anyway. The cell RESULTS still reach health the ordinary way,
            // through the drawables and the Player.
            healthFeed = TypeBeatHealthFeed.Attach(Engine, () => healthProcessor as TypeBeatHealthProcessor);
        }

        /// <summary>The <see cref="TypeBeatHealthFeed"/> subscription, released on dispose.</summary>
        private IDisposable? healthFeed;

        protected override void Update()
        {
            base.Update();

            // The playfield updates before the lyric subtree's children, so fixing the source
            // here guarantees the engine never consumes a frame of the stale load-time clock.
            if (lyricClock.Source != Clock)
                lyricClock.ChangeSource(Clock);

            // Live-applied (M7 will surface a slider; the setting already works end-to-end).
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
            // reporting. The drain, and its refund if the player backspaces the character away, are
            // TypeBeatHealthFeed's (attached in LoadComplete, on this same event), which is what keeps
            // a FIXED typo costing exactly what it did before: nothing beyond what the corrected
            // retype earns.

            // Fletcher's rush cap breaks combo on a press that is still judged Great/Ok/Meh, so the
            // hit result alone (a Great/Ok/Meh, which INCREMENTS osu's combo) cannot carry the break.
            // Mirror the engine's own combo by hand, after the result has been applied, exactly as
            // onMistyped does for a wrong keypress. Gated on the mod so the default path is untouched:
            // there every ComboAfter == 0 judgement either maps to a Miss (which breaks osu's combo
            // itself) or is a WrongChar, whose break onMistyped has already carried.
            //
            // Since backlog 347 this is the PRE-347 ERA's seam only (a replay without the extended
            // CONFIG word): under RushCapCostsAccuracy an over-cap press is an ordinary Meh that
            // credits the engine's combo, so ComboAfter is never 0 on it and this never fires,
            // and under InputEra2 (every new live run) there is no cap at all.
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

        // A backspace taking a wrong character back out of its cell (TypingEngine.TypoErased, backlog
        // 140's other half, made visible by backlog 166) is HEALTH only: the mistype count is spent,
        // and the combo the keypress broke is restored by the corrected RETYPE (see onComboRestored),
        // not by the erase. So this playfield has no handler for it; TypeBeatHealthFeed refunds the
        // drain. The refund rides on the ERASE rather than on the fix so that erasing a typo and
        // leaving the cell empty is priced as the miss it then is (one drain at the seal) instead of
        // as a typo plus a miss. The same is true of TypingEngine.AbandonReclaimed (backlog 167): the
        // combo a skip broke comes back at the RETYPE, and the refund is the feed's. And of
        // TypingEngine.WrongKeyRejected: its combo break rides on Mistyped (see onMistyped), one
        // event earlier and in both input models, and the mash guard left behind is the feed's.

        /// <summary>
        /// A word skip abandoned a run of cells (backlog 167). Neither of the two things it costs can
        /// travel on a judgement result, because the skip applies none: the cells are still
        /// re-typeable, and a cell takes only its first result.
        ///
        /// <para>HEALTH drains one miss per cell, here and now, which is backlog 166's rule applied
        /// to the other deferred judgement: the bar must react to what the player just did. That
        /// drain, and its refund when the cells leave the abandoned state whichever way they leave
        /// it, are <see cref="TypeBeatHealthFeed"/>'s.</para>
        ///
        /// <para>COMBO is zeroed by hand, exactly as <see cref="onMistyped"/> zeroes it and for the
        /// identical reason. The engine has taken its one break at the skip; the Miss results that
        /// used to carry that break into osu's incrementally-maintained combo now arrive at the seal,
        /// a whole line later, so without this the submitted <c>max_combo</c> would count straight on
        /// through the rest of the line.</para>
        /// </summary>
        private void onWordAbandoned(AbandonedCells abandoned)
        {
            if (scoreProcessor != null)
                scoreProcessor.Combo.Value = 0;
        }

        /// <summary>
        /// The line sealed on cells the player never came back for (backlog 167). Both halves of this
        /// are what make a never-reclaimed skip cost exactly what it cost before the reclaim existed,
        /// and both have to happen BEFORE <see cref="onLineSealed"/> applies the results, which the
        /// engine guarantees by raising this event first.
        ///
        /// <para>HEALTH refunds the skip's drain into the drain the Miss results are about to take,
        /// so the pair nets to one charge per cell (<see cref="TypeBeatHealthFeed"/>, on this same
        /// event). COMBO-NEUTRAL marks stop those misses breaking osu's combo a second time: that
        /// break was taken at the skip, and the player may well have rebuilt a run through the rest
        /// of the line since, which the engine's own combo has kept.</para>
        /// </summary>
        private void onAbandonSealed(AbandonedCells abandoned)
        {
            if (scoreProcessor is not TypeBeatScoreProcessor typeBeatProcessor)
                return;

            foreach (int cellIndex in abandoned.CellIndices)
                typeBeatProcessor.MarkComboNeutral(abandoned.LineIndex, cellIndex);
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
        ///
        /// <para>A cell a word skip ABANDONED (backlog 167) arrives here as an ordinary Miss, which
        /// is exactly what it turned out to be, and its two corrections were made a moment earlier in
        /// <see cref="onAbandonSealed"/>: the HP the skip charged is refunded into this drain, and the
        /// cell is marked combo-neutral so the Miss cannot take a break the skip already took.</para>
        ///
        /// <para>THE BREAK MOVED OFF THE RESULTS since backlog 259
        /// (<see cref="TypingEngine.BackDatedSealBreak"/>), which is why EVERY seal miss is marked
        /// combo-neutral under that rule and not only the abandoned ones. A Miss carries a break to
        /// exactly one place, the combo it finds when it lands, and the whole point of back-dating is
        /// that the break belongs to an earlier one. So the results are made combo-neutral like the
        /// skip's, and the one break is mirrored by hand FIRST, as an absolute write of the run the
        /// engine is left holding (<see cref="LineSealResult.SurvivingCombo"/>): the engine is the
        /// only thing that knows which increment was earned on which cell, and the two accounts hold
        /// increments for the very same cells, so its surviving count IS this one's. Written before
        /// the results rather than after so every result this seal applies, the unfixed typos
        /// included, is weighted by the combo the break left, which is the combo the player is
        /// actually holding.</para>
        /// </summary>
        private void onLineSealed(LineSealResult sealResult)
        {
            bool backDated = Engine.BackDatedSealBreak;

            // MIN, not an assignment: this is a BREAK, so it may only ever take combo away. The two
            // accounts hold increments for the same cells, so the two values agree; the floor is
            // there because a break that credited combo would be a defect in whichever of them
            // happened to be behind, not a rule anyone wants.
            if (backDated && sealResult.ComboBroken && scoreProcessor != null)
                scoreProcessor.Combo.Value = Math.Min(scoreProcessor.Combo.Value, sealResult.SurvivingCombo);

            if (!lineDrawables.TryGetValue(sealResult.LineIndex, out var line))
                return;

            line.ApplySealResults(cellIndex =>
            {
                // GRANTED: a character that was already due before this play BEGAN was never in front of
                // the player. A type!beat play can start anywhere in its map - the editor's gameplay
                // test starts at the mapper's playhead - and the part it jumped past is not a miss
                // anybody earned, so it resolves as the perfect hit the run is credited with. The engine
                // reads the same start time to place its caret past exactly these characters
                // (see TypingEngine.PlayStartTime), so the two never disagree about what the play
                // reached.
                if (cellWasDueBeforeThePlayStarted(line, cellIndex))
                    return line.CellAt(cellIndex)!.HitObject.Judgement.MaxResult;

                var result = TypeBeatResultMapping.UnresolvedCellResult(Engine.CellLeftWrong(sealResult.LineIndex, cellIndex), TypoRule.Deferred);

                if (result == TypeBeatResultMapping.UNFIXED_TYPO || (backDated && result == TypeBeatResultMapping.SEAL_MISS))
                    (scoreProcessor as TypeBeatScoreProcessor)?.MarkComboNeutral(sealResult.LineIndex, cellIndex);

                return result;
            });
        }

        /// <summary>
        /// Whether one of <paramref name="line"/>'s cells was already due before this play began, i.e.
        /// whether the play reached it at all (see the seal above, and
        /// <see cref="TypingEngine.PlayStartTime"/>).
        ///
        /// <para>The time it is measured against is the CELL OBJECT's own start, which
        /// <c>TypeBeatHitObject.CreateNestedHitObjects</c> takes straight from the engine's flattening:
        /// the playfield cannot form a second opinion about when a character was due.</para>
        /// </summary>
        private bool cellWasDueBeforeThePlayStarted(DrawableTypeBeatHitObject line, int cellIndex)
            => Engine.PlayStartTime is double start
               && line.CellAt(cellIndex) is DrawableTypeBeatCharObject cell
               && cell.HitObject.StartTime < start;

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
        /// break after it, at which point the next hand-mirrored break (an absolute write of 0, or
        /// since backlog 259 a seal's write of the run it left standing) puts
        /// it back on the engine's value. It is not overwritten from the engine here on purpose: the
        /// two counters are kept equal by mirroring every move, never by one dictating to the other,
        /// and a watched replay's HUD combo is the only thing this reaches. Nothing here can mutate
        /// or re-submit the stored score being watched.</para>
        ///
        /// <para>THE SAME RESIDUE APPLIES TO THE TYPO HP DRAIN (backlog 166) AND TO THE ABANDONED
        /// CELLS' (backlog 167), for the same reason: neither rides on a result (see
        /// <see cref="onCharJudged"/> and <see cref="onWordAbandoned"/>), so the framework's revert
        /// cannot give them back, and a seek backwards past a stretch containing typos or skips
        /// leaves the bar reading one drain low per cell in it until they are re-typed on the way
        /// forward.
        /// Health is not re-derived here because, unlike the mistype count, the engine does not hold
        /// an authoritative total to copy: HP is the health processor's own running account.</para>
        /// </summary>
        private void onRewound() => (scoreProcessor as TypeBeatScoreProcessor)?.ResyncAfterRewind(Engine.Mistypes);

        protected override void Dispose(bool isDisposing)
        {
            Engine.CharJudged -= onCharJudged;
            Engine.LineSealed -= onLineSealed;
            Engine.Mistyped -= onMistyped;
            Engine.ComboRestored -= onComboRestored;
            Engine.WordAbandoned -= onWordAbandoned;
            Engine.AbandonSealed -= onAbandonSealed;
            Engine.Rewound -= onRewound;
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

            /// <summary>
            /// The second-word eras the live factory gave the engine, taken when a replay first
            /// attaches and put back when it detaches (the editor's autoplay toggle), so a mapper
            /// typing on after autoplay plays on the live rules again rather than on whatever the
            /// replay's headers selected (backlog 363: above all the grouping, which the lyric stack
            /// re-lays when it flips back).
            /// </summary>
            private (bool RushCapCostsAccuracy, bool InputEra2, bool AuthoredSyllablesOnly, bool AlignSubdivisionTargets)? liveExtendedEras;
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
                    var frames = replay.Frames;

                    // The replay can be swapped mid-play (editor autoplay toggle); restart feeding.
                    if (!ReferenceEquals(replay, activeReplay))
                    {
                        liveExtendedEras ??= (engine.RushCapCostsAccuracy, engine.InputEra2, engine.AuthoredSyllablesOnly, engine.AlignSubdivisionTargets);
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
                    {
                        engine.RushCapCostsAccuracy = live.RushCapCostsAccuracy;
                        engine.InputEra2 = live.InputEra2;
                        engine.AuthoredSyllablesOnly = live.AuthoredSyllablesOnly;
                        engine.AlignSubdivisionTargets = live.AlignSubdivisionTargets;
                    }

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

            /// <summary>The live IME composition, empty when none. Backspace, space and enter belong to the IME while it is not.</summary>
            private string imeComposition = string.Empty;

            /// <summary>
            /// The text input type a LATIN play activates with: <see cref="TextInputType.Code"/>, with IME
            /// disallowed. Code is the framework's one plain-text type that turns the IME off on Windows
            /// (<c>SupportsIme</c> is false for it, so the framework calls <c>ImmAssociateContext</c>
            /// with nothing) and turns SDL's autocorrect off; it is NOT a password type, so macOS never
            /// enters secure event input for it. A Latin play wants every key straight from the
            /// layout: an IME left on would hold a Latin letter in a composition window rather than
            /// commit it. Polyglot keeps <see cref="TextInputType.Text"/> with the IME allowed.
            /// </summary>
            internal static readonly TextInputProperties LATIN_TEXT_INPUT = new TextInputProperties(TextInputType.Code, AllowIme: false);

            /// <summary>Polyglot's activation, unchanged since backlog 331: plain text, IME allowed.</summary>
            internal static readonly TextInputProperties POLYGLOT_TEXT_INPUT = new TextInputProperties(TextInputType.Text, true);

            private void activateTextInput()
            {
                // A replay's characters are the tape's, not the keyboard's: nothing is activated for playback.
                if (textInputActive || textInput == null || drawableRuleset?.ReplayScore != null)
                    return;

                if (engine.Polyglot)
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

                if (engine.Polyglot)
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

                if (!engine.Polyglot)
                    pairCommittedText();
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
            /// ignored: the spacebar is a gesture and arrives through <see cref="OnKeyDown"/>.
            /// </summary>
            internal void TypeCommittedText(string text, double time)
            {
                // Nothing while paused (the ruleset input manager hands the key path nothing then either),
                // and nothing while another control holds focus (a chat box has activated text input too).
                if (!engine.Polyglot || drawableRuleset?.ReplayScore != null || drawableRuleset?.IsPaused.Value == true || !HasFocus)
                    return;

                engine.Update(time, wpmClockRate(gameplayClock));

                if (!engine.LineIsActive && !engine.FirstLineTypingOpensAt(time))
                    return;

                foreach (char c in PolyglotText.InputCharacters(text))
                {
                    if (c == ' ')
                        continue;

                    collapseSelection(time);

                    if (engine.ProcessKey(c, time))
                        drawableRuleset?.RecordTypingInput(c, time);
                }
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

                /// <summary>When the press was made, on <see cref="pairingClock"/>.</summary>
                public double Since;
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

            /// <summary>Commits from the text source, in arrival order. Filled on the window thread (live) or the update thread (tests), so locked.</summary>
            private readonly List<CommittedText> committed = new List<CommittedText>();

            private readonly object committedLock = new object();

            /// <summary>Fresh presses still waiting for their commit, oldest first. Update thread only.</summary>
            private readonly List<PendingPress> waitingPresses = new List<PendingPress>();

            private bool latinTextInputActive => textInputActive && !engine.Polyglot;

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

            private void resolveAll(List<(PendingPress press, string? text)>? resolved, double time)
            {
                if (resolved == null)
                    return;

                foreach (var (press, text) in resolved)
                    resolvePress(press.Plan, text, time);
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

                resolveAll(resolved, Math.Round(Time.Current));
            }

            /// <summary>
            /// Carries out what <paramref name="plan"/> says with the commit a press produced
            /// (<paramref name="text"/>, null when it committed nothing), at <paramref name="time"/>.
            /// Returns whether anything was typed.
            /// </summary>
            private bool resolvePress(PressPlan plan, string? text, double time)
            {
                switch (plan)
                {
                    case PressPlan.Drop:
                        return false;

                    case PressPlan.Space when text == null:
                        return TypeLatinText(" ", time);

                    default:
                        return text != null && TypeLatinText(text, time);
                }
            }

            /// <summary>
            /// Registers one fresh press for the pairing and resolves it on the spot when its commit
            /// is already here. Returns whether anything was typed.
            /// </summary>
            private bool pressFor(PressPlan plan, double time)
            {
                if (!latinTextInputActive)
                {
                    // No text source at all (a bare scene) or none live: the spacebar alone still works,
                    // by its key, which is the fallback the text path promises.
                    return plan == PressPlan.Space && TypeLatinText(" ", time);
                }

                string? text = claimCommittedText();

                if (text != null)
                    return resolvePress(plan, text, time);

                waitingPresses.Add(new PendingPress { Plan = plan, Since = pairingNow });
                return false;
            }

            /// <summary>
            /// Types a piece of COMMITTED text in a LATIN play (backlog 383): every press
            /// <see cref="TextInputFold.Fold"/> makes of it (a base letter for an accented one, "ss"
            /// for 'ß', marks only under Literate, nothing for a character the surface cannot hold) is
            /// one ordinary <see cref="TypingEngine.ProcessKey"/> call at <paramref name="time"/>,
            /// recorded exactly as a key press always was, so the replay format and the engine are
            /// untouched and a stored run re-derives cell for cell. The gates are the ones a typing key
            /// had on the key path: nothing before the line (or the first line's head start) opens, and
            /// on a finished line only the typed-through newline may take a letter. Returns whether
            /// anything was typed.
            /// </summary>
            internal bool TypeLatinText(string text, double time)
            {
                // Nothing while paused, during playback, or while another control holds focus (a chat
                // box can have activated text input too, and its characters are its own).
                if (engine.Polyglot || drawableRuleset?.ReplayScore != null || drawableRuleset?.IsPaused.Value == true || !HasFocus)
                    return false;

                engine.Update(time, wpmClockRate(gameplayClock));

                if (!engine.LineIsActive && !engine.FirstLineTypingOpensAt(time))
                    return false;

                bool typed = false;

                foreach (char c in TextInputFold.Fold(text, engine.Literate))
                {
                    // A FINISHED line takes a letter only as the typed-through newline (the era bit
                    // engine.NewlineOnTypedLetter carries), and never a space: the space that closes a
                    // line is the manual newline, which the spacebar's key decides (OnKeyDown).
                    if (engine.IsLineComplete && playfield.CurrentRetypeSelection is null)
                    {
                        if (c != ' ' && engine.NewlineOnTypedLetter && engine.ProcessKey(c, time))
                        {
                            drawableRuleset?.RecordTypingInput(c, time);
                            typed = true;
                        }

                        continue;
                    }

                    // A retype selection is consumed FIRST, so this press lands on the anchor cell:
                    // mass backspace, then the ordinary judged keypress (backlog 182).
                    collapseSelection(time);

                    if (engine.ProcessKey(c, time))
                    {
                        drawableRuleset?.RecordTypingInput(c, time);
                        typed = true;
                    }
                }

                return typed;
            }

            #endregion

            protected override bool OnKeyDown(KeyDownEvent e)
            {
                // POLYGLOT (backlog 331): while an IME composition is open, the keys that edit it are the
                // IME's (a backspace shortens the composition, a space converts it, enter commits it), so
                // the engine sees none of them. Swallowed, so none reaches a global binding either.
                if (engine.Polyglot && imeComposition.Length > 0
                                    && (e.Key == Key.BackSpace || e.Key == Key.Space || e.Key == Key.Enter || e.Key == Key.KeypadEnter || e.Key == Key.Escape))
                    return true;

                // A FRESH press of a key that commits text (backlog 383). The framework's own repeats
                // are not presses, and the OS's repeats never arrive as key events at all; see
                // pairing_grace_ms for how their commits are dropped. A Ctrl or Command chord is not
                // one either: no platform commits text for it (SDL drops the control characters), so
                // letting it wait would only hand it the next key's character. Ctrl+Alt stays one,
                // because that is how Windows reports AltGr, and Option (Alt) alone on macOS commits.
                bool freshTextPress = !engine.Polyglot && !e.Repeat && TypingKeys.CommitsText(e.Key)
                                      && !e.SuperPressed && (!e.ControlPressed || e.AltPressed);

                // Which word-level gesture (if any) this press triggers under the user's CURRENT
                // bindings (backlog 183; backlog 182 hardcoded Ctrl+Backspace and Ctrl+A here).
                // Resolved before anything else, so a gesture rebound onto a key this handler would
                // otherwise own outright (Backspace, Shift+Backspace) still reaches its gesture.
                var gesture = TypeBeatInputManager.ResolveGesture(e, rulesetInput?.CurrentGestureBindings
                                                                    ?? (fallbackBindings ??= new TypeBeatRuleset().GetDefaultKeyBindings()));

                // TYPING ALWAYS WINS. A gesture rebound onto a bare (or shifted) typeable key is
                // shadowed for as long as the key would type, and types. Nothing else is survivable:
                // the whole lyric surface is typeable, so a letter that silently stopped typing
                // mid-run could not be recovered from without leaving gameplay. No modifier chord
                // types, so no modifier chord is ever shadowed. Since backlog 383 "would type" is the
                // key's place in the block (the OS decides the character itself): a letter, a digit
                // or the spacebar always, a punctuation position under Literate, where marks are cells.
                if (gesture != null
                    && !e.ControlPressed && !e.AltPressed && !e.SuperPressed
                    && typesHere(e.Key))
                {
                    gesture = null;
                }

                // ...and an ALTGR chord is a character, never a gesture (backlog 383). Windows reports
                // AltGr as Ctrl+Alt, and the gestures match their modifiers loosely
                // (TypeBeatInputManager's matching mode), so without this a Polish AltGr+A ('ą') would
                // select back to the last typo instead of typing its 'a'. A Latin play only: Polyglot's
                // characters come through the IME path, and its keys stay as they were.
                if (gesture != null && freshTextPress && e.ControlPressed && e.AltPressed)
                    gesture = null;

                // Let framework shortcuts (every modifier combo that is not a bound gesture) fall
                // through. Super is in the list for macOS, where it is the platform's editing
                // modifier: the two recovery gestures are chorded with it there by default
                // (TypeBeatRuleset.RecoveryGestureModifier), so leaving it out would have every
                // OTHER Command chord fall through to the typing path and land in the lyric.
                if ((e.ControlPressed || e.AltPressed || e.SuperPressed) && gesture == null)
                {
                    // ...but whatever such a chord COMMITS is still typed (backlog 383): on Windows
                    // AltGr arrives as Ctrl+Alt, and on macOS Option is Alt, and both are how a
                    // layout reaches the characters it has no plain key for ('@', '[', a Polish 'ę').
                    // A real shortcut commits nothing, so its press simply expires.
                    if (freshTextPress)
                        pressFor(PressPlan.Type, Math.Round(Time.Current));

                    return false;
                }

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
                // ...and the map's FIRST line is the one exception: it has no line before it to rush
                // from, so a press inside its head start (FIRST_LINE_LEAD_MS) is consumed and opens it,
                // rather than falling through to a global binding for the whole second before its word.
                if (!engine.LineIsActive && !engine.FirstLineTypingOpensAt(time))
                    return dropPress(freshTextPress, time);

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
                if (engine.FletcherEnabled && gesture == null && e.Key == Key.Space && !engine.SongIsOnTheCaretsLine && engine.ActiveLineUntouched)
                    return dropPress(freshTextPress, time);

                if (gesture == TypeBeatAction.EraseWord || (gesture == null && e.Key == Key.BackSpace))
                {
                    // Gatekeeper rejects wrong keys, including a space inside a word, so a plain
                    // backspace has nothing to undo. A retype selection can still be consumed.
                    if (!engine.AllowWrongInput && playfield.CurrentRetypeSelection is null)
                        return dropPress(freshTextPress, time, true);

                    // Repeat honoured: hold to erase, monkeytype-style. Handled BEFORE the
                    // line-complete fall-through: backspacing at line end must keep working (it is
                    // how typed-through wrong chars get fixed in allow-wrong-input mode). Only an
                    // erase that actually changed state is recorded.
                    //
                    // The ERASE WORD binding takes the whole word (backlog 182). A live SELECTION
                    // takes precedence over either width: an erase key over one collapses it and
                    // types nothing, which is the same mass erase a letter would do before landing.
                    if (!collapseSelection(time))
                    {
                        if (gesture == TypeBeatAction.EraseWord)
                            eraseBackTo(engine.WordBackspaceTarget, time);
                        else if (engine.ProcessBackspace())
                            drawableRuleset?.RecordTypingInput(TypeBeatReplayFrame.BACKSPACE, time);
                    }

                    return dropPress(freshTextPress, time, true);
                }

                if (gesture == TypeBeatAction.SelectBackToTypo)
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

                if (gesture == TypeBeatAction.SkipLine)
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

                    drawableRuleset?.RecordTypingInput(TypeBeatReplayFrame.ENTER, time);
                    return dropPress(freshTextPress, time, true);
                }

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
                if (engine.IsLineComplete && playfield.CurrentRetypeSelection is null)
                {
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
                    if (engine.ManualNewlines)
                    {
                        bool space = e.Key == Key.Space;
                        bool enter = e.Key == Key.Enter || e.Key == Key.KeypadEnter;

                        if ((space || enter) && (space ? engine.ProcessKey(' ', time) : engine.ProcessEnter(time)))
                        {
                            drawableRuleset?.RecordTypingInput(space ? ' ' : TypeBeatReplayFrame.ENTER, time);
                            return dropPress(freshTextPress, time, true);
                        }
                    }

                    // AND BY TYPING (the typed-through era bit): a typeable letter hands the line on
                    // here rather than falling through to the global bindings, so the caret can be
                    // moved by typing the next line instead of by space. Asked of the ENGINE rather
                    // than decided here - ProcessKey answers whether it consumed the press - because a
                    // letter it refuses must still fall through, which is what keeps the mid-song skip
                    // overlay reachable through an instrumental gap. This is the arm the engine's own
                    // rule needs to be reachable at all: without it a letter never gets past this
                    // block, however the engine is configured.
                    //
                    // Since backlog 383 the letter is the OS's character, typed by TypeLatinText,
                    // which asks the engine the same question. When the commit is already here the
                    // answer decides the fall-through exactly as before; when it lands a frame later
                    // the press has already fallen through, and the letter still hands the line on.
                    if (engine.NewlineOnTypedLetter && freshTextPress && e.Key != Key.Space)
                        return pressFor(PressPlan.Type, time);

                    return dropPress(freshTextPress, time);
                }

                // POLYGLOT (backlog 331): characters arrive as committed TEXT (see TypeCommittedText), so a
                // character key does nothing here; it is swallowed while it would type, exactly as it is
                // without the mod, so a letter never reaches a global binding mid-line. The SPACEBAR is
                // the one typing key that stays a key, because it is the word gesture every layout and
                // IME agrees on.
                if (engine.Polyglot)
                {
                    if (e.Key == Key.Space)
                    {
                        if (!e.Repeat)
                        {
                            collapseSelection(time);

                            if (engine.ProcessKey(' ', time))
                                drawableRuleset?.RecordTypingInput(' ', time);
                        }

                        return true;
                    }

                    return polyglotSwallows(e);
                }

                // A LATIN TYPING KEY on an active, unfinished line (backlog 383). The character is
                // whatever the OS commits for the press, folded to the surface and typed when it
                // arrives (usually this very frame, see pressFor); the key itself decides only
                // whether the press is swallowed, so a letter never reaches a global binding mid-line.
                if (TypingKeys.CommitsText(e.Key))
                {
                    // The framework's own auto-repeat is discarded outright: one judgement per
                    // physical press, never a machine-gun run at the keyboard's repeat rate.
                    if (e.Repeat)
                        return typesHere(e.Key);

                    // The default surface keeps the digit row POSITIONAL (see TypingKeys.TryPositionalDigit).
                    if (!engine.Literate && TypingKeys.TryPositionalDigit(e.Key, out char digit))
                    {
                        dropPress(true, time);
                        TypeLatinText(digit.ToString(), time);
                        return true;
                    }

                    bool typed = pressFor(e.Key == Key.Space ? PressPlan.Space : PressPlan.Type, time);

                    // A letter, a digit and the spacebar are swallowed whatever they committed, as
                    // they always were. The rest of the block (the punctuation positions, the keypad
                    // operators) is swallowed exactly when its commit TYPED: under Literate a mark is
                    // a cell and is kept from its global binding, while an inert legend (a US '`' or
                    // '=') falls through to it as it always did; and without the mod a German 'ß' on
                    // the US Minus position types "ss" and must not also nudge the offset, while a US
                    // hyphen there stays inert and still reaches that binding.
                    return TypingKeys.AlwaysSwallowed(e.Key) || typed;
                }

                return false;
            }

            /// <summary>
            /// Whether a key counts as a TYPING key before its commit is known: for the gesture
            /// shadowing (typing always wins) and for a framework repeat, which commits nothing of
            /// its own. <see cref="TypingKeys.AlwaysSwallowed"/>, plus a punctuation position under Literate,
            /// where marks are cells.
            /// </summary>
            private bool typesHere(Key key) => TypingKeys.AlwaysSwallowed(key) || (engine.Literate && TypingKeys.IsPunctuationPosition(key));

            /// <summary>
            /// What a Polyglot play swallows as a character key, unchanged since backlog 331, where it
            /// was "the key would type with the punctuation surface open" on the US table: every
            /// letter and digit (top row or keypad) and the spacebar, the punctuation positions that
            /// carry a supported mark on either legend, and of the three with a mark on only one, the
            /// legend that has it (the brackets unshifted, the tilde shifted).
            /// </summary>
            private static bool polyglotSwallows(KeyDownEvent e)
            {
                if (TypingKeys.AlwaysSwallowed(e.Key))
                    return true;

                switch (e.Key)
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
                        return !e.ShiftPressed;

                    case Key.Tilde:
                        return e.ShiftPressed;

                    default:
                        return false;
                }
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
                while (engine.CaretIndex > target)
                {
                    int before = engine.CaretIndex;
                    // A PARKED typo is cleared where it sits (see TypingEngine.ProcessBackspace), so the
                    // press that removes it mutates the run without moving the caret. Read BEFORE the
                    // press: that is the only iteration the guard below has to let through.
                    bool parked = engine.CaretOnParkedTypo;

                    if (!engine.ProcessBackspace())
                        break;

                    drawableRuleset?.RecordTypingInput(TypeBeatReplayFrame.BACKSPACE, time);

                    // Defensive termination only. Every erase that reports a mutation moves the
                    // caret back, but one that reclaimed abandoned cells at the head of a line can
                    // land on 0 and be auto-skipped forward again, and a gesture must never spin.
                    //
                    // THE PARKED EXCEPTION IS NOT THAT CASE: clearing a parked typo is a real
                    // mutation of the cell the caret is on, with the caret deliberately unmoved, and
                    // breaking here would leave the rest of the selection standing - a retype
                    // selection opened over a word skip ends on exactly such a cell whenever the
                    // player has since typed characters into the gap. The next press steps back
                    // normally, so the run terminates on its own.
                    if (engine.CaretIndex >= before && !parked)
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
