// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Effects;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using typebeat.Game.Graphics;
using typebeat.Game.Graphics.Containers;
using typebeat.Game.Rulesets;
using typebeat.Game.Rulesets.Mods;
using osuTK;
using osuTK.Graphics;

namespace typebeat.Game.Beatmaps.Drawables
{
    public partial class DifficultyIcon : CompositeDrawable, IHasCustomTooltip<DifficultyIconTooltipContent>
    {
        /// <summary>
        /// Size of this difficulty icon.
        /// </summary>
        public new Vector2 Size
        {
            get => iconContainer.Size;
            set => iconContainer.Size = value;
        }

        /// <summary>
        /// Which type of tooltip to show. Only works if a beatmap was provided at construction time.
        /// </summary>
        public DifficultyIconTooltipType TooltipType { get; set; } = DifficultyIconTooltipType.StarRating;

        /// <summary>
        /// Draw the coloured background as a rounded square instead of lazer's disc, following the rounded square of the
        /// type!beat glyph so the colour shows as an even border around it.
        /// </summary>
        public bool SquareBackground { get; init; }

        /// <remarks>
        /// The glyph's square is inset about a tenth of the icon's size on each side and its corners are rounded by about a
        /// tenth too (measured from the <c>RulesetOsu</c> texture), so a background corner of their sum is concentric with them.
        /// </remarks>
        private const float square_corner_radius_ratio = 0.2f;

        /// <remarks>
        /// The glyph's square spans rows 8 to 77 of the 90px <c>RulesetOsu</c> texture (its bottom edge is drawn thicker than its
        /// top), so it sits 2px of 90 above the texture's centre. On the square background that shows as a thicker coloured border
        /// below than above, so the glyph is moved down by that fraction. Columns 9 to 79 of 89 are already centred, and the "t"
        /// is centred within the glyph's square horizontally and inside its outline vertically.
        /// </remarks>
        private const float square_glyph_y_offset = 2 / 90f;

        private readonly IBeatmapInfo? beatmap;

        private readonly IRulesetInfo ruleset;

        private readonly Mod[]? mods;

        private Drawable background = null!;

        private Container backgroundContainer = null!;

        private readonly Container iconContainer;

        [Resolved]
        private OsuColour colours { get; set; } = null!;

        private readonly BindableWithCurrent<StarDifficulty> difficulty = new BindableWithCurrent<StarDifficulty>();

        // TODO: remove this after old song select is gone.
        public virtual Bindable<StarDifficulty> Current
        {
            get => difficulty.Current;
            set => difficulty.Current = value;
        }

        [Resolved]
        private IRulesetStore rulesets { get; set; } = null!;

        /// <summary>
        /// Creates a new <see cref="DifficultyIcon"/>. Will use provided beatmap's <see cref="BeatmapInfo.StarRating"/> for initial value.
        /// </summary>
        /// <param name="beatmap">The beatmap to be displayed in the tooltip, and to be used for the initial star rating value.</param>
        /// <param name="mods">An array of mods to account for in the calculations</param>
        /// <param name="ruleset">An optional ruleset to be used for the icon display, in place of the beatmap's ruleset.</param>
        public DifficultyIcon(IBeatmapInfo beatmap, IRulesetInfo? ruleset = null, Mod[]? mods = null)
        {
            this.beatmap = beatmap;
            this.mods = mods;
            this.ruleset = ruleset ?? beatmap.Ruleset;

            Current.Value = new StarDifficulty(beatmap.StarRating, 0);

            AutoSizeAxes = Axes.Both;
            InternalChild = iconContainer = new Container { Size = new Vector2(20f) };
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            iconContainer.Children = new Drawable[]
            {
                backgroundContainer = (SquareBackground ? new Container() : new CircularContainer()).With(c =>
                {
                    c.RelativeSizeAxes = Axes.Both;
                    c.Anchor = Anchor.Centre;
                    c.Origin = Anchor.Centre;
                    c.Masking = true;
                    c.EdgeEffect = new EdgeEffectParameters
                    {
                        Colour = Color4.Black.Opacity(0.06f),
                        Type = EdgeEffectType.Shadow,
                        Radius = 3,
                    };
                    c.Child = background = new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                    };
                }),
                new ConstrainedIconContainer
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    RelativeSizeAxes = Axes.Both,
                    RelativePositionAxes = Axes.Y,
                    Y = SquareBackground ? square_glyph_y_offset : 0,
                    // the null coalesce here is only present to make unit tests work (ruleset dlls aren't copied correctly for testing at the moment)
                    Icon = getRulesetIcon()
                },
            };
        }

        protected override void Update()
        {
            base.Update();

            if (SquareBackground)
                backgroundContainer.CornerRadius = backgroundContainer.DrawWidth * square_corner_radius_ratio;
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            Current.BindValueChanged(difficulty =>
            {
                background.FadeColour(colours.ForStarDifficulty(difficulty.NewValue.Stars), 200);
            }, true);

            background.FinishTransforms();
        }

        private Drawable getRulesetIcon() => CreateRulesetGlyph(ruleset, rulesets);

        /// <summary>
        /// The glyph a difficulty display draws for <paramref name="ruleset"/>: lazer's osu! ring for the type!beat ruleset.
        /// </summary>
        /// <remarks>
        /// Lazer's difficulty displays (this icon, <see cref="DifficultySpectrumDisplay"/>, the card's difficulty list) draw the
        /// ruleset's <see cref="Ruleset.CreateIcon"/>, which in lazer is a font glyph that scales to whatever size it is given and
        /// leaves the star colour showing around it. type!beat's ruleset icon is a fixed 20px solid circle (kept for the toolbar),
        /// which these displays cannot scale: it covered the coloured disc here and dwarfed the spectrum's dots. They draw the
        /// glyph lazer draws for an osu! map instead, so they look exactly as they do in lazer.
        /// </remarks>
        public static Drawable CreateRulesetGlyph(IRulesetInfo ruleset, IRulesetStore rulesets)
        {
            if (ruleset.OnlineID >= 0 && rulesets.GetRuleset(ruleset.OnlineID) != null)
                return new SpriteIcon { Icon = OsuIcon.RulesetOsu };

            return new SpriteIcon { Icon = FontAwesome.Regular.QuestionCircle };
        }

        ITooltip<DifficultyIconTooltipContent> IHasCustomTooltip<DifficultyIconTooltipContent>.
            GetCustomTooltip() => new DifficultyIconTooltip();

        DifficultyIconTooltipContent IHasCustomTooltip<DifficultyIconTooltipContent>.
            TooltipContent => (TooltipType != DifficultyIconTooltipType.None && beatmap != null ? new DifficultyIconTooltipContent(beatmap, Current, ruleset, mods, TooltipType) : null)!;
    }

    public enum DifficultyIconTooltipType
    {
        /// <summary>
        /// No tooltip.
        /// </summary>
        None,

        /// <summary>
        /// Star rating only.
        /// </summary>
        StarRating,

        /// <summary>
        /// Star rating, OD, HP, CS, AR, length, and max combo.
        /// </summary>
        Extended,
    }
}
