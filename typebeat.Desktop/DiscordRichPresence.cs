// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Text;
using DiscordRPC;
using DiscordRPC.Message;
using Newtonsoft.Json;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Graphics;
using osu.Framework.Logging;
using osu.Framework.Threading;
using typebeat.Game;
using typebeat.Game.Configuration;
using typebeat.Game.Extensions;
using typebeat.Game.Online;
using typebeat.Game.Online.API;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Online.Multiplayer;
using typebeat.Game.Online.Rooms;
using typebeat.Game.Overlays;
using typebeat.Game.Rulesets;
using typebeat.Game.Users;
using LogLevel = osu.Framework.Logging.LogLevel;

namespace typebeat.Desktop
{
    internal partial class DiscordRichPresence : Component
    {
        private const string client_id = "1216669957799018608";

        /// <summary>
        /// Key of the large presence image. This names an entry in the "Rich Presence, Art Assets"
        /// list of the Discord application behind <see cref="client_id"/>, which lives on the
        /// Discord developer portal and NOT in this repository. Renaming it here without uploading
        /// art under the new name silently drops the image; the text lines still show.
        /// </summary>
        private const string large_image_key = "osu_logo_lazer";

        /// <summary>
        /// Small (corner) image key prefix, suffixed with the ruleset's online ID. Same portal
        /// dependency as <see cref="large_image_key"/>. <c>TypeBeatRuleset</c> is an
        /// <c>ILegacyRuleset</c> with <c>LegacyID = 0</c>, so the key this build asks for in
        /// practice is <c>mode_0</c>.
        /// </summary>
        private const string small_image_key_prefix = "mode_";

        /// <summary>
        /// Small image key used for any non-legacy ruleset. Same portal dependency as above.
        /// </summary>
        private const string small_image_key_custom = "mode_custom";

        private DiscordRpcClient client = null!;

        [Resolved]
        private IBindable<RulesetInfo> ruleset { get; set; } = null!;

        [Resolved]
        private IAPIProvider api { get; set; } = null!;

        [Resolved]
        private OsuGame game { get; set; } = null!;

        [Resolved]
        private LoginOverlay? login { get; set; }

        [Resolved]
        private MultiplayerClient multiplayerClient { get; set; } = null!;

        [Resolved]
        private LocalUserStatisticsProvider statisticsProvider { get; set; } = null!;

        private IBindable<DiscordRichPresenceMode> privacyMode = null!;
        private IBindable<UserStatus> userStatus = null!;
        private IBindable<UserActivity?> userActivity = null!;

        private readonly RichPresence presence = new RichPresence
        {
            Assets = new Assets { LargeImageKey = large_image_key },
            Timestamps = Timestamps.Now,
            Secrets = new Secrets
            {
                JoinSecret = null,
                SpectateSecret = null,
            },
        };

        private IBindable<APIUser>? user;

        [BackgroundDependencyLoader]
        private void load(OsuConfigManager config, SessionStatics session)
        {
            privacyMode = config.GetBindable<DiscordRichPresenceMode>(OsuSetting.DiscordRichPresence);
            userStatus = config.GetBindable<UserStatus>(OsuSetting.UserOnlineStatus);
            userActivity = session.GetBindable<UserActivity?>(Static.UserOnlineActivity);

            client = new DiscordRpcClient(client_id)
            {
                // SkipIdenticalPresence allows us to fire SetPresence at any point and leave it to the underlying implementation
                // to check whether a difference has actually occurred before sending a command to Discord (with a minor caveat that's handled in onReady).
                SkipIdenticalPresence = true
            };

            client.OnReady += onReady;
            client.OnError += (_, e) => Logger.Log($"An error occurred with Discord RPC Client: {e.Message} ({e.Code})", LoggingTarget.Network);

            try
            {
                client.RegisterUriScheme();
                client.Subscribe(EventType.Join);
                client.OnJoin += onJoin;
            }
            catch (Exception ex)
            {
                // This is known to fail in at least the following sandboxed environments:
                // - macOS (when packaged as an app bundle)
                // - flatpak (see: https://github.com/flathub/sh.ppy.osu/issues/170)
                // There is currently no better way to do this offered by Discord, so the best we can do is simply ignore it for now.
                Logger.Log($"Failed to register Discord URI scheme: {ex}");
            }

            try
            {
                // Initialize() returns immediately: it starts the library's own connection thread,
                // which retries the Discord IPC pipe on a backoff (DiscordRPC.Helper.BackoffDelay)
                // until it succeeds. So the game tolerates being started BEFORE Discord is running:
                // presence appears whenever Discord does, because OnReady fires then and schedules
                // an update. IsInitialized stays true across a dropped connection, which is why the
                // guard in schedulePresenceUpdate only means "the client got set up", not "Discord
                // is present". Nothing here needs polling or a retry loop of our own.
                client.Initialize();
            }
            catch (Exception ex)
            {
                // Not the "Discord is not running" path (that is the backoff above). Whatever it
                // is, rich presence is a garnish: log it and let the rest of the game load.
                Logger.Log($"Failed to initialise the Discord RPC client: {ex}", LoggingTarget.Network, LogLevel.Important);
            }
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            user = api.LocalUser.GetBoundCopy();

            ruleset.BindValueChanged(_ => schedulePresenceUpdate());
            userStatus.BindValueChanged(_ => schedulePresenceUpdate());
            userActivity.BindValueChanged(_ => schedulePresenceUpdate());
            privacyMode.BindValueChanged(_ => schedulePresenceUpdate());

            multiplayerClient.RoomUpdated += onRoomUpdated;
            statisticsProvider.StatisticsUpdated += onStatisticsUpdated;
        }

        private void onReady(object _, ReadyMessage __)
        {
            Logger.Log("Discord RPC Client ready.", LoggingTarget.Network, LogLevel.Debug);

            // when RPC is lost and reconnected, we have to clear presence state for updatePresence to work (see DiscordRpcClient.SkipIdenticalPresence).
            if (client.CurrentPresence != null)
                client.SetPresence(null);

            schedulePresenceUpdate();
        }

        private void onRoomUpdated() => schedulePresenceUpdate();

        private void onStatisticsUpdated(UserStatisticsUpdate _) => schedulePresenceUpdate();

        private ScheduledDelegate? presenceUpdateDelegate;

        private void schedulePresenceUpdate()
        {
            presenceUpdateDelegate?.Cancel();
            presenceUpdateDelegate = Scheduler.AddDelayed(() =>
            {
                if (!client.IsInitialized)
                    return;

                // The Discord privacy setting is the ONE kill switch. Upstream also clears presence
                // when signed out of osu! and when the online-status dropdown says "appear offline",
                // but both of those are osu!-SERVER broadcast settings: they say who may see you on
                // the website, and have nothing to say about what your own Discord client shows.
                // Tying them together is what made presence disappear here. `UserOnlineStatus` is
                // forced to Offline by LocalUserState whenever /api/v2/me omits `last_visit`, which
                // this fork's server always does (see the note there), so every logged-in player
                // landed in this branch and got no presence at all, ever. Presence now renders
                // whenever the RPC client is up and the player has not turned it off.
                if (privacyMode.Value == DiscordRichPresenceMode.Off)
                {
                    client.ClearPresence();
                    return;
                }

                // Signed out, "appear offline" and "do not disturb" all still show WHAT is being
                // played (the activity line plus the map title); they withhold only WHO is playing
                // it: no username, no rank, no multiplayer join secret.
                bool hideIdentifiableInformation = !api.IsLoggedIn
                                                   || privacyMode.Value == DiscordRichPresenceMode.Limited
                                                   || userStatus.Value == UserStatus.Offline
                                                   || userStatus.Value == UserStatus.DoNotDisturb;

                updatePresence(hideIdentifiableInformation);
                client.SetPresence(presence);
            }, 200);
        }

        private void updatePresence(bool hideIdentifiableInformation)
        {
            // NOTE: `user` is deliberately not checked here. Upstream returned early when it was
            // null, which made the whole presence (activity line, map title, ruleset image) depend
            // on an API user being bound. It is now an optional extra, read only by the large-image
            // tooltip below, so a session that never logs in still reports what it is playing.

            // user activity
            if (userActivity.Value != null)
            {
                presence.State = clampLength(userActivity.Value.GetStatus(hideIdentifiableInformation));
                presence.Details = clampLength(userActivity.Value.GetDetails(hideIdentifiableInformation) ?? string.Empty);

                if (userActivity.Value.GetBeatmapID(hideIdentifiableInformation) is int beatmapId && beatmapId > 0)
                {
                    presence.Buttons = new[]
                    {
                        new Button
                        {
                            Label = "View beatmap",
                            Url = $@"{api.Endpoints.WebsiteUrl}/beatmaps/{beatmapId}?mode={ruleset.Value.ShortName}"
                        }
                    };
                }
                else
                {
                    presence.Buttons = null;
                }
            }
            else
            {
                presence.State = "Idle";
                presence.Details = string.Empty;
            }

            // user party
            if (!hideIdentifiableInformation && multiplayerClient.Room != null && !multiplayerClient.Room.Settings.MatchType.IsMatchmakingType())
            {
                MultiplayerRoom room = multiplayerClient.Room;

                presence.Party = new Party
                {
                    Privacy = string.IsNullOrEmpty(room.Settings.Password) ? Party.PrivacySetting.Public : Party.PrivacySetting.Private,
                    ID = room.RoomID.ToString(),
                    // technically lobbies can have infinite users, but Discord needs this to be set to something.
                    // to make party display sensible, assign a powers of two above participants count (8 at minimum).
                    Max = (int)Math.Max(8, Math.Pow(2, Math.Ceiling(Math.Log2(room.Users.Count)))),
                    Size = room.Users.Count,
                };

                RoomSecret roomSecret = new RoomSecret
                {
                    RoomID = room.RoomID,
                    Password = room.Settings.Password,
                };

                if (client.HasRegisteredUriScheme)
                    presence.Secrets.JoinSecret = JsonConvert.SerializeObject(roomSecret);

                // discord cannot handle both secrets and buttons at the same time, so we need to choose something.
                // the multiplayer room seems more important.
                presence.Buttons = null;
            }
            else
            {
                presence.Party = null;
                presence.Secrets.JoinSecret = null;
            }

            // game images:
            // large image tooltip. This is the only place the local player's identity reaches
            // Discord, so it is the one part a hidden-identity session drops (upstream gated it on
            // Limited alone, which was safe there only because the caller had already refused to
            // build a presence for anyone signed out or appearing offline). `user` is assigned in
            // LoadComplete, and a signed-out session holds a GuestUser, so both are covered by the
            // hideIdentifiableInformation arm rather than by an early return.
            if (hideIdentifiableInformation || user?.Value == null)
                presence.Assets.LargeImageText = string.Empty;
            else
            {
                var statistics = statisticsProvider.GetStatisticsFor(ruleset.Value);
                presence.Assets.LargeImageText = $"{user.Value.Username}" + (statistics?.GlobalRank > 0 ? $" (rank #{statistics.GlobalRank:N0})" : string.Empty);
            }

            // small image
            presence.Assets.SmallImageKey = ruleset.Value.IsLegacyRuleset() ? $"{small_image_key_prefix}{ruleset.Value.OnlineID}" : small_image_key_custom;
            presence.Assets.SmallImageText = ruleset.Value.Name;
        }

        private void onJoin(object sender, JoinMessage args) => Scheduler.AddOnce(() =>
        {
            game.Window?.Raise();

            if (!api.IsLoggedIn)
            {
                login?.Show();
                return;
            }

            Logger.Log($"Received room secret from Discord RPC Client: \"{args.Secret}\"", LoggingTarget.Network, LogLevel.Debug);

            // Stable and lazer share the same Discord client ID, meaning they can accept join requests from each other.
            // Since they aren't compatible in multi, see if stable's format is being used and log to avoid confusion.
            if (args.Secret[0] != '{' || !tryParseRoomSecret(args.Secret, out long roomId, out string? password))
            {
                Logger.Log("Could not join multiplayer room, invitation is invalid or incompatible.", LoggingTarget.Network, LogLevel.Important);
                return;
            }

            // Multiplayer is not supported in this build; Discord room-join requests cannot be actioned.
            Logger.Log($"Ignoring Discord multiplayer room-join request (room ID: {roomId}); multiplayer is not supported.", LoggingTarget.Network, LogLevel.Important);
        });

        private static readonly int ellipsis_length = Encoding.UTF8.GetByteCount(new[] { '…' });

        private static string clampLength(string str)
        {
            // Empty strings are fine to discord even though single-character strings are not. Make it make sense.
            if (string.IsNullOrEmpty(str))
                return str;

            // As above, discord decides that *non-empty* strings shorter than 2 characters cannot possibly be valid input, because... reasons?
            // And yes, that is two *characters*, or *codepoints*, not *bytes* as further down below (as determined by empirical testing).
            // Also, spaces don't count. Because reasons, clearly.
            // That all seems very questionable, and isn't even documented anywhere. So to *make it* accept such valid input,
            // just tack on enough of U+200B ZERO WIDTH SPACEs at the end. After making sure to trim whitespace.
            string trimmed = str.Trim();
            if (trimmed.Length < 2)
                return trimmed.PadRight(2, '\u200B');

            if (Encoding.UTF8.GetByteCount(str) <= 128)
                return str;

            ReadOnlyMemory<char> strMem = str.AsMemory();

            do
            {
                strMem = strMem[..^1];
            } while (Encoding.UTF8.GetByteCount(strMem.Span) + ellipsis_length > 128);

            return string.Create(strMem.Length + 1, strMem, (span, mem) =>
            {
                mem.Span.CopyTo(span);
                span[^1] = '…';
            });
        }

        private static bool tryParseRoomSecret(string secretJson, out long roomId, out string? password)
        {
            roomId = 0;
            password = null;

            RoomSecret? roomSecret;

            try
            {
                roomSecret = JsonConvert.DeserializeObject<RoomSecret>(secretJson);
            }
            catch
            {
                return false;
            }

            if (roomSecret == null) return false;

            roomId = roomSecret.RoomID;
            password = roomSecret.Password;

            return true;
        }

        protected override void Dispose(bool isDisposing)
        {
            if (multiplayerClient.IsNotNull())
                multiplayerClient.RoomUpdated -= onRoomUpdated;

            if (statisticsProvider.IsNotNull())
                statisticsProvider.StatisticsUpdated -= onStatisticsUpdated;

            client.Dispose();
            base.Dispose(isDisposing);
        }

        private class RoomSecret
        {
            [JsonProperty(@"roomId", Required = Required.Always)]
            public long RoomID { get; set; }

            [JsonProperty(@"password", Required = Required.AllowNull)]
            public string? Password { get; set; }
        }
    }
}
