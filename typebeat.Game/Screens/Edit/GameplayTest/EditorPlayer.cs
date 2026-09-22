// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Graphics.Containers;
using osu.Framework.Input.Bindings;
using osu.Framework.Input.Events;
using osu.Framework.Screens;
using typebeat.Game.Beatmaps;
using typebeat.Game.Input.Bindings;
using typebeat.Game.Overlays;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Objects;
using typebeat.Game.Rulesets.Objects.Drawables;
using typebeat.Game.Scoring;
using typebeat.Game.Screens.Play;
using typebeat.Game.Screens.Play.Leaderboards;
using typebeat.Game.Screens.Ranking;
using typebeat.Game.Users;

namespace typebeat.Game.Screens.Edit.GameplayTest
{
    public partial class EditorPlayer : Player, IKeyBindingHandler<GlobalAction>
    {
        private readonly Editor editor;
        private readonly EditorState editorState;

        protected override UserActivity InitialActivity => new UserActivity.TestingBeatmap(Beatmap.Value.BeatmapInfo);

        [Resolved]
        private MusicController musicController { get; set; } = null!;

        [Cached(typeof(IGameplayLeaderboardProvider))]
        private EmptyGameplayLeaderboardProvider leaderboardProvider = new EmptyGameplayLeaderboardProvider();

        public EditorPlayer(Editor editor)
            : base(new PlayerConfiguration { ShowResults = false })
        {
            this.editor = editor;
            editorState = editor.GetState();
        }

        protected override GameplayClockContainer CreateGameplayClockContainer(WorkingBeatmap beatmap, double gameplayStart)
        {
            var masterGameplayClockContainer = new MasterGameplayClockContainer(beatmap, gameplayStart);

            // Only reset the time to the current point if the editor is later than the normal start time (and the first object).
            // This allows more sane test playing from the start of the beatmap (ie. correctly adding lead-in time).
            if (editorState.Time > gameplayStart && editorState.Time > DrawableRuleset.Objects.FirstOrDefault()?.StartTime)
                masterGameplayClockContainer.Reset(editorState.Time);

            return masterGameplayClockContainer;
        }

        protected override void LoadAsyncComplete()
        {
            base.LoadAsyncComplete();

            if (!LoadedBeatmapSuccessfully)
                return;

            // This hack needs to be called to install its hooks before drawable hit objects get the chance to run update logic,
            // because it will not work otherwise due to being too late (various effects of the objects getting missed will have already taken place).
            preventMissOnPreviousHitObjects();
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            ScoreProcessor.HasCompleted.BindValueChanged(completed =>
            {
                if (completed.NewValue)
                {
                    Scheduler.AddDelayed(() =>
                    {
                        if (this.IsCurrentScreen())
                            this.Exit();
                    }, RESULTS_DISPLAY_DELAY);
                }
            });
        }

        /// <summary>
        /// Resolves every hit object the play found already behind it as its best result, so a test play
        /// started part-way through a map does not drop a line of misses on the player as it begins.
        ///
        /// <para>The SKIPPED PREFIX's RESULTS no longer come from here: the ruleset grants a character
        /// that was already due when the play began at its line's seal
        /// (<c>TypeBeatPlayfield.cellWasDueBeforeThePlayStarted</c>), from the same start time the engine
        /// puts its caret by. Applying them here as well - which the editor player used to do, straight
        /// into the score processor - counted every skipped character TWICE, once as that granted hit
        /// and once as the miss the engine's own seal charged for it, which is what left a test play
        /// starting at 50% accuracy instead of 100%.</para>
        ///
        /// <para>What is left here is the DRAWABLE side of the same idea, and it is deliberately
        /// narrow: an object that comes alive during the play after the playhead has already passed it
        /// (the line under the cursor, and the characters of it that are behind them) is resolved
        /// directly, so it neither misses nor draws as unfinished. Objects that never come alive are not
        /// reachable this way at all, and do not need to be - the seal covers them.</para>
        /// </summary>
        private void preventMissOnPreviousHitObjects()
        {
            void preventMiss(HitObject hitObject)
            {
                var drawableObject = DrawableRuleset.Playfield.HitObjectContainer
                                                    .AliveObjects
                                                    .SingleOrDefault(it => it.HitObject == hitObject);

                if (drawableObject != null)
                    preventMissOnDrawable(drawableObject);
            }

            void preventMissOnDrawable(DrawableHitObject drawableObject)
            {
                foreach (var nested in drawableObject.NestedHitObjects)
                    preventMissOnDrawable(nested);

                if (drawableObject.Entry != null && drawableObject.HitObject.GetEndTime() < editorState.Time)
                {
                    var result = drawableObject.CreateResult(drawableObject.HitObject.Judgement);
                    result.Type = result.Judgement.MaxResult;
                    drawableObject.Entry.Result = result;
                }
            }

            void removeListener()
            {
                if (!DrawableRuleset.Playfield.IsLoaded)
                {
                    Schedule(removeListener);
                    return;
                }

                DrawableRuleset.Playfield.HitObjectUsageBegan -= preventMiss;
            }

            DrawableRuleset.Playfield.HitObjectUsageBegan += preventMiss;

            Schedule(removeListener);
        }

        protected override void PrepareReplay()
        {
            // don't record replays.
        }

        protected override bool CheckModsAllowFailure() => false; // never fail.

        public bool OnPressed(KeyBindingPressEvent<GlobalAction> e)
        {
            if (e.Repeat)
                return false;

            switch (e.Action)
            {
                case GlobalAction.EditorTestPlayToggleAutoplay:
                    if (PauseOverlay?.State.Value == Visibility.Visible || DrawableRuleset.ResumeOverlay?.State.Value == Visibility.Visible)
                        return true;

                    toggleAutoplay();
                    return true;

                case GlobalAction.EditorTestPlayToggleQuickPause:
                    if (PauseOverlay?.State.Value == Visibility.Visible || DrawableRuleset.ResumeOverlay?.State.Value == Visibility.Visible)
                        return true;

                    toggleQuickPause();
                    return true;

                case GlobalAction.EditorTestPlayQuickExitToInitialTime:
                    quickExit(false);
                    return true;

                case GlobalAction.EditorTestPlayQuickExitToCurrentTime:
                    quickExit(true);
                    return true;

                default:
                    return false;
            }
        }

        public void OnReleased(KeyBindingReleaseEvent<GlobalAction> e)
        {
        }

        private void toggleAutoplay()
        {
            if (DrawableRuleset.ReplayScore == null)
            {
                var autoplay = Ruleset.Value.CreateInstance().GetAutoplayMod();
                if (autoplay == null)
                    return;

                var score = autoplay.CreateScoreFromReplayData(GameplayState.Beatmap, [autoplay]);

                // remove past frames to prevent replay frame handler from seeking back to start in an attempt to play back the entirety of the replay.
                score.Replay.Frames.RemoveAll(f => f.Time <= GameplayClockContainer.CurrentTime);

                DrawableRuleset.SetReplayScore(score);
                // Without this schedule, the `GlobalCursorDisplay.Update()` machinery will fade the gameplay cursor out, but we still want it to show.
                Schedule(() => DrawableRuleset.Cursor?.Show());
            }
            else
                DrawableRuleset.SetReplayScore(null);
        }

        private void toggleQuickPause()
        {
            if (GameplayClockContainer.IsPaused.Value)
                GameplayClockContainer.Start();
            else
                GameplayClockContainer.Stop();
        }

        private void quickExit(bool useCurrentTime)
        {
            if (useCurrentTime)
                editorState.Time = GameplayClockContainer.CurrentTime;

            editor.RestoreState(editorState);
            this.Exit();
        }

        public override void OnEntering(ScreenTransitionEvent e)
        {
            base.OnEntering(e);

            // finish alpha transforms on entering to avoid gameplay starting in a half-hidden state.
            // the finish calls are purposefully not propagated to children to avoid messing up their state.
            FinishTransforms();
            GameplayClockContainer.FinishTransforms(false, nameof(Alpha));
        }

        public override bool OnExiting(ScreenExitEvent e)
        {
            musicController.Stop();

            editor.RestoreState(editorState);
            return base.OnExiting(e);
        }

        protected override ResultsScreen CreateResults(ScoreInfo score) => throw new NotSupportedException();
    }
}
