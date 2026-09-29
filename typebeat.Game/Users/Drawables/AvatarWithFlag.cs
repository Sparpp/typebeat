// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osuTK;
using osuTK.Graphics;
using typebeat.Game.Configuration;

namespace typebeat.Game.Users.Drawables
{
    /// <summary>
    /// A user's avatar with their country flag as a small badge in its bottom-right corner, the one
    /// shape every compact "who set this" row uses (the song-select and beatmap leaderboards, the
    /// gameplay leaderboard and the user panels), so that the flag costs those rows no width.
    ///
    /// <para>The badge is about a third of the avatar's height, masked and bordered so it reads
    /// against any avatar, and it is HIDDEN when the country is <see cref="CountryCode.Unknown"/>
    /// (or the player hides flags in settings): the '?' tile is for the places where a flag tile is
    /// the point, like the rankings' country column, never for a badge on someone's face. It takes
    /// no input, so a click anywhere on the avatar still does what the avatar does.</para>
    ///
    /// <para>Places where the flag sits beside the name at full size (the profile header, the login
    /// overlay's signed-in panel) keep a plain <see cref="UpdateableFlag"/> instead.</para>
    /// </summary>
    public partial class AvatarWithFlag : CompositeDrawable
    {
        /// <summary>The badge's height as a fraction of the avatar's.</summary>
        public const float FLAG_HEIGHT_RATIO = 1 / 3f;

        /// <summary>The flag textures' aspect (150x108), which the badge keeps.</summary>
        public const float FLAG_ASPECT = 150f / 108f;

        /// <summary>The badge's inset from the avatar's bottom-right corner, as a fraction of the avatar's size.</summary>
        public const float FLAG_INSET_RATIO = 0.06f;

        /// <summary>The avatar drawable this wraps, filling the whole of this component.</summary>
        public Drawable Avatar { get; }

        /// <summary>
        /// The badge (the masked, bordered container holding the flag). Exposed so a sheared row
        /// can counter-shear it, as those rows already do for the avatar itself.
        /// </summary>
        public Container Badge { get; }

        /// <summary>The flag inside <see cref="Badge"/>.</summary>
        public UpdateableFlag Flag { get; }

        private CountryCode countryCode;

        public CountryCode CountryCode
        {
            get => countryCode;
            set
            {
                countryCode = value;
                Flag.CountryCode = value;
                updateBadgeVisibility();
            }
        }

        private readonly Bindable<bool> hideFlags = new BindableBool();

        public AvatarWithFlag(Drawable avatar, CountryCode countryCode)
        {
            Avatar = avatar;
            avatar.RelativeSizeAxes = Axes.Both;

            InternalChildren = new[]
            {
                avatar,
                Badge = new InertContainer
                {
                    Anchor = Anchor.BottomRight,
                    Origin = Anchor.BottomRight,
                    RelativeSizeAxes = Axes.Both,
                    RelativePositionAxes = Axes.Both,
                    Position = new Vector2(-FLAG_INSET_RATIO),
                    Size = new Vector2(1, FLAG_HEIGHT_RATIO),
                    FillMode = FillMode.Fit,
                    FillAspectRatio = FLAG_ASPECT,
                    Masking = true,
                    CornerRadius = 2,
                    BorderThickness = 1.5f,
                    BorderColour = Color4.Black.Opacity(0.6f),
                    Child = Flag = new UpdateableFlag
                    {
                        RelativeSizeAxes = Axes.Both,
                    },
                },
            };

            CountryCode = countryCode;
        }

        [BackgroundDependencyLoader]
        private void load(OsuConfigManager config)
        {
            config.BindWith(OsuSetting.HideCountryFlags, hideFlags);
            hideFlags.BindValueChanged(_ => updateBadgeVisibility(), true);
        }

        /// <summary>Whether the badge is showing (a real country, flags not hidden).</summary>
        public bool BadgeVisible => Badge.Alpha > 0;

        private void updateBadgeVisibility() => Badge.Alpha = countryCode == CountryCode.Unknown || hideFlags.Value ? 0 : 1;

        /// <summary>
        /// The badge is a label, not a control: nothing under it takes the pointer, so the avatar
        /// beneath keeps every click and hover (the flag's own click would open country rankings,
        /// which is not what a click on someone's avatar means).
        /// </summary>
        private partial class InertContainer : Container
        {
            public override bool PropagatePositionalInputSubTree => false;
        }
    }
}
