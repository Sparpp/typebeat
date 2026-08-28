// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Ported from type!beat TypeBeat.Game/UI/HudOverlay.cs, slimmed for the type!beat fork:
// score/combo/accuracy readouts dropped (type!beat's own HUD shows those from the
// ScoreProcessor); the SyncBar and hit-error meters were removed by design; the only
// engine-authoritative extras left are the WPM / sync% readouts, plus the live pp counter
// (which is score-processor authoritative, not engine authoritative, see below).

using System;
using System.Collections.Generic;
using System.Globalization;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using typebeat.Game.Beatmaps;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.UI
{
    /// <summary>
    /// Playfield-level HUD extras: top-centre WPM / sync% / pp readouts, polled each frame, plus a
    /// playback-rate readout that appears only while a mod is publishing one.
    /// Mounted under the playfield's lyric-offset clock container so <c>Time.Current</c> is
    /// lyric-gameplay time.
    /// </summary>
    public partial class TypeBeatHudOverlay : CompositeDrawable
    {
        /// <summary>
        /// What the pp readout shows for a play that can never earn pp (see
        /// <see cref="starRating"/>). Deliberately not a number: a live "214" on a play the server
        /// will store at 0 pp would be a lie the player only discovers on the results screen.
        /// Aliases <see cref="PerformancePointsDisplay.INELIGIBLE_TEXT"/> rather than re-stating it,
        /// so this counter and the results screen render an ineligible play identically.
        /// </summary>
        public const string INELIGIBLE_TEXT = PerformancePointsDisplay.INELIGIBLE_TEXT;

        private readonly TypingEngine engine;

        private OsuSpriteText wpmValue = null!;
        private OsuSpriteText syncValue = null!;
        private OsuSpriteText ppValue = null!;
        private OsuSpriteText rateValue = null!;

        /// <summary>
        /// The "rate" stat, built always and PRESENT only while something is publishing a rate (the
        /// Conductor mod). A <see cref="FillFlowContainer"/> lays out only its present children, so
        /// an alpha of 0 takes the column out of the row entirely rather than leaving a hole.
        /// </summary>
        private Drawable rateStat = null!;

        /// <summary>Last whole percent rendered, so a steady rate costs no string allocation.</summary>
        private int lastRatePercent = -1;

        // Cached by DrawableTypeBeatRuleset for its subtree; absent in bare playfield test scenes.
        // Carries the Conductor mod's live rate (null = no mod is following the player).
        [Resolved]
        private DrawableTypeBeatRuleset? drawableRuleset { get; set; }

        // Both cached by Player; absent in bare drawable-ruleset test scenes.
        [Resolved]
        private ScoreProcessor? scoreProcessor { get; set; }

        /// <summary>
        /// The star rating this play is priced at, or null when it is pp-INELIGIBLE and the readout
        /// is frozen at <see cref="INELIGIBLE_TEXT"/>. Computed once, at load: it is a full pass
        /// over the map's words, and nothing that can move it changes during a play.
        ///
        /// <para>The three ways a play is ineligible are the three the server would refuse to pay
        /// for, so the counter never promises pp that will not be awarded:</para>
        /// <list type="number">
        /// <item>a CUSTOM rate (only the DT/NC 1.50x and HT 0.75x base rates earn pp, docs/pp.md);</item>
        /// <item>any UNRANKED mod in the stack (Mashing, Autoplay, ...), which makes the submission
        /// path store the score <c>ranked = false</c>;</item>
        /// <item>a map that grants no pp (anything not Ranked/Approved: a local map, an unsubmitted
        /// map, a work-in-progress).</item>
        /// </list>
        /// A FAILED play is deliberately NOT in that list. Failing is not knowable in advance, the
        /// counter's contract is "what this play is worth if it ends right here", and a run that
        /// still might be no-failed or recovered should keep showing what it is building.
        ///
        /// <para>SO THE COUNTER DOES NOT MOVE ON A SPOTLESS RUN, and that is correct rather than
        /// broken (backlog 152, backlog 154). Since length pricing left pp for the star rating, a
        /// play with no misses, no typos and an unbroken combo is worth
        /// <c>scale · SR^2 · acc^1.8</c> exactly, and the note count appears in none of those: it
        /// survives only under the two penalty terms, the combo ratio and Flashlight's bonus, all
        /// of which sit at 1.0 on a clean play. A perfect run therefore reads its final value from
        /// the first character and holds it, which is precisely "what this play is worth if it ends
        /// right here". Before backlog 152 the deleted length factor climbed from 0.1 to about 1.35
        /// across a 500-cell map and was the ONLY thing animating this readout. Any mistake puts the
        /// count back into the arithmetic through the penalty denominators and it moves again.</para>
        /// </summary>
        private double? starRating;

        /// <summary>
        /// The rate multiplier that goes with <see cref="starRating"/>, 1.0 for every play but a
        /// base-rate Half Time one (backlog 90). Resolved once at load, like the rating, because
        /// both are functions of the map and the mods and neither can move mid-play.
        /// </summary>
        private double rateMultiplier = 1;

        private IReadOnlyList<Mod>? mods;

        // Last state the readout was computed from, so a frame that judged nothing does no work.
        private PerformancePoints.NoteCounts lastCounts = new PerformancePoints.NoteCounts(-1, -1, -1);
        private int lastMaxCombo = -1;

        public TypeBeatHudOverlay(TypingEngine engine)
        {
            this.engine = engine;
            RelativeSizeAxes = Axes.Both;
        }

        [BackgroundDependencyLoader(true)]
        private void load(IBeatmap? playableBeatmap, IReadOnlyList<Mod>? gameplayMods)
        {
            InternalChild = new FillFlowContainer
            {
                Anchor = Anchor.TopCentre,
                Origin = Anchor.TopCentre,
                AutoSizeAxes = Axes.Both,
                Direction = FillDirection.Horizontal,
                Spacing = new osuTK.Vector2(36, 0),
                Margin = new MarginPadding { Top = 24 },
                Children = new[]
                {
                    stat("wpm", out wpmValue),
                    stat("sync", out syncValue),
                    stat("pp", out ppValue),
                    rateStat = stat("rate", out rateValue),
                },
            };

            rateStat.Alpha = 0;

            mods = gameplayMods;
            starRating = StarRatingFor(playableBeatmap, gameplayMods);
            rateMultiplier = PerformancePointsDisplay.RateMultiplierFor(playableBeatmap, gameplayMods);
            ppValue.Text = PerformancePointsDisplay.Format(starRating == null ? null : 0d);
        }

        /// <summary>
        /// The rating to price this play at, or null when it is ineligible (see
        /// <see cref="starRating"/>). Delegates to
        /// <see cref="PerformancePointsDisplay.StarRatingFor"/>, which is where the gates and their
        /// reasoning live, so this counter and the results screen apply exactly the same ones.
        ///
        /// <para>Kept here, and public, because the task 74 tests drive the gate the HUD uses
        /// through this name rather than through a paraphrase of it.</para>
        /// </summary>
        public static double? StarRatingFor(IBeatmap? playableBeatmap, IReadOnlyList<Mod>? mods)
            => PerformancePointsDisplay.StarRatingFor(playableBeatmap, mods);

        private Drawable stat(string caption, out OsuSpriteText value)
        {
            value = new OsuSpriteText
            {
                Anchor = Anchor.TopCentre,
                Origin = Anchor.TopCentre,
                Font = TypeBeatStyle.Mono(30),
                Colour = TypeBeatStyle.TypedChar,
                Text = "0",
                ShadowColour = TypeBeatStyle.TextShadow,
                ShadowOffset = TypeBeatStyle.TEXT_SHADOW_OFFSET,
            };

            return new FillFlowContainer
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
                    value,
                },
            };
        }

        protected override void Update()
        {
            base.Update();

            // Rolling window over the last few dozen keypresses, not the whole-run average: the live
            // readout should track current pace. The results screen still reports the whole-run figure.
            wpmValue.Text = engine.LiveRollingWpm.ToString("0");
            syncValue.Text = engine.LiveSyncPercent.ToString("0.0") + "%";

            updatePerformancePoints();
            updateRate();
        }

        /// <summary>
        /// The Conductor mod's live playback rate, as a whole percent. Players need to SEE the song
        /// responding to them to trust that it is: without this the mod is an unexplained wobble.
        /// Absent (and taking no space in the row) on every play with no rate-following mod.
        /// </summary>
        private void updateRate()
        {
            if (drawableRuleset?.ConductorRate is not double rate)
            {
                rateStat.Alpha = 0;
                return;
            }

            rateStat.Alpha = 1;

            int percent = (int)Math.Round(rate * 100);

            if (percent == lastRatePercent)
                return;

            lastRatePercent = percent;
            rateValue.Text = percent.ToString(CultureInfo.InvariantCulture) + "%";
        }

        /// <summary>
        /// "What this play is worth if it ends right here". The formula's <c>notes</c> is the count
        /// of JUDGED notes, so feeding it the live counts makes the readout land, on the play's last
        /// judgement, on exactly the value the server will store for the submitted score (pinned by
        /// <c>PerformancePointsHudTest.LiveCounterConvergesOnTheSubmittedScoresValue</c>).
        ///
        /// <para>Recomputed only when a judgement actually moved the counts or the combo, so a
        /// typical frame costs one scan of a dictionary with a handful of entries and no
        /// <c>Math.Pow</c> at all. Every input the formula takes moves with those: accuracy cannot
        /// change without a note being judged, and a mistype shows up as its own count.</para>
        ///
        /// <para>The accuracy fed in is the score processor's RUNNING accuracy (denominator: what
        /// has been judged so far), which is the only honest reading mid-play and is exactly equal
        /// to the whole-map accuracy the server recomputes once every cell has been judged. The two
        /// diverge only for a play that ends early, i.e. a FAIL, which is stored unranked and earns
        /// nothing anyway.</para>
        /// </summary>
        private void updatePerformancePoints()
        {
            if (scoreProcessor == null || starRating is not double stars)
                return;

            var counts = PerformancePoints.CountNotes(scoreProcessor.Statistics);
            int maxCombo = scoreProcessor.HighestCombo.Value;

            if (counts == lastCounts && maxCombo == lastMaxCombo)
                return;

            lastCounts = counts;
            lastMaxCombo = maxCombo;

            ppValue.Text = PerformancePointsDisplay.Format(PerformancePoints.ForPlay(stars, counts, scoreProcessor.Accuracy.Value, maxCombo, mods, rateMultiplier));
        }
    }
}
