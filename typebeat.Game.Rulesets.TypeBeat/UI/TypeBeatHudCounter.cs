// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using typebeat.Game.Beatmaps;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Screens.Edit.GameplayTest;
using typebeat.Game.Skinning;

namespace typebeat.Game.Rulesets.TypeBeat.UI
{
    /// <summary>A labelled readout that the skin editor can position and scale on its own.</summary>
    public abstract partial class TypeBeatHudCounter : CompositeDrawable, ISerialisableDrawable
    {
        private readonly OsuSpriteText value;

        public bool UsesFixedAnchor { get; set; } = true;

        public string DisplayedText => value.Text.ToString();

        protected string Text
        {
            set => this.value.Text = value;
        }

        protected TypeBeatHudCounter(string caption)
        {
            AutoSizeAxes = Axes.Both;
            InternalChild = new FillFlowContainer
            {
                AutoSizeAxes = Axes.Both,
                Direction = FillDirection.Vertical,
                Children = new Drawable[]
                {
                    new OsuSpriteText
                    {
                        Anchor = Anchor.TopCentre,
                        Origin = Anchor.TopCentre,
                        Font = TypeBeatStyle.Mono(14),
                        Colour = TypeBeatStyle.UntypedChar,
                        Text = caption,
                        ShadowColour = TypeBeatStyle.TextShadow,
                        ShadowOffset = TypeBeatStyle.TEXT_SHADOW_OFFSET,
                    },
                    value = new OsuSpriteText
                    {
                        Anchor = Anchor.TopCentre,
                        Origin = Anchor.TopCentre,
                        Font = TypeBeatStyle.Mono(30),
                        Colour = TypeBeatStyle.TypedChar,
                        Text = "0",
                        ShadowColour = TypeBeatStyle.TextShadow,
                        ShadowOffset = TypeBeatStyle.TEXT_SHADOW_OFFSET,
                    },
                },
            };
        }
    }

    public partial class TypeBeatWpmCounter : TypeBeatHudCounter
    {
        [Resolved]
        private TypeBeatPlayfield? playfield { get; set; }

        public TypeBeatWpmCounter()
            : base("wpm")
        {
        }

        protected override void Update()
        {
            base.Update();
            // The engine owns rate handling, including the editor preview tempo.
            // Applying the editor tempo again here would scale the counter twice.
            if (playfield != null)
                Text = playfield.Engine.LiveRollingWpm.ToString("0");
        }
    }

    public partial class TypeBeatPpCounter : TypeBeatHudCounter
    {
        [Resolved]
        private ScoreProcessor? scoreProcessor { get; set; }

        private double? starRating;
        private double difficultCharacters;
        private double playedDurationSeconds;
        private IReadOnlyList<Mod>? mods;
        private PerformancePoints.NoteCounts lastCounts = new PerformancePoints.NoteCounts(-1, -1, -1);
        private int lastMaxCombo = -1;

        public TypeBeatPpCounter()
            : base("pp")
        {
        }

        [BackgroundDependencyLoader(true)]
        private void load(IBeatmap? playableBeatmap, IReadOnlyList<Mod>? gameplayMods, EditorTestPlayback? editorPlayback)
        {
            mods = gameplayMods;
            double statisticsRate = editorPlayback?.StatisticsRate ?? 1;
            var difficulty = PerformancePointsDisplay.DifficultyFor(playableBeatmap, gameplayMods, requireRankedMap: false, playbackRate: statisticsRate);
            difficultCharacters = difficulty?.DifficultCharacters ?? 0;
            starRating = difficulty?.Stars;

            // The short-map factor reads the played span, so it takes the same editor preview rate the
            // rating above was computed at (PlayedDurationFor divides by the mod rate alone).
            playedDurationSeconds = playableBeatmap == null
                ? 0
                : PerformancePoints.PlayedDurationFor(playableBeatmap.HitObjects.OfType<TypeBeatHitObject>().Select(h => h.Line), gameplayMods) / statisticsRate;
            Text = PerformancePointsDisplay.Format(starRating == null ? null : 0d);
        }

        protected override void Update()
        {
            base.Update();

            if (scoreProcessor == null || starRating is not double stars)
                return;

            var counts = PerformancePoints.CountNotes(scoreProcessor.Statistics) with { DifficultCharacters = difficultCharacters };
            int maxCombo = scoreProcessor.HighestCombo.Value;

            if (counts == lastCounts && maxCombo == lastMaxCombo)
                return;

            lastCounts = counts;
            lastMaxCombo = maxCombo;
            Text = PerformancePointsDisplay.Format(PerformancePoints.ForPlay(stars, counts, scoreProcessor.Accuracy.Value, maxCombo, mods, playedDurationSeconds));
        }
    }
}
