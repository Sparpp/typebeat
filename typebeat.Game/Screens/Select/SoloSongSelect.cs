// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Audio.Sample;
using osu.Framework.Extensions.LocalisationExtensions;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Screens;
using typebeat.Game.Beatmaps;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Localisation;
using typebeat.Game.Online.API;
using typebeat.Game.Overlays;
using typebeat.Game.Overlays.Notifications;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Screens.Edit;
using typebeat.Game.Screens.Play;
using typebeat.Game.Users;
using typebeat.Game.Utils;
using WebCommonStrings = typebeat.Game.Resources.Localisation.Web.CommonStrings;

namespace typebeat.Game.Screens.Select
{
    public partial class SoloSongSelect : SongSelect
    {
        protected override UserActivity InitialActivity => new UserActivity.ChoosingBeatmap();

        private PlayerLoader? playerLoader;
        private IReadOnlyList<Mod>? modsAtGameplayStart;

        [Resolved]
        private BeatmapSetOverlay? beatmapOverlay { get; set; }

        [Resolved]
        private BeatmapManager beatmaps { get; set; } = null!;

        [Resolved]
        private IAPIProvider api { get; set; } = null!;

        [Resolved]
        private INotificationOverlay? notifications { get; set; }

        [Resolved]
        private IDialogOverlay? dialogOverlay { get; set; }

        [Resolved]
        private OsuGame? game { get; set; }

        private Sample? sampleConfirmSelection { get; set; }

        [BackgroundDependencyLoader]
        private void load(AudioManager audio)
        {
            sampleConfirmSelection = audio.Samples.Get(@"SongSelect/confirm-selection");

            AddInternal(new SongSelectTouchInputDetector());
        }

        public override IEnumerable<OsuMenuItem> GetForwardActions(BeatmapInfo beatmap)
        {
            yield return new OsuMenuItem(ButtonSystemStrings.Play.ToSentence(), MenuItemType.Highlighted, () => SelectAndRun(beatmap, OnStart)) { Icon = FontAwesome.Solid.Check };

            yield return new OsuMenuItem(ButtonSystemStrings.Edit.ToSentence(), MenuItemType.Standard, () => SelectAndRun(beatmap, openEditor)) { Icon = FontAwesome.Solid.PencilAlt };

            yield return new OsuMenuItemSpacer();

            if (beatmap.OnlineID > 0)
            {
                yield return new OsuMenuItem(CommonStrings.Details, MenuItemType.Standard, () => beatmapOverlay?.FetchAndShowBeatmap(beatmap.OnlineID));

                if (beatmap.GetOnlineURL(api, Ruleset.Value) is string url)
                    yield return new OsuMenuItem(CommonStrings.CopyLink, MenuItemType.Standard, () => game?.CopyToClipboard(url));

                yield return new OsuMenuItemSpacer();
            }

            foreach (var i in CreateCollectionMenuActions(beatmap))
                yield return i;

            if (beatmap.LastPlayed == null)
                yield return new OsuMenuItem(SongSelectStrings.MarkAsPlayed, MenuItemType.Standard, () => beatmaps.MarkPlayed(beatmap)) { Icon = FontAwesome.Solid.TimesCircle };
            else
                yield return new OsuMenuItem(SongSelectStrings.RemoveFromPlayed, MenuItemType.Standard, () => beatmaps.MarkNotPlayed(beatmap)) { Icon = FontAwesome.Solid.TimesCircle };

            yield return new OsuMenuItem(SongSelectStrings.ClearAllLocalScores, MenuItemType.Standard, () => dialogOverlay?.Push(new BeatmapClearScoresDialog(beatmap)))
            {
                Icon = FontAwesome.Solid.Eraser
            };

            if (beatmaps.CanHide(beatmap))
                yield return new OsuMenuItem(WebCommonStrings.ButtonsHide.ToSentence(), MenuItemType.Destructive, () => beatmaps.Hide(beatmap));
        }

        protected override void OnStart()
        {
            if (playerLoader != null) return;

            // A blank map (an audio-only import, no lyric lines yet) has no typeable cells at all:
            // starting it would push a Player that immediately bails with the generic "contains no
            // hit objects" log and leaves the user on an empty screen. Say what is actually wrong
            // and where to fix it instead. See BlankBeatmap for why this is never allowed to play.
            if (BlankBeatmap.IsBlank(Beatmap.Value))
            {
                notifications?.Post(new SimpleNotification { Text = SongSelectStrings.BlankBeatmapCannotBePlayed });
                return;
            }

            modsAtGameplayStart = Mods.Value.Select(m => m.DeepClone()).ToArray();

            // Ctrl+Enter should start map with autoplay enabled.
            if (GetContainingInputManager()?.CurrentState?.Keyboard.ControlPressed == true)
            {
                var autoInstance = getAutoplayMod();

                if (autoInstance == null)
                {
                    notifications?.Post(new SimpleNotification
                    {
                        Text = NotificationsStrings.NoAutoplayMod
                    });
                    return;
                }

                var mods = Mods.Value.Append(autoInstance).ToArray();

                if (!ModUtils.CheckCompatibleSet(mods, out var invalid))
                    mods = mods.Except(invalid).Append(autoInstance).ToArray();

                Mods.Value = mods;
            }

            sampleConfirmSelection?.Play();

            this.Push(playerLoader = new PlayerLoader(createPlayer));

            Player createPlayer()
            {
                Player player;

                var replayGeneratingMod = Mods.Value.OfType<ICreateReplayData>().FirstOrDefault();

                if (replayGeneratingMod != null)
                {
                    player = new ReplayPlayer(replayGeneratingMod.CreateScoreFromReplayData);
                }
                else
                {
                    player = new SoloPlayer();
                }

                return player;
            }
        }

        public override void OnResuming(ScreenTransitionEvent e)
        {
            base.OnResuming(e);
            revertMods();
        }

        public override bool OnExiting(ScreenExitEvent e)
        {
            if (base.OnExiting(e))
                return true;

            revertMods();
            return false;
        }

        private void openEditor() => this.Push(new EditorLoader());

        private ModAutoplay? getAutoplayMod() => Ruleset.Value.CreateInstance().GetAutoplayMod();

        private void revertMods()
        {
            if (playerLoader == null) return;

            Mods.Value = modsAtGameplayStart;
            playerLoader = null;
        }

        private partial class PlayerLoader : Play.PlayerLoader
        {
            public override bool ShowFooter => !QuickRestart;

            public PlayerLoader(Func<Player> createPlayer)
                : base(createPlayer)
            {
            }
        }
    }
}
