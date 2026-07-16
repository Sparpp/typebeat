// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using JetBrains.Annotations;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Audio.Sample;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.Screens;
using osu.Framework.Timing;
using osu.Framework.Utils;
using typebeat.Game.Graphics;
using typebeat.Game.Graphics.Containers;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Rulesets;
using osuTK;
using osuTK.Graphics;

namespace typebeat.Game.Screens.Menu
{
    public partial class IntroTriangles : IntroScreen
    {
        private TrianglesIntroSequence intro;

        public IntroTriangles([CanBeNull] Func<MainMenu> createNextScreen = null)
            : base(createNextScreen)
        {
        }

        protected override void LogoArriving(OsuLogo logo, bool resuming)
        {
            base.LogoArriving(logo, resuming);

            if (!resuming)
            {
                PrepareMenuLoad();

                var decouplingClock = new DecouplingFramedClock(null);

                LoadComponentAsync(intro = new TrianglesIntroSequence(logo, () => FadeInBackground())
                {
                    RelativeSizeAxes = Axes.Both,
                    Clock = new InterpolatingFramedClock(decouplingClock),
                    LoadMenu = LoadMenu
                }, _ =>
                {
                    AddInternal(intro);

                    // There is a chance that the intro timed out before being displayed, and this scheduled callback could
                    // happen during the outro rather than intro.
                    // In such a scenario, we don't want to start the intro track
                    // (that may have already been since disposed by MusicController).
                    if (DidLoadMenu)
                        return;

                    // Time the beatdrop track so its drop lands exactly on the menu reveal.
                    // With no beatdrop map selected, the intro animation runs silent.
                    StartBeatdropTrack(TrianglesIntroSequence.MENU_LOAD_TIME);

                    decouplingClock.Start();
                });
            }
        }

        public override void OnSuspending(ScreenTransitionEvent e)
        {
            base.OnSuspending(e);

            // important as there is a clock attached to a track which will likely be disposed before returning to this screen.
            intro.Expire();
        }

        private partial class TrianglesIntroSequence : CompositeDrawable
        {
            private readonly OsuLogo logo;
            private readonly Action showBackgroundAction;
            private OsuSpriteText welcomeText;

            private RulesetFlow rulesets;
            private Container rulesetsScale;
            private Container logoContainerSecondary;
            private LazerLogo lazerLogo;

            private GlitchingSquares squares;

            public Action LoadMenu;

            public TrianglesIntroSequence(OsuLogo logo, Action showBackgroundAction)
            {
                this.logo = logo;
                this.showBackgroundAction = showBackgroundAction;
            }

            [Resolved]
            private OsuGameBase game { get; set; }

            [BackgroundDependencyLoader]
            private void load()
            {
                InternalChildren = new Drawable[]
                {
                    squares = new GlitchingSquares
                    {
                        Alpha = 0,
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        Size = new Vector2(0.4f, 0.16f)
                    },
                    welcomeText = new OsuSpriteText
                    {
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        Padding = new MarginPadding { Bottom = 10 },
                        Font = OsuFont.GetFont(weight: FontWeight.Light, size: 42),
                        Alpha = 1,
                        Spacing = new Vector2(5),
                    },
                    rulesetsScale = new Container
                    {
                        RelativeSizeAxes = Axes.Both,
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        Children = new Drawable[]
                        {
                            rulesets = new RulesetFlow()
                        }
                    },
                    logoContainerSecondary = new Container
                    {
                        RelativeSizeAxes = Axes.Both,
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        Child = lazerLogo = new LazerLogo
                        {
                            Anchor = Anchor.Centre,
                            Origin = Anchor.Centre
                        }
                    },
                };
            }

            private const double text_1 = 200;
            private const double text_2 = 400;
            private const double text_3 = 700;
            private const double text_4 = 900;
            private const double text_glitch = 1060;

            private const double rulesets_1 = 1450;
            private const double rulesets_2 = 1650;
            private const double rulesets_3 = 1850;

            private const double logo_scale_duration = 920;
            private const double logo_1 = 2080;
            private const double logo_2 = logo_1 + logo_scale_duration;

            /// <summary>
            /// Time (ms from sequence start) at which <see cref="LoadMenu"/> fires — the menu
            /// reveal. The intro beatdrop is timed to land exactly here.
            /// </summary>
            public const double MENU_LOAD_TIME = logo_2;

            protected override void LoadComplete()
            {
                base.LoadComplete();

                const float scale_start = 1.2f;
                const float scale_adjust = 0.8f;

                rulesets.Hide();
                lazerLogo.Hide();

                using (BeginAbsoluteSequence(0))
                {
                    using (BeginDelayedSequence(text_1))
                        welcomeText.FadeIn().OnComplete(t => t.Text = "wel");

                    using (BeginDelayedSequence(text_2))
                        welcomeText.FadeIn().OnComplete(t => t.Text = "welcome");

                    using (BeginDelayedSequence(text_3))
                        welcomeText.FadeIn().OnComplete(t => t.Text = "welcome to");

                    using (BeginDelayedSequence(text_4))
                    {
                        welcomeText.FadeIn().OnComplete(t => t.Text = "welcome to type!beat");
                        welcomeText.TransformTo(nameof(welcomeText.Spacing), new Vector2(50, 0), 5000);
                    }

                    using (BeginDelayedSequence(text_glitch))
                        squares.FadeIn();

                    using (BeginDelayedSequence(rulesets_1))
                    {
                        rulesetsScale.ScaleTo(0.8f, 1000);
                        rulesets.FadeIn().ScaleTo(1).TransformSpacingTo(new Vector2(200, 0));
                        welcomeText.FadeOut().Expire();
                        squares.FadeOut().Expire();
                    }

                    using (BeginDelayedSequence(rulesets_2))
                    {
                        rulesets.ScaleTo(2).TransformSpacingTo(new Vector2(30, 0));
                    }

                    using (BeginDelayedSequence(rulesets_3))
                    {
                        rulesets.ScaleTo(4).TransformSpacingTo(new Vector2(10, 0));
                        rulesetsScale.ScaleTo(1.3f, 1000);
                    }

                    using (BeginDelayedSequence(logo_1))
                    {
                        rulesets.FadeOut();

                        // matching flyte curve y = 0.25x^2 + (max(0, x - 0.7) / 0.3) ^ 5
                        lazerLogo.FadeIn().ScaleTo(scale_start).Then().Delay(logo_scale_duration * 0.7f).ScaleTo(scale_start - scale_adjust, logo_scale_duration * 0.3f, Easing.InQuint);

                        lazerLogo.TransformTo(nameof(LazerLogo.Progress), 1f, logo_scale_duration);

                        logoContainerSecondary.ScaleTo(scale_start).Then().ScaleTo(scale_start - scale_adjust * 0.25f, logo_scale_duration, Easing.InQuad);
                    }

                    using (BeginDelayedSequence(logo_2))
                    {
                        lazerLogo.FadeOut().OnComplete(_ =>
                        {
                            logoContainerSecondary.Remove(lazerLogo, true);

                            logo.FadeIn();

                            showBackgroundAction();

                            game.Add(new GameWideFlash());

                            LoadMenu();
                        });
                    }
                }
            }

            private partial class GameWideFlash : Box
            {
                private const double flash_length = 1000;

                public GameWideFlash()
                {
                    Colour = Color4.White;
                    RelativeSizeAxes = Axes.Both;
                    Blending = BlendingParameters.Additive;
                }

                protected override void LoadComplete()
                {
                    base.LoadComplete();
                    this.FadeOutFromOne(flash_length, Easing.Out);
                }
            }

            private partial class LazerLogo : CompositeDrawable
            {
                private LogoAnimation highlight, background;

                public float Progress
                {
                    get => background.AnimationProgress;
                    set
                    {
                        background.AnimationProgress = value;
                        highlight.AnimationProgress = value;
                    }
                }

                public LazerLogo()
                {
                    Size = new Vector2(960);
                }

                [BackgroundDependencyLoader]
                private void load(LargeTextureStore textures)
                {
                    InternalChildren = new Drawable[]
                    {
                        highlight = new LogoAnimation
                        {
                            RelativeSizeAxes = Axes.Both,
                            Texture = textures.Get(@"Intro/Triangles/logo-highlight"),
                            Colour = Color4.White,
                        },
                        background = new LogoAnimation
                        {
                            RelativeSizeAxes = Axes.Both,
                            Texture = textures.Get(@"Intro/Triangles/logo-background"),
                            Colour = OsuColour.Gray(0.6f),
                        },
                    };
                }
            }

            private partial class RulesetFlow : FillFlowContainer
            {
                // Three ruleset icons, as textures shipped in the resources package. The flow
                // auto-lays them out with no gap; spacing is animated in the intro sequence.
                private static readonly string[] ruleset_icons = { "RulesetOsu", "RulesetCatch", "RulesetMania" };

                [BackgroundDependencyLoader]
                private void load(LargeTextureStore textures)
                {
                    AutoSizeAxes = Axes.Both;

                    Anchor = Anchor.Centre;
                    Origin = Anchor.Centre;

                    foreach (string name in ruleset_icons)
                    {
                        Add(new Sprite
                        {
                            Anchor = Anchor.Centre,
                            Origin = Anchor.Centre,
                            Size = new Vector2(30),
                            Texture = textures.Get($@"Icons/{name}"),
                        });
                    }
                }
            }

            private partial class GlitchingSquares : CompositeDrawable
            {
                public GlitchingSquares()
                {
                    RelativeSizeAxes = Axes.Both;
                }

                private double? lastGenTime;

                private const double time_between_squares = 22;

                protected override void Update()
                {
                    base.Update();

                    if (lastGenTime == null || Time.Current - lastGenTime > time_between_squares)
                    {
                        lastGenTime = (lastGenTime ?? Time.Current) + time_between_squares;

                        Drawable square = new OutlineSquare(RNG.NextBool(), (RNG.NextSingle() + 0.2f) * 80)
                        {
                            RelativePositionAxes = Axes.Both,
                            Position = new Vector2(RNG.NextSingle(), RNG.NextSingle()),
                        };

                        AddInternal(square);

                        square.FadeOutFromOne(120);
                    }
                }

                /// <summary>
                /// A square that is either solid or hollow (an outline with a punched-out centre).
                /// </summary>
                public partial class OutlineSquare : BufferedContainer
                {
                    public OutlineSquare(bool outlineOnly, float size)
                        : base(cachedFrameBuffer: true)
                    {
                        Size = new Vector2(size);

                        InternalChildren = new Drawable[]
                        {
                            new Box { RelativeSizeAxes = Axes.Both },
                        };

                        if (outlineOnly)
                        {
                            AddInternal(new Box
                            {
                                Anchor = Anchor.Centre,
                                Origin = Anchor.Centre,
                                Colour = Color4.Black,
                                Size = new Vector2(size - 5),
                                Blending = BlendingParameters.None,
                            });
                        }

                        Blending = BlendingParameters.Additive;
                    }
                }
            }
        }
    }
}
