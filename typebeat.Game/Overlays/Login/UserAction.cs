// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Localisation;
using typebeat.Game.Resources.Localisation.Web;
using typebeat.Game.Localisation;

namespace typebeat.Game.Overlays.Login
{
    public enum UserAction
    {
        [LocalisableDescription(typeof(UsersStrings), nameof(UsersStrings.StatusOnline))]
        Online,

        [LocalisableDescription(typeof(LoginPanelStrings), nameof(LoginPanelStrings.DoNotDisturb))]
        DoNotDisturb,

        [LocalisableDescription(typeof(LoginPanelStrings), nameof(LoginPanelStrings.AppearOffline))]
        AppearOffline,

        [LocalisableDescription(typeof(LoginPanelStrings), nameof(LoginPanelStrings.SignOut))]
        SignOut,
    }
}
