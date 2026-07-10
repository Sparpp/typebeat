// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using typebeat.Game.Overlays;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Overlays.Mods;
using typebeat.Game.Rulesets.Mods;

namespace typebeat.Game.Screens.OnlinePlay
{
    public partial class FreeModSelectOverlay : ModSelectOverlay
    {
        protected override bool AllowCustomisation => false;

        public new Func<Mod, bool> IsValidMod
        {
            get => base.IsValidMod;
            set => base.IsValidMod = m => m.UserPlayable && value.Invoke(m);
        }

        protected override SelectAllModsButton? SelectAllModsButton => DisplayedFooterContent?.SelectAllModsButton;

        public FreeModSelectOverlay()
            : base(OverlayColourScheme.Plum)
        {
            IsValidMod = _ => true;
        }

        protected override ModColumn CreateModColumn(ModType modType) => new ModColumn(modType, true);

        public new FreeModSelectFooterContent? DisplayedFooterContent => base.DisplayedFooterContent as FreeModSelectFooterContent;

        public override VisibilityContainer CreateFooterContent() => new FreeModSelectFooterContent(this)
        {
            Beatmap = { BindTarget = Beatmap },
            ActiveMods = { BindTarget = ActiveMods },
            Ruleset = { BindTarget = Ruleset },
        };

        public partial class FreeModSelectFooterContent : ModSelectFooterContent
        {
            private readonly FreeModSelectOverlay overlay;

            protected override bool ShowModEffects => false;

            public SelectAllModsButton? SelectAllModsButton;

            public FreeModSelectFooterContent(FreeModSelectOverlay overlay)
                : base(overlay)
            {
                this.overlay = overlay;
            }

            protected override IEnumerable<ShearedButton> CreateButtons()
                => base.CreateButtons()
                       .Prepend(SelectAllModsButton = new SelectAllModsButton(overlay)
                       {
                           Anchor = Anchor.BottomLeft,
                           Origin = Anchor.BottomLeft,
                       });
        }
    }
}
