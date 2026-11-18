// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using osu.Framework;
using osu.Framework.Bindables;
using osu.Framework.Configuration;
using osu.Framework.Configuration.Tracking;
using osu.Framework.Extensions;
using osu.Framework.Extensions.LocalisationExtensions;
using osu.Framework.Localisation;
using osu.Framework.Platform;
using typebeat.Game.Beatmaps.Drawables.Cards;
using typebeat.Game.Input;
using typebeat.Game.Input.Bindings;
using typebeat.Game.Localisation;
using typebeat.Game.Online.Leaderboards;
using typebeat.Game.Overlays.Mods.Input;
using typebeat.Game.Screens.Select;
using typebeat.Game.Screens.Select.Filter;
using typebeat.Game.Skinning;

namespace typebeat.Game.Configuration
{
    public class OsuConfigManager : IniConfigManager<OsuSetting>, IGameplaySettings, INamedSettingSource
    {
        public OsuConfigManager(Storage storage)
            : base(storage)
        {
        }

        protected override void InitialiseDefaults()
        {
            // UI/selection defaults
            SetDefault(OsuSetting.Ruleset, string.Empty);
            SetDefault(OsuSetting.Skin, SkinInfo.ARGON_SKIN.ToString());

            SetDefault(OsuSetting.BeatmapDetailTab, BeatmapDetailTab.Local);
            SetDefault(OsuSetting.BeatmapLeaderboardSortMode, LeaderboardSortMode.Score);
            SetDefault(OsuSetting.BeatmapDetailModsFilter, false);

            SetDefault(OsuSetting.SeparateAllDifficulties, false);
            SetDefault(OsuSetting.DisplayStarsMinimum, 0.0, 0, 10, 0.1);
            SetDefault(OsuSetting.DisplayStarsMaximum, 10.1, 0, 10.1, 0.1);

            SetDefault(OsuSetting.SongSelectGroupMode, GroupMode.None);
            SetDefault(OsuSetting.SongSelectSortingMode, SortMode.Title);
            SetDefault(OsuSetting.SongSelectCollectionFilter, string.Empty);

            SetDefault(OsuSetting.RandomSelectAlgorithm, RandomSelectAlgorithm.RandomPermutation);
            SetDefault(OsuSetting.ModSelectHotkeyStyle, ModSelectHotkeyStyle.Sequential);

            SetDefault(OsuSetting.ModSelectTextSearchStartsActive, true);

            SetDefault(OsuSetting.BeatmapListingCardSize, BeatmapCardSize.Normal);

            SetDefault(OsuSetting.ProfileCoverExpanded, true);

            SetDefault(OsuSetting.ToolbarClockDisplayMode, ToolbarClockDisplayMode.Full);

            SetDefault(OsuSetting.SongSelectBackgroundBlur, false);

            // Online settings
            SetDefault(OsuSetting.Username, string.Empty);
            SetDefault(OsuSetting.Token, string.Empty);
            SetDefault(OsuSetting.ClientInstance, string.Empty);

            SetDefault(OsuSetting.AutomaticallyDownloadMissingBeatmaps, true);

            SetDefault(OsuSetting.SavePassword, true).ValueChanged += enabled =>
            {
                if (enabled.NewValue)
                    SetValue(OsuSetting.SaveUsername, true);
                else
                    GetBindable<string>(OsuSetting.Token).SetDefault();
            };

            SetDefault(OsuSetting.SaveUsername, true).ValueChanged += enabled =>
            {
                if (!enabled.NewValue)
                {
                    GetBindable<string>(OsuSetting.Username).SetDefault();
                    SetValue(OsuSetting.SavePassword, false);
                }
            };

            SetDefault(OsuSetting.ExternalLinkWarning, true);
            SetDefault(OsuSetting.PreferNoVideo, false);

            SetDefault(OsuSetting.ShowOnlineExplicitContent, false);

            // Audio
            SetDefault(OsuSetting.VolumeInactive, 0.25, 0, 1, 0.01);

            SetDefault(OsuSetting.MenuMusic, true);
            SetDefault(OsuSetting.MenuTips, true);

            SetDefault(OsuSetting.AudioOffset, 0, -500.0, 500.0, 1);

            SetDefault(OsuSetting.AutomaticallyAdjustBeatmapOffset, false);

            // Input
            SetDefault(OsuSetting.MenuCursorSize, 1.0f, 0.5f, 2f, 0.01f);

            SetDefault(OsuSetting.MouseDisableButtons, false);
            SetDefault(OsuSetting.MouseDisableWheel, false);
            SetDefault(OsuSetting.ConfineMouseMode, OsuConfineMouseMode.DuringGameplay);

            // Graphics
            SetDefault(OsuSetting.ShowFpsDisplay, false);

            SetDefault(OsuSetting.ShowStoryboard, true);
            SetDefault(OsuSetting.BeatmapSkins, true);
            SetDefault(OsuSetting.BeatmapColours, true);
            SetDefault(OsuSetting.BeatmapHitsounds, true);

            SetDefault(OsuSetting.CursorRotation, true);

            SetDefault(OsuSetting.MenuParallaxScale, 1.0f, 0.0f, 2.0f, 0.1f);

            // Gameplay
            // type!beat ships with a 40% gameplay background dim by default so the beatmap
            // background image/video stays partly veiled. (Only affects fresh installs: an existing
            // install persists DimLevel to game.ini and reloads its stored value over this default,
            // so changing it never rewrites a player's own setting.)
            SetDefault(OsuSetting.DimLevel, 0.4, 0, 1, 0.01);
            SetDefault(OsuSetting.BlurLevel, 0, 0, 1, 0.01);
            SetDefault(OsuSetting.LightenDuringBreaks, true);

            SetDefault(OsuSetting.StarFountains, true);

            SetDefault(OsuSetting.HUDVisibilityMode, HUDVisibilityMode.Always);
            SetDefault(OsuSetting.ShowHealthDisplayWhenCantFail, true);
            SetDefault(OsuSetting.ReplaySettingsOverlay, true);
            SetDefault(OsuSetting.ReplayPlaybackControlsExpanded, true);
            SetDefault(OsuSetting.GameplayLeaderboard, true);

            SetDefault(OsuSetting.GameplayDisableWinKey, true);

            // Applied before the window exists by Input.RawKeyboardFallback, which reads game.ini itself.
            SetDefault(OsuSetting.DisableRawKeyboard, false);

            SetDefault(OsuSetting.Version, string.Empty);

            SetDefault(OsuSetting.ShowFirstRunSetup, true);
            SetDefault(OsuSetting.ShowMobileDisclaimer, RuntimeInfo.IsMobile);

            SetDefault(OsuSetting.ScreenshotFormat, ScreenshotFormat.Jpg);
            SetDefault(OsuSetting.ScreenshotCaptureMenuCursor, false);

            SetDefault(OsuSetting.Scaling, ScalingMode.Off);
            SetDefault(OsuSetting.SafeAreaConsiderations, true);
            SetDefault(OsuSetting.ScalingBackgroundDim, 0.9f, 0.5f, 1f, 0.01f);

            SetDefault(OsuSetting.ScalingSizeX, 0.8f, 0.2f, 1f, 0.01f);
            SetDefault(OsuSetting.ScalingSizeY, 0.8f, 0.2f, 1f, 0.01f);

            SetDefault(OsuSetting.ScalingPositionX, 0.5f, 0f, 1f, 0.01f);
            SetDefault(OsuSetting.ScalingPositionY, 0.5f, 0f, 1f, 0.01f);

            if (RuntimeInfo.IsMobile)
                SetDefault(OsuSetting.UIScale, 1f, 0.8f, 1.1f, 0.01f);
            else
                SetDefault(OsuSetting.UIScale, 1f, 0.8f, 1.6f, 0.01f);

            SetDefault(OsuSetting.UIHoldActivationDelay, 200.0, 0.0, 500.0, 50.0);

            SetDefault(OsuSetting.DiscordRichPresence, DiscordRichPresenceMode.Full);

            SetDefault(OsuSetting.EditorDim, 0.25f, 0f, 1f, 0.25f);
            SetDefault(OsuSetting.EditorWaveformOpacity, 0.25f, 0f, 1f, 0.25f);

            SetDefault(OsuSetting.HideCountryFlags, false);

            SetDefault(OsuSetting.ComboColourNormalisationAmount, 0.2f, 0f, 1f, 0.01f);

            SetDefault(OsuSetting.EditorTimelineShowTimingChanges, true);

            SetDefault(OsuSetting.AlwaysShowHoldForMenuButton, false);
            SetDefault(OsuSetting.AlwaysRequireHoldingForPause, false);
            SetDefault(OsuSetting.EditorShowStoryboard, true);

            SetDefault(OsuSetting.EditorSubmissionLoadInBrowserAfterSubmission, true);
        }

        /// <summary>
        /// Puts every game setting back on its default (Maintenance, "Reset all settings to defaults")
        /// and saves, except the ones in <see cref="KeepOnReset"/>. Key bindings, the framework's own
        /// settings (display, renderer, audio device, volume) and ruleset settings live elsewhere and
        /// are not touched here.
        /// </summary>
        public int ResetToDefaults()
        {
            int reset = ConfigDefaults.Reset(ConfigStore, KeepOnReset);
            Save();
            return reset;
        }

        /// <summary>
        /// Settings a reset leaves alone: they are not preferences the player tunes but identity or
        /// bookkeeping, and resetting them would sign the player out, re-run first-run setup or replay the
        /// changelog.
        /// </summary>
        public static bool KeepOnReset(OsuSetting lookup)
        {
            switch (lookup)
            {
                // Login.
                case OsuSetting.Token:
                case OsuSetting.Username:
                case OsuSetting.SaveUsername:
                case OsuSetting.SavePassword:
                case OsuSetting.ClientInstance:
                // Bookkeeping and one-time flows.
                case OsuSetting.Version:
                case OsuSetting.ShowFirstRunSetup:
                case OsuSetting.ShowMobileDisclaimer:
                case OsuSetting.Ruleset:
                    return true;
            }

            return false;
        }

        protected override bool CheckLookupContainsPrivateInformation(OsuSetting lookup)
        {
            switch (lookup)
            {
                case OsuSetting.Token:
                case OsuSetting.ClientInstance:
                    return true;
            }

            return false;
        }

        public override TrackedSettings CreateTrackedSettings()
        {
            return new TrackedSettings
            {
                new TrackedSetting<bool>(OsuSetting.ShowFpsDisplay, state => new SettingDescription(
                    rawValue: state,
                    name: GlobalActionKeyBindingStrings.ToggleFPSCounter,
                    value: state ? CommonStrings.Enabled.ToLower() : CommonStrings.Disabled.ToLower(),
                    shortcut: LookupKeyBindings(GlobalAction.ToggleFPSDisplay))
                ),
                new TrackedSetting<bool>(OsuSetting.MouseDisableButtons, disabledState => new SettingDescription(
                    rawValue: !disabledState,
                    name: GlobalActionKeyBindingStrings.ToggleGameplayMouseButtons,
                    value: disabledState ? CommonStrings.Disabled.ToLower() : CommonStrings.Enabled.ToLower(),
                    shortcut: LookupKeyBindings(GlobalAction.ToggleGameplayMouseButtons))
                ),
                new TrackedSetting<bool>(OsuSetting.GameplayLeaderboard, state => new SettingDescription(
                    rawValue: state,
                    name: GlobalActionKeyBindingStrings.ToggleInGameLeaderboard,
                    value: state ? CommonStrings.Enabled.ToLower() : CommonStrings.Disabled.ToLower(),
                    shortcut: LookupKeyBindings(GlobalAction.ToggleInGameLeaderboard))
                ),
                new TrackedSetting<HUDVisibilityMode>(OsuSetting.HUDVisibilityMode, visibilityMode => new SettingDescription(
                    rawValue: visibilityMode,
                    name: GameplaySettingsStrings.HUDVisibilityMode,
                    value: visibilityMode.GetLocalisableDescription(),
                    shortcut: new TranslatableString(@"_", @"{0}: {1} {2}: {3}",
                        GlobalActionKeyBindingStrings.ToggleInGameInterface,
                        LookupKeyBindings(GlobalAction.ToggleInGameInterface),
                        GlobalActionKeyBindingStrings.HoldForHUD,
                        LookupKeyBindings(GlobalAction.HoldForHUD)))
                ),
                new TrackedSetting<ScalingMode>(OsuSetting.Scaling, scalingMode => new SettingDescription(
                        rawValue: scalingMode,
                        name: GraphicsSettingsStrings.ScreenScaling,
                        value: scalingMode.GetLocalisableDescription()
                    )
                ),
                new TrackedSetting<string>(OsuSetting.Skin, skin =>
                {
                    string skinName = string.Empty;

                    if (Guid.TryParse(skin, out var id))
                        skinName = LookupSkinName(id);

                    return new SettingDescription(
                        rawValue: skinName,
                        name: SkinSettingsStrings.SkinSectionHeader,
                        value: skinName,
                        shortcut: new TranslatableString(@"_", @"{0}: {1}",
                            GlobalActionKeyBindingStrings.RandomSkin,
                            LookupKeyBindings(GlobalAction.RandomSkin))
                    );
                }),
                new TrackedSetting<float>(OsuSetting.UIScale, scale => new SettingDescription(
                        rawValue: scale,
                        name: GraphicsSettingsStrings.UIScaling,
                        value: $"{scale:N2}x"
                        // TODO: implement lookup for framework platform key bindings
                    )
                ),
            };
        }

        public Func<Guid, string> LookupSkinName { private get; set; } = _ => @"unknown";
        public Func<GlobalAction, LocalisableString> LookupKeyBindings { private get; set; } = _ => @"unknown";

        IBindable<float> IGameplaySettings.ComboColourNormalisationAmount => GetOriginalBindable<float>(OsuSetting.ComboColourNormalisationAmount);

        public IEnumerable<string> SettingNames => NamedSettings.NamesIn(ConfigStore);

        public IBindable? FindSetting(string name) => NamedSettings.Find(ConfigStore, name);
    }

    // IMPORTANT: These are used in user configuration files.
    // The naming of these keys should not be changed once they are deployed in a release, unless migration logic is also added.
    public enum OsuSetting
    {
        Ruleset,
        Token,
        MenuCursorSize,
        DimLevel,
        BlurLevel,
        EditorDim,
        LightenDuringBreaks,
        ShowStoryboard,
        GameplayLeaderboard,
        HUDVisibilityMode,

        ShowHealthDisplayWhenCantFail,

        /// <summary>
        /// Disables mouse buttons clicks during gameplay.
        /// </summary>
        MouseDisableButtons,
        MouseDisableWheel,
        ConfineMouseMode,

        /// <summary>
        /// Globally applied audio offset.
        /// This is added to the audio track's current time. Higher values will cause gameplay to occur earlier, relative to the audio track.
        /// </summary>
        AudioOffset,

        VolumeInactive,
        MenuMusic,
        MenuTips,
        CursorRotation,

        MenuParallaxScale,

        // NOTE (backlog 468): there used to be a Prefer24HourTime member here, defaulted from the
        // system culture's short time pattern. Every clock and timestamp now always shows 24-hour
        // time, so it was retired. A stored "Prefer24HourTime = ..." line in game.ini needs no
        // migration: the ini loader skips a key it cannot parse into this enum, and the next save
        // writes the file without it. Do not reuse the name for something else.

        BeatmapDetailTab,
        BeatmapLeaderboardSortMode,
        BeatmapDetailModsFilter,
        Username,
        SavePassword,
        SaveUsername,
        DisplayStarsMinimum,
        DisplayStarsMaximum,
        SongSelectGroupMode,
        SongSelectSortingMode,
        SongSelectCollectionFilter,
        RandomSelectAlgorithm,
        ModSelectHotkeyStyle,
        ShowFpsDisplay,
        BeatmapListingCardSize,
        ToolbarClockDisplayMode,
        SongSelectBackgroundBlur,
        Version,
        ShowFirstRunSetup,
        Skin,
        ScreenshotFormat,
        ScreenshotCaptureMenuCursor,
        BeatmapSkins,
        BeatmapColours,
        BeatmapHitsounds,
        ExternalLinkWarning,
        PreferNoVideo,
        Scaling,
        ScalingPositionX,
        ScalingPositionY,
        ScalingSizeX,
        ScalingSizeY,
        ScalingBackgroundDim,
        UIScale,
        // NOTE (2026-10-25): IntroSequence was retired; the intro is always Triangles (Screens.Loader). A
        // stored "IntroSequence = ..." line in game.ini needs no migration (the ini loader skips a key it
        // cannot parse into this enum). Do not reuse the name.
        UIHoldActivationDelay,
        StarFountains,
        GameplayDisableWinKey,
        EditorWaveformOpacity,
        DiscordRichPresence,

        ShowOnlineExplicitContent,
        SafeAreaConsiderations,
        ComboColourNormalisationAmount,
        ProfileCoverExpanded,
        ReplaySettingsOverlay,
        ReplayPlaybackControlsExpanded,
        AutomaticallyDownloadMissingBeatmaps,
        ModSelectTextSearchStartsActive,

        HideCountryFlags,
        EditorTimelineShowTimingChanges,
        AlwaysShowHoldForMenuButton,
        AlwaysRequireHoldingForPause,
        ShowMobileDisclaimer,
        EditorShowStoryboard,
        EditorSubmissionLoadInBrowserAfterSubmission,

        AutomaticallyAdjustBeatmapOffset,

        SeparateAllDifficulties,

        // NOTE (backlog 383): there used to be a KeyboardLayout member here, the layout the player's
        // keycaps follow. Gameplay typing reads the OS's committed text since 383, and the shortcuts
        // it still steered follow the OS's own keyboard map now (Input.OsKeyboardLayout), so it was
        // retired rather than renamed. A stored "KeyboardLayout = ..." line in game.ini needs no
        // migration: the ini loader skips a key it cannot parse into this enum, and the next save
        // writes the file without it. Do not reuse the name for something else.

        /// <summary>
        /// Windows only: turns SDL's raw keyboard input off, a fallback should it drop or double keys in
        /// gameplay typing. It has to be applied before the window is created, so it is read straight from
        /// game.ini at startup by <see cref="Input.RawKeyboardFallback"/> (by this member's name, like
        /// every ini key), and a change takes effect on the next start.
        /// </summary>
        DisableRawKeyboard,

        /// <summary>
        /// A random id this install creates once and sends with API requests (<see cref="Online.API.APIAccess.ClientInstance"/>).
        /// </summary>
        ClientInstance,

        // NOTE (backlog 483): the legacy osu! settings below were retired after an audit found they did
        // nothing in type!beat or misled the player. Their readers now use what the setting defaulted
        // to (or what a non-supporter always got), so nobody on the defaults sees a change; anyone who
        // had moved one sees the default again. A stored line for any of them in game.ini needs no
        // migration: the ini loader skips a key it cannot parse into this enum, and the next save writes
        // the file without it (ConfigDefaultsTest pins that). Do not reuse any of these names:
        //
        //  Had a settings control: ScoreDisplayMode (scores always show Standardised),
        //  FadePlayfieldWhenHealthLow (the fail flash always plays), MenuVoice (no intro or exit voice,
        //  ever), MenuBackgroundSource (the default menu background rotation), ShowConvertedBeatmaps,
        //  TouchDisableGameplayTaps, EditorSubmissionNotifyOnDiscussionReplies, UserOnlineStatus
        //  (always Online).
        //
        //  Read by nothing reachable, or only by code that was never instantiated: HitLighting,
        //  AlwaysPlayFirstComboBreak, FloatingComments, PositionalHitsoundsLevel (balance stays
        //  centred), MenuParallax, SeasonalBackgroundMode, KeyOverlay (the key counter shows in
        //  replays only), ChatDisplayHeight, NotifyOnUsernameMentioned, NotifyOnPrivateMessage,
        //  NotifyOnFriendPresenceChange, DashboardSortMode, DashboardDisplayStyle,
        //  MultiplayerRoomFilter, MultiplayerShowInProgressFilter, MultiplayerShowFullFilter,
        //  BeatmapListingFeaturedArtistFilter, EditorShowHitMarkers, EditorAutoSeekOnPlacement,
        //  EditorLimitedDistanceSnap, EditorShowSpeedChanges, EditorScaleOrigin, EditorRotationOrigin,
        //  EditorAdjustExistingObjectsOnTimingChanges, EditorTimelineShowTicks,
        //  EditorTimelineShowBreaks, EditorContractSidebars, LastProcessedMetadataId,
        //  LastOnlineTagsPopulation, ReleaseStream (always Lazer), PMFriendsOnly, WasSupporter.
    }
}
