// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using JetBrains.Annotations;
using osu.Framework.Allocation;
using osu.Framework.Platform;
using osu.Framework.Audio;
using osu.Framework.Audio.Sample;
using osu.Framework.Audio.Track;
using osu.Framework.Bindables;
using osu.Framework.Extensions;
using osu.Framework.Graphics;
using osu.Framework.Logging;
using osu.Framework.Screens;
using osu.Framework.Utils;
using typebeat.Game.Audio;
using typebeat.Game.Beatmaps;
using typebeat.Game.Configuration;
using typebeat.Game.Database;
using typebeat.Game.Extensions;
using typebeat.Game.Localisation;
using typebeat.Game.Online.API;
using typebeat.Game.Overlays;
using typebeat.Game.Overlays.Notifications;
using typebeat.Game.Overlays.Volume;
using typebeat.Game.Rulesets;
using typebeat.Game.Screens.Backgrounds;
using typebeat.Game.Skinning;
using osuTK;
using osuTK.Graphics;
using Realms;

namespace typebeat.Game.Screens.Menu
{
    public abstract partial class IntroScreen : StartupScreen
    {
        /// <summary>
        /// Whether we have loaded the menu previously.
        /// </summary>
        public bool DidLoadMenu { get; private set; }

        protected IBindable<bool> MenuVoice { get; private set; }

        protected IBindable<bool> MenuMusic { get; private set; }

        private WorkingBeatmap initialBeatmap;

        /// <summary>
        /// The beatdrop timestamp (ms) of <see cref="initialBeatmap"/>, when one was selected.
        /// </summary>
        private double? beatdropTime;

        /// <summary>
        /// Whether a beatmap from the intro pool was selected to soundtrack the intro.
        /// When false, the intro runs silent.
        /// </summary>
        protected bool HasBeatdropTrack => beatdropTime != null;

        protected ITrack Track { get; private set; }

        private const int exit_delay = 3000;

        private SkinnableSound skinnableSeeya;
        private ISample seeya;

        protected virtual string SeeyaSampleName => "Intro/seeya";

        protected override bool PlayExitSound => false;

        private LeasedBindable<WorkingBeatmap> beatmap;

        private OsuScreen nextScreen;

        [Resolved]
        private AudioManager audio { get; set; }

        [Resolved]
        private MusicController musicController { get; set; }

        [CanBeNull]
        private readonly Func<OsuScreen> createNextScreen;

        protected override BackgroundScreen CreateBackground() => new BackgroundScreenDefault
        {
            Colour = Color4.Black
        };

        public override bool? AllowGlobalTrackControl => false;

        protected IntroScreen([CanBeNull] Func<MainMenu> createNextScreen = null)
        {
            this.createNextScreen = createNextScreen;
        }

        /// <summary>
        /// Set (before the screen is loaded) to run this intro as an editor beatdrop demo rather than as the
        /// game's startup intro: it is soundtracked by the requested map at the requested timestamp instead
        /// of by a pick from the intro pool, and at the menu reveal it exits back into the menu it was pushed
        /// over instead of pushing a new one. Null for a real startup.
        /// </summary>
        [CanBeNull]
        public IntroBeatdropDemo Demo { get; init; }

        /// <summary>
        /// Creates the intro screen to run a beatdrop demo on. Deliberately not the user's configured intro
        /// sequence: <see cref="IntroTriangles"/> is the only one that times the track so the beatdrop lands on
        /// the menu reveal (<see cref="StartBeatdropTrack"/>), the others just start the chosen map from its
        /// preview point, so it is the only one that has a beatdrop to demo at all.
        /// </summary>
        public static IntroScreen CreateDemo(IntroBeatdropDemo demo) => new IntroTriangles { Demo = demo };

        [Resolved]
        private BeatmapManager beatmaps { get; set; }

        [Resolved]
        private Storage storage { get; set; }

        [BackgroundDependencyLoader]
        private void load(OsuConfigManager config, RealmAccess realm, IAPIProvider api)
        {
            // prevent user from changing beatmap while the intro is still running.
            beatmap = Beatmap.BeginLease(false);

            MenuVoice = config.GetBindable<bool>(OsuSetting.MenuVoice);
            MenuMusic = config.GetBindable<bool>(OsuSetting.MenuMusic);

            if (api.LocalUser.Value.IsSupporter)
                AddInternal(skinnableSeeya = new SkinnableSound(new SampleInfo(SeeyaSampleName)));
            else
                seeya = audio.Samples.Get(SeeyaSampleName);

            // A beatdrop demo names its own soundtrack: the map being edited, at the timestamp being
            // edited. Intro pool membership (BeatmapUserSettings.IntroPoolInclusion, the song select "Use on
            // game intro" toggle) is neither read nor written here, because the point is to preview the map
            // in front of you whether or not it has been opted in. The anti-repeat history is likewise left
            // alone: a demo is not a real intro appearance and must not steer future ones. The MenuMusic
            // setting is ignored too, since a silent beatdrop demo would have nothing to demo.
            if (Demo != null)
            {
                var demoBeatmap = beatmaps.GetWorkingBeatmap(Demo.BeatmapInfo);

                // Only reachable if the map was destroyed between requesting the demo and arriving here
                // (discarding a brand new beatmap deletes it). Running silent is better than pretending.
                if (demoBeatmap is DummyWorkingBeatmap)
                    Logger.Log("Beatdrop demo target is no longer available; the intro will run silent.");
                else
                {
                    initialBeatmap = demoBeatmap;
                    beatdropTime = Demo.DropTime;
                    Logger.Log($"Intro beatdrop demo: {initialBeatmap.Metadata.Artist} - {initialBeatmap.Metadata.Title} (drop at {beatdropTime:0}ms)");
                }
            }
            // The intro is soundtracked by a random user beatmap from the intro pool (by default the
            // maps that declare an intro beatdrop, see IntroBeatdropPool): subclasses start the track
            // so the drop lands exactly on the menu reveal. No candidates (or menu music disabled)
            // -> silent intro.
            else if (MenuMusic.Value)
            {
                var recentlyPlayed = readBeatdropHistory();
                Guid selectedSetId = Guid.Empty;

                realm.Run(r =>
                {
                    var usableBeatmapSets = r.All<BeatmapSetInfo>().Where(s => !s.DeletePending && !s.Protected).AsRealmCollection();

                    // Human-feeling shuffle (à la Spotify): rather than a uniform random pick — which
                    // happily repeats the same map two launches running — we bias away from what was
                    // recently played. Unplayed maps come first in a fresh random order; recently-played
                    // ones are pushed to the back, oldest-first, so the most recent map is dead last and
                    // only resurfaces when nothing else is in the pool. Taking the first pool member in
                    // this order therefore avoids repeats whenever the library allows it, and non-members
                    // only cost a decode along the way.
                    int recencyOf(BeatmapSetInfo s) => recentlyPlayed.IndexOf(s.ID.ToString());

                    var ordered = usableBeatmapSets.AsEnumerable()
                                                   .OrderBy(s => recencyOf(s) < 0 ? 0 : 1)
                                                   .ThenByDescending(s => recencyOf(s) < 0 ? RNG.Next() : recencyOf(s))
                                                   .ToList();

                    foreach (var setInfo in ordered)
                    {
                        foreach (var beatmapInfo in setInfo.Beatmaps)
                        {
                            bool? inclusion = beatmapInfo.UserSettings.IntroPoolInclusion;

                            // A map the user has explicitly kept out of the pool costs nothing: no decode.
                            if (inclusion == false)
                                continue;

                            try
                            {
                                var working = beatmaps.GetWorkingBeatmap(beatmapInfo);
                                double? drop = working.Beatmap.IntroBeatdropTime;

                                if (!IntroBeatdropPool.IsCandidate(inclusion, drop.HasValue))
                                    continue;

                                initialBeatmap = working;
                                beatdropTime = IntroBeatdropPool.ResolveDropTime(drop, beatmapInfo.Metadata.PreviewTime);
                                selectedSetId = setInfo.ID;
                                break;
                            }
                            catch
                            {
                                // an unreadable/corrupt map shouldn't block startup, try the next one.
                            }
                        }

                        if (initialBeatmap != null)
                            break;
                    }
                });

                if (initialBeatmap != null)
                {
                    writeBeatdropHistory(selectedSetId, recentlyPlayed);
                    Logger.Log($"Intro beatdrop track: {initialBeatmap.Metadata.Artist} - {initialBeatmap.Metadata.Title} (drop at {beatdropTime:0}ms)");
                }
                else
                    Logger.Log("No intro pool candidates; intro will run silent.");
            }

            AddInternal(new GlobalScrollAdjustsVolume());
        }

        private const string beatdrop_history_filename = "intro_beatdrop_history.txt";

        /// <summary>
        /// How many recently-played beatdrop maps to remember and steer away from. Kept modest so
        /// a small flagged library still cycles rather than starving.
        /// </summary>
        private const int beatdrop_history_size = 8;

        /// <summary>
        /// The set IDs of recently-chosen intro beatdrop maps, most-recent first. Used to bias the
        /// intro shuffle away from immediate repeats (see the selection in <see cref="load"/>).
        /// </summary>
        private List<string> readBeatdropHistory()
        {
            try
            {
                if (!storage.Exists(beatdrop_history_filename))
                    return new List<string>();

                using var stream = storage.GetStream(beatdrop_history_filename);
                using var reader = new StreamReader(stream);

                return reader.ReadToEnd()
                             .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                             .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        private void writeBeatdropHistory(Guid selectedSetId, List<string> previous)
        {
            try
            {
                var updated = new List<string> { selectedSetId.ToString() };
                updated.AddRange(previous);

                updated = updated.Distinct().Take(beatdrop_history_size).ToList();

                using var stream = storage.CreateFileSafely(beatdrop_history_filename);
                using var writer = new StreamWriter(stream);

                foreach (string id in updated)
                    writer.WriteLine(id);
            }
            catch
            {
                // history is a best-effort nicety; failing to persist it just means the next
                // intro might repeat, which is harmless.
            }
        }

        public override void OnEntering(ScreenTransitionEvent e)
        {
            base.OnEntering(e);
            ensureEventuallyArrivingAtMenu();
        }

        [Resolved]
        private INotificationOverlay notifications { get; set; }

        private void ensureEventuallyArrivingAtMenu()
        {
            // This intends to handle the case where an intro may get stuck.
            // Historically, this could happen if the host system's audio device is in a state it can't
            // play audio, causing a clock to never elapse time and the intro to never end.
            //
            // This safety measure gives the user a chance to fix the problem from the settings menu.
            Scheduler.AddDelayed(() =>
            {
                if (DidLoadMenu)
                    return;

                PrepareMenuLoad();
                LoadMenu();

                if (!Debugger.IsAttached)
                {
                    notifications.Post(new SimpleErrorNotification
                    {
                        Text = NotificationsStrings.AudioPlaybackIssue
                    });
                }
            }, 8000);
        }

        public override void OnResuming(ScreenTransitionEvent e)
        {
            this.FadeIn(300);

            ApplyToBackground(b => b.FadeColour(Color4.Black, 100));

            double fadeOutTime = exit_delay;

            var track = musicController.CurrentTrack;

            // ensure the track doesn't change or loop as we are exiting.
            track.Looping = false;
            Beatmap.Disabled = true;

            // we also handle the exit transition.
            if (MenuVoice.Value)
            {
                if (skinnableSeeya != null)
                {
                    // resuming a screen (i.e. calling OnResume) happens before the screen itself becomes alive,
                    // therefore skinnable samples may not be updated yet with the recently selected skin.
                    // schedule after children to ensure skinnable samples have processed skin changes before playing.
                    ScheduleAfterChildren(() => skinnableSeeya.Play());
                }
                else
                    seeya.Play();

                // if playing the outro voice, we have more time to have fun with the background track.
                // initially fade to almost silent then ramp out over the remaining time.
                const double initial_fade = 200;
                track
                    .VolumeTo(0.03f, initial_fade).Then()
                    .VolumeTo(0, fadeOutTime - initial_fade, Easing.In);
            }
            else
            {
                fadeOutTime = 500;

                // if outro voice is turned off, just do a simple fade out.
                track.VolumeTo(0, fadeOutTime, Easing.Out);
            }

            //don't want to fade out completely else we will stop running updates.
            Game.FadeTo(0.01f, fadeOutTime).OnComplete(_ => this.Exit());

            base.OnResuming(e);
        }

        private bool backgroundFaded;

        protected void FadeInBackground(float duration = 0)
        {
            ApplyToBackground(b => b.FadeColour(Color4.White, duration));
            backgroundFaded = true;
        }

        public override void OnSuspending(ScreenTransitionEvent e)
        {
            base.OnSuspending(e);
            initialBeatmap = null;

            if (!backgroundFaded)
                FadeInBackground(200);
        }

        protected void StartTrack()
        {
            var drawableTrack = musicController.CurrentTrack;

            initialBeatmap?.PrepareTrackForPreview(false, -2600);

            drawableTrack.VolumeTo(0f);
            drawableTrack.Restart();
            drawableTrack.VolumeTo(1, 2600, Easing.InCubic);
        }

        /// <summary>
        /// Starts the selected beatdrop beatmap's track, timed so the beatdrop lands
        /// <paramref name="dropTime"/> ms from now — the moment the intro animation reveals the
        /// menu. If the drop sits earlier in the song than <paramref name="dropTime"/>, playback
        /// is instead delayed (silence first) so the drop still lands on cue.
        /// No-op when no beatdrop beatmap was selected (<see cref="HasBeatdropTrack"/>).
        /// </summary>
        protected void StartBeatdropTrack(double dropTime)
        {
            if (beatdropTime is not double drop)
                return;

            double seekTime = drop - dropTime;
            double startDelay = 0;

            if (seekTime < 0)
            {
                startDelay = -seekTime;
                seekTime = 0;
            }

            // Ramp in over the available lead time but settle at full volume comfortably
            // before the drop, so the drop itself is never mid-fade.
            double rampTime = Math.Clamp(dropTime - startDelay - 250, 0, 2200);
            double seek = seekTime;

            Scheduler.AddDelayed(() =>
            {
                var drawableTrack = musicController.CurrentTrack;

                Track.RestartPoint = seek;
                drawableTrack.VolumeTo(0.3f);
                drawableTrack.Restart();
                drawableTrack.VolumeTo(1, rampTime, Easing.InCubic);
            }, startDelay);
        }

        protected override void LogoArriving(OsuLogo logo, bool resuming)
        {
            base.LogoArriving(logo, resuming);

            logo.Colour = Color4.White;
            logo.Triangles = false;
            logo.Ripple = false;

            if (!resuming)
            {
                // Null when the intro pool is empty (or menu music is disabled): the intro
                // then runs on the default (silent) beatmap.
                if (initialBeatmap != null)
                    beatmap.Value = initialBeatmap;
                Track = beatmap.Value.Track;

                // ensure the track starts at maximum volume
                musicController.CurrentTrack.FinishTransforms();

                logo.MoveTo(new Vector2(0.5f));
                logo.ScaleTo(Vector2.One);
                logo.Hide();
            }
            else
            {
                const int quick_appear = 350;
                int initialMovementTime = logo.Alpha > 0.2f ? quick_appear : 0;

                logo.MoveTo(new Vector2(0.5f), initialMovementTime, Easing.OutQuint);

                logo
                    .ScaleTo(1, initialMovementTime, Easing.OutQuint)
                    .FadeIn(quick_appear, Easing.OutQuint)
                    .Then()
                    .RotateTo(20, exit_delay * 1.5f)
                    .FadeOut(exit_delay);
            }
        }

        protected void PrepareMenuLoad()
        {
            if (nextScreen != null)
                return;

            nextScreen = createNextScreen?.Invoke();

            if (nextScreen != null)
                LoadComponentAsync(nextScreen);
        }

        protected void LoadMenu()
        {
            if (DidLoadMenu)
                return;

            beatmap.Return();

            DidLoadMenu = true;

            if (nextScreen != null)
            {
                this.Push(nextScreen);
                return;
            }

            // A beatdrop demo is pushed over the menu it is going to reveal rather than being handed a menu
            // to push, so at the reveal it exits back into the one it came from. That leaves the screen stack
            // exactly the shape it was before the demo, instead of stacking a second menu (and a second
            // intro) on top of the startup pair the game's exit path relies on.
            if (Demo != null)
                this.Exit();
        }
    }
}
