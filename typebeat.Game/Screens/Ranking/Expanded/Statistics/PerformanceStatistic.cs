// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using typebeat.Game.Beatmaps;
using typebeat.Game.Graphics;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Resources.Localisation.Web;
using typebeat.Game.Scoring;
using typebeat.Game.Localisation;

namespace typebeat.Game.Screens.Ranking.Expanded.Statistics
{
    /// <summary>
    /// The score panel's pp readout.
    ///
    /// <para>
    /// THREE STATES, and the middle one is why this is not simply a counter. A play can be worth a
    /// NUMBER (0 included, which a give-up run genuinely earns), or it can be INELIGIBLE, meaning no
    /// number describes it because none was ever on offer. Rendering the second as "0" would claim
    /// the player earned nothing when the truth is that nothing was there to earn, so an ineligible
    /// play shows <see cref="INELIGIBLE_TEXT"/> and explains itself in the tooltip.
    /// </para>
    ///
    /// <para>
    /// This replaces two older behaviours that both misreported. A play that failed an eligibility
    /// check used to show a DIMMED NUMBER, which reads as "you nearly had this" rather than "this
    /// never counted"; and a ruleset whose <see cref="Rulesets.Difficulty.PerformanceCalculator"/>
    /// could not run left the counter at its default and printed a hardcoded 0.
    /// </para>
    ///
    /// <para>
    /// A value the SERVER supplied wins outright, ahead of the eligibility check. Safe, because the
    /// server sends a number only for a play it actually priced and null for everything else, so a
    /// stored value is proof of eligibility by itself. Necessary, because the local copy of a map
    /// can drift from the ranked one a score was set on (opening it in the editor marks it
    /// LocallyModified), and that must not hide pp the player really earned.
    /// </para>
    /// </summary>
    public partial class PerformanceStatistic : StatisticDisplay, IHasTooltip
    {
        /// <summary>Shown for a play that can never earn pp. Deliberately not a number.</summary>
        public const string INELIGIBLE_TEXT = "-";

        public LocalisableString TooltipText { get; private set; }

        private readonly ScoreInfo score;

        /// <summary>
        /// Whether this score gets a number at all. Decided in the constructor, because
        /// <see cref="CreateContent"/> runs from the BASE class's loader, ahead of this class's own,
        /// so which drawable to build has to be settled before any async work starts.
        /// </summary>
        private readonly bool earnsPerformancePoints;

        private readonly Bindable<int> performance = new Bindable<int>();

        private readonly CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

        private RollingCounter<int>? counter;

        public PerformanceStatistic(ScoreInfo score)
            : base(BeatmapsetsStrings.ShowScoreboardHeaderspp)
        {
            this.score = score;

            earnsPerformancePoints = ScoreEarnsPerformancePoints(score);

            if (!earnsPerformancePoints)
            {
                TooltipText = score.BeatmapInfo?.Status.GrantsPerformancePoints() != true
                    ? ResultsScreenStrings.NoPPForUnrankedBeatmaps
                    : score.Rank == ScoreRank.F || !score.Passed
                        ? ResultsScreenStrings.NoPPForFailedScores
                        : ResultsScreenStrings.NoPPForUnrankedMods;
            }
        }

        /// <summary>
        /// Whether a number may be shown for this score at all: a server-supplied price, or the
        /// ruleset's own eligibility rule (<see cref="Rulesets.Ruleset.ScoreEarnsPerformancePoints"/>,
        /// which is the single authority so this panel and the ruleset's own statistics table cannot
        /// gate differently). Public so a test can pin the two surfaces against the same call.
        /// </summary>
        public static bool ScoreEarnsPerformancePoints(ScoreInfo score)
            // The null-conditional covers a hand-built score carrying no ruleset reference, which
            // degrades to the dash. Showing nothing for a score we cannot ask about is the safe
            // direction; the old fallback was a hardcoded 0, which asserted something false.
            => score.PP.HasValue || score.Ruleset?.CreateInstance().ScoreEarnsPerformancePoints(score) == true;

        [BackgroundDependencyLoader]
        private void load(BeatmapDifficultyCache difficultyCache, CancellationToken? cancellationToken)
        {
            if (!earnsPerformancePoints)
            {
                Alpha = 0.5f;
                return;
            }

            if (score.PP.HasValue)
            {
                performance.Value = toDisplayValue(score.PP.Value);
                return;
            }

            Task.Run(async () =>
            {
                var attributes = await difficultyCache.GetDifficultyAsync(score.BeatmapInfo!, score.Ruleset, score.Mods, cancellationToken ?? CancellationToken.None).ConfigureAwait(false);
                var performanceCalculator = score.Ruleset.CreateInstance().CreatePerformanceCalculator();

                // Performance calculation requires the beatmap and ruleset to be locally available. If not, return a default value.
                if (attributes?.DifficultyAttributes == null || performanceCalculator == null)
                    return;

                var result = await performanceCalculator.CalculateAsync(score, attributes.Value.DifficultyAttributes, cancellationToken ?? CancellationToken.None).ConfigureAwait(false);

                Schedule(() => performance.Value = toDisplayValue(result.Total));
            }, cancellationToken ?? CancellationToken.None);
        }

        private static int toDisplayValue(double pp) => (int)Math.Round(pp, MidpointRounding.AwayFromZero);

        public override void Appear()
        {
            base.Appear();

            counter?.Current.BindTo(performance);
        }

        protected override void Dispose(bool isDisposing)
        {
            cancellationTokenSource.Cancel();
            base.Dispose(isDisposing);
        }

        protected override Drawable CreateContent()
        {
            if (!earnsPerformancePoints)
            {
                return new OsuSpriteText
                {
                    Anchor = Anchor.TopCentre,
                    Origin = Anchor.TopCentre,
                    Font = OsuFont.Torus.With(size: 20, weight: FontWeight.SemiBold),
                    Text = INELIGIBLE_TEXT,
                };
            }

            return counter = new StatisticCounter
            {
                Anchor = Anchor.TopCentre,
                Origin = Anchor.TopCentre
            };
        }
    }
}
