// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Logging;
using typebeat.Game.Extensions;
using typebeat.Game.Online.API;
using typebeat.Game.Online.API.Requests;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Rulesets;
using typebeat.Game.Users;

namespace typebeat.Game.Online
{
    /// <summary>
    /// A component that keeps track of the latest statistics for the local user.
    /// </summary>
    public partial class LocalUserStatisticsProvider : Component
    {
        /// <summary>
        /// Invoked whenever a change occured to the statistics of any ruleset,
        /// either due to change in local user (log out and log in) or as a result of score submission.
        /// </summary>
        /// <remarks>
        /// This does not guarantee the presence of the old statistics,
        /// specifically in the case of initial population or change in local user.
        /// </remarks>
        public event Action<UserStatisticsUpdate>? StatisticsUpdated;

        [Resolved]
        private RulesetStore rulesets { get; set; } = null!;

        [Resolved]
        private IAPIProvider api { get; set; } = null!;

        private readonly IBindable<APIUser> localUser = new Bindable<APIUser>();

        private readonly Dictionary<string, UserStatistics> statisticsCache = new Dictionary<string, UserStatistics>();

        /// <summary>
        /// Returns the <see cref="UserStatistics"/> currently available for the given ruleset.
        /// This may return null if the requested statistics has not been fetched before yet.
        /// </summary>
        /// <param name="ruleset">The ruleset to return the corresponding <see cref="UserStatistics"/> for.</param>
        public UserStatistics? GetStatisticsFor(RulesetInfo ruleset) => statisticsCache.GetValueOrDefault(ruleset.ShortName);

        protected override void LoadComplete()
        {
            base.LoadComplete();

            localUser.BindTo(api.LocalUser);
            localUser.BindValueChanged(_ =>
            {
                // queuing up requests directly on user change is unsafe, as the API status may have not been updated yet.
                // schedule a frame to allow the API to be in its correct state sending requests.
                Schedule(initialiseStatistics);
            }, true);
        }

        private void initialiseStatistics()
        {
            statisticsCache.Clear();

            if (api.LocalUser.Value == null || api.LocalUser.Value.Id <= 1)
                return;

            foreach (var ruleset in rulesets.AvailableRulesets.Where(r => r.IsLegacyRuleset()))
                RefetchStatistics(ruleset);
        }

        /// <summary>
        /// Fetches the local user's latest statistics for the given ruleset and folds them into the cache.
        /// </summary>
        /// <param name="ruleset">The ruleset to fetch statistics for.</param>
        /// <param name="callback">
        /// Optional. Invoked EXACTLY ONCE when the attempt finishes, either way: with the resulting update, or with
        /// <c>null</c> to say the fetch failed and no update is ever coming for it.
        /// <para>
        /// The nullable argument is the point of it. This request originally subscribed to the API request's success arm
        /// only, so a 404, a 500, a timeout or a dropped connection resolved nothing at all and any caller showing a
        /// placeholder while it waited (see OverallRanking) waited forever. Making the parameter nullable forces the one
        /// kind of caller that can be hurt by that, a caller that passes a callback, to decide what a failure looks like;
        /// callers that just want the cache warmed pass nothing and are unaffected.
        /// </para>
        /// </param>
        public void RefetchStatistics(RulesetInfo ruleset, Action<UserStatisticsUpdate?>? callback = null)
        {
            if (!ruleset.IsLegacyRuleset())
                throw new InvalidOperationException($@"Retrieving statistics is not supported for ruleset {ruleset.ShortName}");

            var request = new GetUserRequest(api.LocalUser.Value.Id, ruleset);
            request.Success += u => UpdateStatistics(u.Statistics, ruleset, callback);
            request.Failure += exception =>
            {
                // deliberately not Logger.Error: a lost connection is ordinary, and an error entry would pop a
                // "something went wrong" notification on top of the results screen after every failed submission.
                Logger.Log($@"Failed to fetch local user statistics for ruleset {ruleset.ShortName}: {exception.Message}", LoggingTarget.Network);

                // the cache is deliberately left alone. A failed fetch is not evidence that the statistics changed,
                // and dropping the entry would turn the next successful fetch into a second "no previous statistics".
                callback?.Invoke(null);
            };
            api.Queue(request);
        }

        protected void UpdateStatistics(UserStatistics newStatistics, RulesetInfo ruleset, Action<UserStatisticsUpdate?>? callback = null)
        {
            var oldStatistics = statisticsCache.GetValueOrDefault(ruleset.ShortName);
            statisticsCache[ruleset.ShortName] = newStatistics;

            var update = new UserStatisticsUpdate(ruleset, oldStatistics, newStatistics);
            callback?.Invoke(update);
            StatisticsUpdated?.Invoke(update);
        }
    }

    public record UserStatisticsUpdate(RulesetInfo Ruleset, UserStatistics? OldStatistics, UserStatistics NewStatistics);
}
