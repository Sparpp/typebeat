// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Bindables;
using typebeat.Game.Graphics;
using typebeat.Game.Online.API;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Skinning;
using osuTK.Graphics;

namespace typebeat.Game.Screens.Menu
{
    public partial class MenuLogoVisualisation : LogoVisualisation
    {
        private IBindable<APIUser> user = null!;
        private Bindable<Skin> skin = null!;

        private OsuColour colours = null!;

        [BackgroundDependencyLoader]
        private void load(IAPIProvider api, SkinManager skinManager, OsuColour colours)
        {
            this.colours = colours;

            user = api.LocalUser.GetBoundCopy();
            skin = skinManager.CurrentSkin.GetBoundCopy();

            user.ValueChanged += _ => UpdateColour();
            skin.BindValueChanged(_ => UpdateColour(), true);
        }

        protected virtual void UpdateColour()
        {
            if (user.Value?.IsSupporter ?? false)
                Colour = skin.Value.GetConfig<GlobalSkinColours, Color4>(GlobalSkinColours.MenuGlow)?.Value ?? Color4.White;
            else
            {
                // type!beat: the bars carry the brand lime (the cookie itself went charcoal).
                // `Pink` holds the Caret accent — see the note in OsuColour.
                Colour = colours.Pink;
            }
        }
    }
}
