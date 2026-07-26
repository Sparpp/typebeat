// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using typebeat.Game.Graphics;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Overlays;

namespace typebeat.Game.Screens.Edit.Components.Timelines.Summary
{
    /// <summary>
    /// The bottom bar's ruleset slot, immediately left of the Test button: a button driven entirely
    /// by <see cref="EditorRulesetAction"/>. It is zero-width (and so invisible in the layout) until
    /// the active ruleset's compose screen publishes something into it.
    /// Styled as a quieter sibling of <see cref="TestGameplayButton"/>: same flat, square, full-height
    /// shape, blue instead of orange, and it turns red while the published action is armed.
    /// </summary>
    public partial class RulesetActionButton : OsuButton
    {
        /// <summary>Occupied width. Matches the Test button so the two read as a pair.</summary>
        public const float OCCUPIED_WIDTH = 110;

        [Resolved]
        private OsuColour colours { get; set; } = null!;

        [Resolved]
        private EditorRulesetAction rulesetAction { get; set; } = null!;

        protected override SpriteText CreateText() => new OsuSpriteText
        {
            Depth = -1,
            Origin = Anchor.Centre,
            Anchor = Anchor.Centre,
            Font = OsuFont.TorusAlternate.With(weight: FontWeight.Light, size: 24),
            Shadow = false,
        };

        public RulesetActionButton()
        {
            RelativeSizeAxes = Axes.Y;
            // OsuButton's own constructor sets Height = 40 as an absolute pixel value (its usual,
            // non-relative sizing mode). Switching to RelativeSizeAxes.Y here reinterprets that same
            // stored value as a fraction of the parent's height instead, so it must be reset to a
            // proper 100% or the button balloons to 40x its parent's height, pushing the label text
            // far outside the bottom bar's masked/clipped area (invisible, but never zero-sized).
            Height = 1;
            Width = 0;
        }

        [BackgroundDependencyLoader]
        private void load(OverlayColourProvider colourProvider)
        {
            SpriteText.Colour = colourProvider.Background6;
            Content.CornerRadius = 0;
            Action = () => rulesetAction.Activated?.Invoke();
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            rulesetAction.Visible.BindValueChanged(v =>
            {
                // Zero width collapses the auto-sized grid column, handing the space back to the
                // summary timeline for every ruleset that publishes nothing.
                Width = v.NewValue ? OCCUPIED_WIDTH : 0;
                Alpha = v.NewValue ? 1 : 0;
            }, true);

            rulesetAction.Text.BindValueChanged(t => Text = t.NewValue, true);
            rulesetAction.Armed.BindValueChanged(_ => updateColour(), true);
        }

        private void updateColour() => BackgroundColour = rulesetAction.Armed.Value ? colours.Red1 : colours.Blue1;

        protected override bool OnMouseDown(MouseDownEvent e)
        {
            Background.FadeColour(rulesetAction.Armed.Value ? colours.Red0 : colours.Blue0, 500, Easing.OutQuint);
            // don't call base in order to block scale animation (matches the Test button)
            return false;
        }

        protected override void OnMouseUp(MouseUpEvent e)
        {
            Background.FadeColour(rulesetAction.Armed.Value ? colours.Red1 : colours.Blue1, 300, Easing.OutQuint);
            // don't call base in order to block scale animation (matches the Test button)
        }
    }
}
