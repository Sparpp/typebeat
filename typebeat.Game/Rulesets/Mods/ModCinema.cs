// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using typebeat.Game.Graphics;
using typebeat.Game.Rulesets.Objects;
using typebeat.Game.Rulesets.UI;
using typebeat.Game.Screens.Play;

namespace typebeat.Game.Rulesets.Mods
{
    public abstract class ModCinema<T> : ModCinema, IApplicableToDrawableRuleset<T>
        where T : HitObject
    {
        public virtual void ApplyToDrawableRuleset(DrawableRuleset<T> drawableRuleset)
        {
            // AlwaysPresent required for hitsounds
            drawableRuleset.AlwaysPresent = true;
            drawableRuleset.Hide();
        }
    }

    public class ModCinema : ModAutoplay, IApplicableToHUD, IApplicableToPlayer, IApplicableFailOverride
    {
        public override string Name => "Cinema";
        public override string Acronym => "CN";
        public override IconUsage? Icon => OsuIcon.ModCinema;
        public override LocalisableString Description => "Watch the video without visual distractions.";

        public override Type[] IncompatibleMods => base.IncompatibleMods.Concat(new[] { typeof(ModAutoplay), typeof(ModNoFail), typeof(ModFailCondition) }).ToArray();

        public void ApplyToHUD(HUDOverlay overlay)
        {
            overlay.ShowHud.Value = false;
            overlay.ShowHud.Disabled = true;

            overlay.PlayfieldSkinLayer.Hide();
        }

        public void ApplyToPlayer(Player player)
        {
            player.ApplyToBackground(b => b.IgnoreUserSettings.Value = true);
            player.DimmableStoryboard.IgnoreUserSettings.Value = true;

            player.BreakOverlay.Hide();
            (player as ReplayPlayer)?.ReplayOverlay.Hide();
        }

        public bool PerformFail() => false;

        public bool RestartOnFail => false;
    }
}
