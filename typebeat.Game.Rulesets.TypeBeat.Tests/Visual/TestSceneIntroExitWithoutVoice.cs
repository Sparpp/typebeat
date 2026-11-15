// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Screens;
using typebeat.Game.Overlays;
using typebeat.Game.Screens;
using typebeat.Game.Screens.Menu;
using typebeat.Game.Tests.Visual;
using osuTK;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Backlog 483 removed "Interface voices": the game never plays a voice clip on start or quit. On
    /// the way out that is visible as timing. With osu!'s "seeya" voice on (the setting's default), the
    /// intro screen held the game open for three seconds so the clip could finish; without it, quitting
    /// is the short fade osu! used with the voice off. This runs the real intro (Circles, the sequence
    /// headless tests use; the exit is IntroScreen's, shared by every sequence) to its menu hand-off,
    /// then quits back through it.
    /// </summary>
    public partial class TestSceneIntroExitWithoutVoice : OsuTestScene
    {
        [Resolved]
        private OsuGameBase game { get; set; } = null!;

        [Test]
        public void TestQuittingDoesNotWaitForAnOutroVoice()
        {
            OsuScreenStack stack = null!;
            IntroCircles intro = null!;
            BlankScreen menu = null!;
            double quitAt = 0;
            double? closedAt = null;

            AddStep("run the intro", () =>
            {
                var logo = new OsuLogo
                {
                    Alpha = 0,
                    RelativePositionAxes = Axes.Both,
                    Position = new Vector2(0.5f),
                };
                var notifications = new NotificationOverlay();

                Child = new DependencyProvidingContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    CachedDependencies = new (Type, object)[]
                    {
                        (typeof(OsuLogo), logo),
                        (typeof(INotificationOverlay), notifications),
                    },
                    Children = new Drawable[]
                    {
                        stack = new OsuScreenStack { RelativeSizeAxes = Axes.Both },
                        logo,
                        notifications,
                    },
                };

                stack.ScreenExited += (last, _) =>
                {
                    if (last == intro)
                        closedAt ??= Time.Current;
                };
                stack.Push(intro = new IntroCircles());
            });

            AddUntilStep("the intro hands over to the menu", () => intro.DidLoadMenu);

            AddStep("open a screen above it", () => stack.Push(menu = new BlankScreen()));
            AddUntilStep("it is current", () => menu.IsCurrentScreen());

            AddStep("quit back through the intro", () =>
            {
                quitAt = Time.Current;
                menu.Exit();
            });

            AddUntilStep("the intro closes", () => closedAt != null);
            AddAssert("after the short fade, not the voice's three seconds", () => closedAt!.Value - quitAt,
                () => Is.LessThan(IntroScreen.EXIT_FADE_DURATION + 1000));

            AddStep("restore the game", () =>
            {
                game.FadeIn();
                Beatmap.Disabled = false;
            });
        }

        /// <summary>Stands in for the main menu, whose exit is what sends the game back through the intro.</summary>
        private partial class BlankScreen : OsuScreen
        {
        }
    }
}
