// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.Collections.Generic;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Development;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Platform;
using osu.Framework.Screens;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Configuration;
using typebeat.Game.Database;
using typebeat.Game.Graphics.Cursor;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Online.API;
using typebeat.Game.Online.Spectator;
using typebeat.Game.Overlays;
using typebeat.Game.Rulesets;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Scoring;
using typebeat.Game.Screens;
using typebeat.Game.Screens.Menu;
using typebeat.Game.Screens.Play;
using osuTK.Graphics;
using IntroSequence = typebeat.Game.Configuration.IntroSequence;

namespace typebeat.Game.Tests.Visual
{
    /// <summary>
    /// A scene which tests full game flow.
    /// </summary>
    public abstract partial class OsuGameTestScene : OsuManualInputManagerTestScene
    {
        protected TestOsuGame Game;

        protected override bool UseFreshStoragePerRun => true;

        protected override bool CreateNestedActionContainer => false;

        protected override bool DisplayCursorForManualInput => false;

        [BackgroundDependencyLoader]
        private void load()
        {
            Child = new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Color4.Black,
            };
        }

        [SetUpSteps]
        public virtual void SetUpSteps()
        {
            CreateNewGame();
            ConfirmAtMainMenu();
        }

        protected void CreateNewGame()
        {
            AddStep("Create new game instance", () =>
            {
                if (Game?.Parent != null)
                    Remove(Game, true);

                RecycleLocalStorage(false);

                CreateGame();
            });

            AddUntilStep("Wait for load", () => Game.IsLoaded);
            AddUntilStep("Wait for intro", () => Game.ScreenStack.CurrentScreen is IntroScreen);
        }

        [TearDownSteps]
        public virtual void TearDownSteps()
        {
            if (DebugUtils.IsNUnitRunning)
            {
                AddStep("exit game", () => Game?.Exit());
                AddUntilStep("wait for game exit", () => Game?.Parent == null);
                AddStep("dispose game", () => Game?.Dispose());
            }
        }

        protected void CreateGame()
        {
            AddGame(Game = CreateTestGame());
        }

        protected virtual TestOsuGame CreateTestGame() => new TestOsuGame(LocalStorage, API);

        protected void PushAndConfirm(Func<Screen> newScreen)
        {
            Screen screen = null;
            IScreen previousScreen = null;

            AddStep("Push new screen", () =>
            {
                previousScreen = Game.ScreenStack.CurrentScreen;
                Game.ScreenStack.Push(screen = newScreen());
            });

            AddUntilStep("Wait for new screen", () => screen.IsLoaded
                                                      && Game.ScreenStack.CurrentScreen != previousScreen
                                                      && previousScreen.GetChildScreen() == screen);
        }

        protected void ConfirmAtMainMenu() => AddUntilStep("Wait for main menu", () => Game.ScreenStack.CurrentScreen is MainMenu menu && menu.IsLoaded);

        /// <summary>
        /// Dismisses any notifications pushed which block from interacting with the game (or block screens from loading, e.g. <see cref="Player"/>).
        /// </summary>
        protected void DismissAnyNotifications() => Game.Notifications.State.Value = Visibility.Hidden;

        public partial class TestOsuGame : OsuGame
        {
            public new const float SIDE_OVERLAY_OFFSET_RATIO = OsuGame.SIDE_OVERLAY_OFFSET_RATIO;

            public RealmAccess Realm => Dependencies.Get<RealmAccess>();

            public new GlobalCursorDisplay GlobalCursorDisplay => base.GlobalCursorDisplay;

            public new BackButton BackButton => base.BackButton;

            public new BeatmapManager BeatmapManager => base.BeatmapManager;

            public new ScoreManager ScoreManager => base.ScoreManager;

            public new Container ScreenOffsetContainer => base.ScreenOffsetContainer;

            public new SettingsOverlay Settings => base.Settings;

            public new NotificationOverlay Notifications => base.Notifications;

            public new FirstRunSetupOverlay FirstRunOverlay => base.FirstRunOverlay;

            public new MusicController MusicController => base.MusicController;

            public new OsuConfigManager LocalConfig => base.LocalConfig;

            public new Bindable<WorkingBeatmap> Beatmap => base.Beatmap;

            public new Bindable<RulesetInfo> Ruleset => base.Ruleset;

            public new Bindable<IReadOnlyList<Mod>> SelectedMods => base.SelectedMods;

            public new Storage Storage => base.Storage;

            public new SpectatorClient SpectatorClient => base.SpectatorClient;

            // if we don't apply these changes, when running under nUnit the version that gets populated is that of nUnit.
            public override Version AssemblyVersion => new Version(0, 0);
            public override string Version => "test game";

            protected override Loader CreateLoader() => new TestLoader();

            public new void PerformFromScreen(Action<IScreen> action, IEnumerable<Type> validScreens = null) => base.PerformFromScreen(action, validScreens);

            public TestOsuGame(Storage storage, IAPIProvider api, string[] args = null)
                : base(args)
            {
                base.Storage = storage;
                API = api;
            }

            protected override void LoadComplete()
            {
                base.LoadComplete();

                LocalConfig.SetValue(OsuSetting.IntroSequence, IntroSequence.Circles);
                LocalConfig.SetValue(OsuSetting.ShowFirstRunSetup, false);

                API.Login("Rhythm Champion", "type!beat");
                ((DummyAPIAccess)API).AuthenticateSecondFactor("abcdefgh");

                Dependencies.Get<SessionStatics>().SetValue(Static.MutedAudioNotificationShownOnce, true);

                // set applied version to latest so that the BackgroundBeatmapProcessor doesn't consider
                // beatmap star ratings as outdated and reset them throughout the test.
                foreach (var ruleset in RulesetStore.AvailableRulesets)
                    ruleset.LastAppliedDifficultyVersion = ruleset.CreateInstance().CreateDifficultyCalculator(Beatmap.Default).Version;
            }

            protected override void Update()
            {
                base.Update();

                // when running in visual tests and the window loses focus, we generally don't want the game to pause.
                ((Bindable<bool>)IsActive).Value = true;
            }
        }

        public partial class TestLoader : Loader
        {
            protected override ShaderPrecompiler CreateShaderPrecompiler() => new TestShaderPrecompiler();

            private partial class TestShaderPrecompiler : ShaderPrecompiler
            {
                protected override bool AllLoaded => true;
            }
        }
    }
}
