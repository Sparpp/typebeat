// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Effects;
using osu.Framework.Input.Events;
using osu.Framework.Testing;
using typebeat.Game.Screens.Edit.Components;
using typebeat.Game.Screens.Edit.Components.Timelines.Summary;
using osuTK;
using osuTK.Graphics;

namespace typebeat.Game.Screens.Edit
{
    internal partial class BottomBar : CompositeDrawable
    {
        public TestGameplayButton TestGameplayButton { get; private set; } = null!;

        private IBindable<bool> saveInProgress = null!;
        private Bindable<bool> composerFocusMode = null!;

        [BackgroundDependencyLoader]
        private void load(Editor editor)
        {
            Anchor = Anchor.BottomLeft;
            Origin = Anchor.BottomLeft;

            RelativeSizeAxes = Axes.X;

            Height = 50;

            Masking = true;
            EdgeEffect = new EdgeEffectParameters
            {
                Colour = Color4.Black.Opacity(0.2f),
                Type = EdgeEffectType.Shadow,
                Radius = 10f,
            };

            InternalChildren = new Drawable[]
            {
                new GridContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    ColumnDimensions = new[]
                    {
                        new Dimension(GridSizeMode.Absolute, 150),
                        new Dimension(),
                        new Dimension(GridSizeMode.Absolute, 220),
                        // The ruleset slot: zero width unless the active ruleset's compose screen
                        // publishes an action, so the summary timeline only gives up space when
                        // there is actually a button there.
                        new Dimension(GridSizeMode.AutoSize),
                        new Dimension(GridSizeMode.Absolute, 120f),
                    },
                    Content = new[]
                    {
                        new Drawable[]
                        {
                            new TimeInfoContainer { RelativeSizeAxes = Axes.Both },
                            new SummaryTimeline { RelativeSizeAxes = Axes.Both },
                            new PlaybackControl { RelativeSizeAxes = Axes.Both },
                            new Container
                            {
                                AutoSizeAxes = Axes.X,
                                RelativeSizeAxes = Axes.Y,
                                Child = new RulesetActionButton(),
                            },
                            TestGameplayButton = new TestGameplayButton
                            {
                                RelativeSizeAxes = Axes.Both,
                                Size = new Vector2(1),
                                Action = editor.TestGameplay,
                            }
                        },
                    }
                }
            };

            saveInProgress = editor.MutationTracker.InProgress.GetBoundCopy();
            composerFocusMode = editor.ComposerFocusMode.GetBoundCopy();
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            saveInProgress.BindValueChanged(_ => TestGameplayButton.Enabled.Value = !saveInProgress.Value, true);
            composerFocusMode.BindValueChanged(_ =>
            {
                // Transforms should be kept in sync with other usages of composer focus mode.
                foreach (var c in this.ChildrenOfType<BottomBarContainer>())
                {
                    if (!composerFocusMode.Value)
                        c.Background.FadeIn(750, Easing.OutQuint);
                    else
                        c.Background.Delay(600).FadeTo(0.5f, 4000, Easing.OutQuint);
                }
            }, true);
        }

        protected override bool OnMouseDown(MouseDownEvent e) => true;
        protected override bool OnClick(ClickEvent e) => true;
    }
}
