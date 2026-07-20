// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Input.Events;
using osu.Framework.Timing;
using typebeat.Game.Rulesets.Objects.Drawables;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects.Drawables;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Rulesets.UI;
using osuTK.Input;

namespace typebeat.Game.Rulesets.TypeBeat.UI
{
    /// <summary>
    /// Hosts the monkeytype lyric stage. The regression-anchored <see cref="TypingEngine"/> is
    /// the gameplay/judgement authority; invisible <see cref="DrawableTypeBeatHitObject"/>s
    /// mirror its judgements into osu's scoring pipeline.
    ///
    /// The LyricOffsetMs config value is applied at a single seam: an offset clock container
    /// wrapping the engine ticker, the stage, the HUD extras AND the key handler — so
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

        private FramedOffsetClock lyricClock = null!;

        // Both cached by Player; absent in bare drawable-ruleset test scenes.
        [Resolved]
        private ScoreProcessor? scoreProcessor { get; set; }

        [Resolved]
        private HealthProcessor? healthProcessor { get; set; }

        public TypeBeatPlayfield(TypingEngine engine)
        {
            Engine = engine;
        }

        [BackgroundDependencyLoader(true)]
        private void load(TypeBeatRulesetConfigManager? config)
        {
            config?.BindWith(TypeBeatRulesetSetting.LyricOffsetMs, lyricOffset);
            config?.BindWith(TypeBeatRulesetSetting.KeyboardLayout, keyboardLayout);

            // Positive offset = lyrics later relative to the music => lyric time runs behind audio.
            // The source set here is provisional: the playfield's Clock is swapped after load
            // (FrameStabilityContainer installs the frame-stable gameplay clock on itself), so a
            // load-time capture can be a stale non-gameplay clock whose time is app uptime —
            // which ran the engine seconds ahead of the audio. Update() re-points the source at
            // the current Clock every frame, before any child of the lyric subtree ticks.
            lyricClock = new FramedOffsetClock(Clock, processSource: false) { Offset = -lyricOffset.Value };

            AddRangeInternal(new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = TypeBeatStyle.Background,
                },
                // Invisible scoring drawables (results-only; the stage does the rendering).
                HitObjectContainer,
                new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Clock = lyricClock,
                    Children = new Drawable[]
                    {
                        // Ticks the engine FIRST in this subtree so the stage and HUD read
                        // fresh engine state for the same lyric-clock frame.
                        new EngineTicker(Engine),
                        new LyricStage(Engine),
                        new TypeBeatHudOverlay(Engine),
                        new TypeBeatKeyHandler(Engine, keyboardLayout),
                    },
                },
            });
        }

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
            // Any accepted char (even a scoring-inert retype) ends the wrong-key streak.
            (healthProcessor as TypeBeatHealthProcessor)?.ResetWrongKeyStreak();

            if (lineDrawables.TryGetValue(judgement.LineIndex, out var line))
                line.ApplyCharJudgement(judgement);
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
        /// </summary>
        private partial class EngineTicker : Drawable
        {
            private readonly TypingEngine engine;

            public EngineTicker(TypingEngine engine)
            {
                this.engine = engine;
            }

            protected override void Update()
            {
                base.Update();
                engine.Update(Time.Current);
            }
        }

        /// <summary>
        /// Full-keyboard typing input, taken via raw <see cref="OnKeyDown"/> inside the ruleset
        /// input manager's subtree (raw key events pass through RulesetInputManager to its
        /// children; children receive them before the key-binding container, so typing letters
        /// wins over the vestigial Z/X action bindings). Key-repeat is honoured ONLY for
        /// backspace (hold to erase); held character keys never machine-gun judgements.
        /// Ctrl/Alt combos fall through to framework shortcuts. Raw input intentionally
        /// bypasses frame-stable/replay input — accepted limitation: no replays/autoplay.
        /// </summary>
        private partial class TypeBeatKeyHandler : Drawable
        {
            private readonly TypingEngine engine;
            private readonly IBindable<KeyboardLayout> keyboardLayout;

            public TypeBeatKeyHandler(TypingEngine engine, IBindable<KeyboardLayout> keyboardLayout)
            {
                this.engine = engine;
                this.keyboardLayout = keyboardLayout;
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

                if (e.Key == Key.BackSpace)
                {
                    // Repeat honoured: hold to erase, monkeytype-style.
                    engine.ProcessBackspace();
                    return true;
                }

                if (KeyCharMap.TryMap(e.Key, keyboardLayout.Value, out char c))
                {
                    // Holding a character key must not machine-gun judgements.
                    if (!e.Repeat)
                        engine.ProcessKey(c, Time.Current);

                    return true;
                }

                return false;
            }
        }
    }
}
