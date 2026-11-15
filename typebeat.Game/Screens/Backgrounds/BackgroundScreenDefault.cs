// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System.Threading;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Logging;
using osu.Framework.Threading;
using osu.Framework.Utils;
using typebeat.Game.Beatmaps;
using typebeat.Game.Graphics.Backgrounds;
using typebeat.Game.Online.API;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Skinning;

namespace typebeat.Game.Screens.Backgrounds
{
    /// <summary>
    /// The menu's background: always the default rotation of menu background images.
    /// </summary>
    /// <remarks>
    /// osu! also let supporters show the beatmap's background (with or without its storyboard) or the
    /// skin's ("Background source"), and showed seasonal backgrounds when the server offered them.
    /// type!beat has no supporters and no seasonal backgrounds, so neither ever applied; both were removed
    /// in backlog 483, leaving exactly what every player already saw.
    /// </remarks>
    public partial class BackgroundScreenDefault : BackgroundScreen
    {
        private Background background;

        private int currentDisplay;
        private const int background_count = 8;
        private IBindable<APIUser> user;
        private Bindable<Skin> skin;

        [Resolved]
        private IBindable<WorkingBeatmap> beatmap { get; set; }

        [BackgroundDependencyLoader]
        private void load(IAPIProvider api, SkinManager skinManager)
        {
            user = api.LocalUser.GetBoundCopy();
            skin = skinManager.CurrentSkin.GetBoundCopy();
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            // A change of user, skin or beatmap moves the rotation on to the next image, as it always has
            // (they once picked which kind of background to show; the rotation is all that is left).
            user.ValueChanged += _ => Scheduler.AddOnce(next);
            skin.ValueChanged += _ => Scheduler.AddOnce(next);
            beatmap.ValueChanged += _ => Scheduler.AddOnce(next);

            currentDisplay = RNG.Next(0, background_count);
            Next();

            // helper function required for AddOnce usage.
            void next() => Next();
        }

        private ScheduledDelegate nextTask;
        private CancellationTokenSource cancellationTokenSource;

        /// <summary>
        /// Request loading the next background.
        /// </summary>
        /// <returns>Whether a new background was queued for load. May return false if the current background is still valid.</returns>
        public virtual bool Next()
        {
            var nextBackground = createBackground();

            // in the case that the background hasn't changed, we want to avoid cancelling any tasks that could still be loading.
            if (nextBackground == background)
                return false;

            Logger.Log(@"🌅 Global background change queued");

            cancellationTokenSource?.Cancel();
            cancellationTokenSource = new CancellationTokenSource();

            nextTask?.Cancel();
            nextTask = Scheduler.AddDelayed(() =>
            {
                Logger.Log(@"🌅 Global background loading");
                LoadComponentAsync(nextBackground, displayNext, cancellationTokenSource.Token);
            }, 500);

            return true;
        }

        private void displayNext(Background newBackground)
        {
            background?.FadeOut(800, Easing.OutQuint);
            background?.Expire();

            AddInternal(background = newBackground);
            currentDisplay++;
        }

        private Background createBackground() => new Background(getBackgroundTextureName())
        {
            Depth = currentDisplay
        };

        // The intro is always Triangles now, so the Welcome intro's single background never applies.
        private string getBackgroundTextureName() => $@"Menu/menu-background-{currentDisplay % background_count + 1}";
    }
}
