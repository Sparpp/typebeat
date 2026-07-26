// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Bindables;
using osu.Framework.Logging;
using osu.Framework.Platform;
using typebeat.Game.Beatmaps;
using typebeat.Game.Configuration;
using typebeat.Game.Database;
using typebeat.Game.IO.Archives;
using typebeat.Game.Online.API;
using typebeat.Game.Overlays.Notifications;
using typebeat.Game.Rulesets;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Scoring.Legacy;

namespace typebeat.Game.Scoring
{
    public class ScoreManager : ModelManager<ScoreInfo>, IModelImporter<ScoreInfo>
    {
        private readonly Func<BeatmapManager> beatmaps;
        private readonly OsuConfigManager? configManager;
        private readonly ScoreImporter scoreImporter;
        private readonly LegacyScoreExporter scoreExporter;

        public override bool PauseImports
        {
            get => base.PauseImports;
            set
            {
                base.PauseImports = value;
                scoreImporter.PauseImports = value;
            }
        }

        public ScoreManager(RulesetStore rulesets, Func<BeatmapManager> beatmaps, Storage storage, RealmAccess realm, IAPIProvider api,
                            OsuConfigManager? configManager = null)
            : base(storage, realm)
        {
            this.beatmaps = beatmaps;
            this.configManager = configManager;

            scoreImporter = new ScoreImporter(rulesets, beatmaps, storage, realm, api)
            {
                PostNotification = obj => PostNotification?.Invoke(obj)
            };

            scoreExporter = new LegacyScoreExporter(storage)
            {
                PostNotification = obj => PostNotification?.Invoke(obj)
            };
        }

        /// <summary>
        /// Retrieve a <see cref="Score"/> from a given <see cref="IScoreInfo"/>.
        /// </summary>
        /// <param name="scoreInfo">The <see cref="IScoreInfo"/> to convert.</param>
        /// <returns>The <see cref="Score"/>. Null if the score cannot be found in the database.</returns>
        /// <remarks>
        /// The <see cref="IScoreInfo"/> is re-retrieved from the database to ensure all the required data
        /// for retrieving a replay are present (may have missing properties if it was retrieved from online data).
        /// </remarks>
        public Score? GetScore(IScoreInfo scoreInfo)
        {
            ScoreInfo? databasedScoreInfo = getDatabasedScoreInfo(scoreInfo);

            return databasedScoreInfo == null ? null : scoreImporter.GetScore(databasedScoreInfo);
        }

        /// <summary>
        /// Perform a lookup query on available <see cref="ScoreInfo"/>s.
        /// </summary>
        /// <param name="query">The query.</param>
        /// <returns>The first result for the provided query in its detached form, or null if no results were found.</returns>
        public ScoreInfo? Query(Expression<Func<ScoreInfo, bool>> query)
        {
            return Realm.Run(r => r.All<ScoreInfo>().FirstOrDefault(query)?.Detach());
        }

        private ScoreInfo? getDatabasedScoreInfo(IScoreInfo originalScoreInfo)
        {
            ScoreInfo? databasedScoreInfo = null;

            if (originalScoreInfo is ScoreInfo scoreInfo)
            {
                if (scoreInfo.IsManaged)
                    return scoreInfo.Detach();

                if (!string.IsNullOrEmpty(scoreInfo.Hash))
                    databasedScoreInfo = Query(s => s.Hash == scoreInfo.Hash);
            }

            if (originalScoreInfo.OnlineID > 0)
                databasedScoreInfo ??= Query(s => s.OnlineID == originalScoreInfo.OnlineID);

            if (originalScoreInfo.LegacyOnlineID > 0)
                databasedScoreInfo ??= Query(s => s.LegacyOnlineID == originalScoreInfo.LegacyOnlineID);

            if (databasedScoreInfo == null)
            {
                Logger.Log("The requested score could not be found locally.", LoggingTarget.Information);
                return null;
            }

            return databasedScoreInfo;
        }

        /// <summary>
        /// Find a locally stored score that carries a replay file and corresponds to the given score,
        /// which may be an online-only leaderboard row.
        /// </summary>
        /// <remarks>
        /// Matching mirrors <c>ScoreDownloadTracker</c>'s realm subscription: hash first (only ever set
        /// on locally imported scores), then online id, then legacy online id. An online row that the
        /// local user set themselves carries the online id that was written back into realm at submission
        /// time, which is what makes the local replay reachable from a leaderboard row.
        /// Unlike <see cref="GetScore"/> this stays quiet when nothing matches, because it is called
        /// speculatively (once per context menu open, once per leaderboard fetch).
        /// </remarks>
        /// <param name="scoreInfo">The score to resolve.</param>
        /// <returns>The detached local score, or null if there is no local match holding a replay.</returns>
        public ScoreInfo? FindLocalScoreWithReplay(IScoreInfo scoreInfo)
        {
            ScoreInfo? local = null;

            if (scoreInfo is ScoreInfo typed && !string.IsNullOrEmpty(typed.Hash))
            {
                string hash = typed.Hash;
                local = Query(s => s.Hash == hash && !s.DeletePending);
            }

            if (local == null && scoreInfo.OnlineID > 0)
            {
                long onlineId = scoreInfo.OnlineID;
                local = Query(s => s.OnlineID == onlineId && !s.DeletePending);
            }

            if (local == null && scoreInfo.LegacyOnlineID > 0)
            {
                long legacyOnlineId = scoreInfo.LegacyOnlineID;
                local = Query(s => s.LegacyOnlineID == legacyOnlineId && !s.DeletePending);
            }

            return local != null && local.Files.Any(ScoreInfoExtensions.IsReplayFile) ? local : null;
        }

        /// <summary>
        /// Whether a replay for the given score is present in the local score store.
        /// </summary>
        public bool HasLocalReplay(IScoreInfo scoreInfo) => FindLocalScoreWithReplay(scoreInfo) != null;

        /// <summary>
        /// Read back the raw .osr bytes stored alongside a local score, for re-uploading to the server.
        /// </summary>
        /// <returns>The .osr payload, or null when the score has no replay file (or it is missing on disk).</returns>
        public byte[]? GetRawReplayBytes(ScoreInfo score) => scoreImporter.GetRawReplayBytes(score);

        /// <summary>
        /// Retrieves a bindable that represents the total score of a <see cref="ScoreInfo"/>.
        /// </summary>
        /// <remarks>
        /// Responds to changes in the currently-selected <see cref="ScoringMode"/>.
        /// </remarks>
        /// <param name="score">The <see cref="ScoreInfo"/> to retrieve the bindable for.</param>
        /// <returns>The bindable containing the total score.</returns>
        public Bindable<long> GetBindableTotalScore(ScoreInfo score) => new TotalScoreBindable(score, configManager);

        /// <summary>
        /// Retrieves a bindable that represents the formatted total score string of a <see cref="ScoreInfo"/>.
        /// </summary>
        /// <remarks>
        /// Responds to changes in the currently-selected <see cref="ScoringMode"/>.
        /// </remarks>
        /// <param name="score">The <see cref="ScoreInfo"/> to retrieve the bindable for.</param>
        /// <returns>The bindable containing the formatted total score string.</returns>
        public Bindable<string> GetBindableTotalScoreString(ScoreInfo score) => new TotalScoreStringBindable(GetBindableTotalScore(score));

        /// <summary>
        /// Provides the total score of a <see cref="ScoreInfo"/>. Responds to changes in the currently-selected <see cref="ScoringMode"/>.
        /// </summary>
        private class TotalScoreBindable : Bindable<long>
        {
            private readonly Bindable<ScoringMode> scoringMode = new Bindable<ScoringMode>();

            /// <summary>
            /// Creates a new <see cref="TotalScoreBindable"/>.
            /// </summary>
            /// <param name="score">The <see cref="ScoreInfo"/> to provide the total score of.</param>
            /// <param name="configManager">The config.</param>
            public TotalScoreBindable(ScoreInfo score, OsuConfigManager? configManager)
            {
                configManager?.BindWith(OsuSetting.ScoreDisplayMode, scoringMode);
                scoringMode.BindValueChanged(mode => Value = score.GetDisplayScore(mode.NewValue), true);
            }
        }

        /// <summary>
        /// Provides the total score of a <see cref="ScoreInfo"/> as a formatted string. Responds to changes in the currently-selected <see cref="ScoringMode"/>.
        /// </summary>
        private class TotalScoreStringBindable : Bindable<string>
        {
            // ReSharper disable once PrivateFieldCanBeConvertedToLocalVariable (need to hold a reference)
            private readonly IBindable<long> totalScore;

            public TotalScoreStringBindable(IBindable<long> totalScore)
            {
                this.totalScore = totalScore;
                this.totalScore.BindValueChanged(v => Value = v.NewValue.ToString("N0"), true);
            }
        }

        public void Delete(Expression<Func<ScoreInfo, bool>>? filter = null, bool silent = false)
        {
            Realm.Run(r =>
            {
                var items = r.All<ScoreInfo>()
                             .Where(s => !s.DeletePending);

                if (filter != null)
                    items = items.Where(filter);

                Delete(items.ToList(), silent);
            });
        }

        public void Delete(BeatmapInfo beatmap, bool silent = false)
        {
            Realm.Run(r =>
            {
                var beatmapScores = r.Find<BeatmapInfo>(beatmap.ID)!.Scores.ToList();
                Delete(beatmapScores, silent);
            });
        }

        public Task Import(params string[] paths) => scoreImporter.Import(paths);

        public Task Import(ImportTask[] imports, ImportParameters parameters = default) => scoreImporter.Import(imports, parameters);

        public override bool IsAvailableLocally(ScoreInfo model)
            => Realm.Run(realm => realm.All<ScoreInfo>()
                                       // this basically inlines `ModelExtension.MatchesOnlineID(IScoreInfo, IScoreInfo)`,
                                       // because that method can't be used here, as realm can't translate it to its query language.
                                       .Any(s => s.OnlineID == model.OnlineID || s.LegacyOnlineID == model.LegacyOnlineID));

        public IEnumerable<string> HandledExtensions => scoreImporter.HandledExtensions;

        public Task<IEnumerable<Live<ScoreInfo>>> Import(ProgressNotification notification, ImportTask[] tasks, ImportParameters parameters = default) => scoreImporter.Import(notification, tasks);

        /// <summary>
        /// Export a replay from a given <see cref="IScoreInfo"/>.
        /// </summary>
        /// <param name="scoreInfo">The <see cref="IScoreInfo"/> to export.</param>
        /// <returns>The <see cref="Task"/>. Return <see cref="Task.CompletedTask"/> if the score cannot be found in the database.</returns>
        /// <remarks>
        /// The <see cref="IScoreInfo"/> is re-retrieved from the database to ensure all the required data
        /// for exporting a replay are present (may have missing properties if it was retrieved from online data).
        /// </remarks>
        public Task Export(ScoreInfo scoreInfo)
        {
            ScoreInfo? databasedScoreInfo = getDatabasedScoreInfo(scoreInfo);

            return databasedScoreInfo == null ? Task.CompletedTask : scoreExporter.ExportAsync(databasedScoreInfo.ToLive(Realm));
        }

        public Task<Live<ScoreInfo>?> ImportAsUpdate(ProgressNotification notification, ImportTask task, ScoreInfo original) => scoreImporter.ImportAsUpdate(notification, task, original);
        public Task<ExternalEditOperation<ScoreInfo>> BeginExternalEditing(ScoreInfo model) => scoreImporter.BeginExternalEditing(model);

        public Live<ScoreInfo>? Import(ScoreInfo item, ArchiveReader? archive = null, ImportParameters parameters = default, CancellationToken cancellationToken = default) =>
            scoreImporter.ImportModel(item, archive, parameters, cancellationToken);

        /// <summary>
        /// Populates the <see cref="ScoreInfo.MaximumStatistics"/> for a given <see cref="ScoreInfo"/>.
        /// </summary>
        /// <param name="score">The score to populate the statistics of.</param>
        public void PopulateMaximumStatistics(ScoreInfo score)
        {
            Debug.Assert(score.BeatmapInfo != null);
            LegacyScoreDecoder.PopulateMaximumStatistics(score, beatmaps().GetWorkingBeatmap(score.BeatmapInfo.Detach()));
        }

        #region Implementation of IPresentImports<ScoreInfo>

        public Action<IEnumerable<Live<ScoreInfo>>>? PresentImport
        {
            set => scoreImporter.PresentImport = value;
        }

        #endregion
    }
}
