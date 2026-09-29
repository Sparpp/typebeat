// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using typebeat.Game.Beatmaps;
using typebeat.Game.Graphics;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Rulesets;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Screens.Play.HUD;
using osuTK;

namespace typebeat.Game.Overlays.Mods
{
    internal partial class IncompatibilityDisplayingTooltip : ModButtonTooltip
    {
        private readonly OsuSpriteText incompatibleText;

        private readonly Bindable<IReadOnlyList<Mod>> incompatibleMods = new Bindable<IReadOnlyList<Mod>>();

        [Resolved]
        private Bindable<RulesetInfo> ruleset { get; set; } = null!;

        /// <summary>The game's current beatmap, which the song select mod overlay is showing mods for.</summary>
        [Resolved(canBeNull: true)]
        private IBindable<WorkingBeatmap>? beatmap { get; set; }

        private readonly ModDisplay incompatibleDisplay;

        public IncompatibilityDisplayingTooltip(OverlayColourProvider colourProvider)
            : base(colourProvider)
        {
            AddRange(new Drawable[]
            {
                incompatibleText = new OsuSpriteText
                {
                    Margin = new MarginPadding { Top = 5 },
                    Colour = colourProvider.Content2,
                    Font = OsuFont.GetFont(weight: FontWeight.Regular),
                    Text = "Incompatible with:"
                },
                incompatibleDisplay = new ModDisplay
                {
                    Current = incompatibleMods,
                    ExpansionMode = ExpansionMode.AlwaysExpanded,
                    Scale = new Vector2(0.7f)
                }
            });
        }

        protected override void UpdateDisplay(Mod mod)
        {
            base.UpdateDisplay(mod);

            var incompatibleTypes = mod.IncompatibleMods;

            var allMods = ruleset.Value.CreateInstance().AllMods;

            incompatibleMods.Value = allMods.Where(m => m.GetType() != mod.GetType() && incompatibleTypes.Any(t => t.IsInstanceOfType(m))).Select(m => m.CreateInstance()).ToList();
            incompatibleText.Text = incompatibleMods.Value.Any() ? "Incompatible with:" : "Compatible with all mods";
            incompatibleDisplay.Show();

            // A mod the beatmap cannot carry (backlog 331) says why instead: that is the reason it is greyed.
            if (beatmap != null && !mod.IsAvailableFor(beatmap.Value))
            {
                incompatibleText.Text = mod.UnavailableReason;
                incompatibleDisplay.Hide();
            }
        }
    }
}
