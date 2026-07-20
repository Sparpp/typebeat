// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Platform;
using typebeat.Game.Configuration;
using typebeat.Game.Localisation;
using typebeat.Game.Online.API;
using typebeat.Game.Overlays;
using typebeat.Game.Overlays.Dialog;
using typebeat.Game.Overlays.Notifications;
using WebCommonStrings = typebeat.Game.Resources.Localisation.Web.CommonStrings;

namespace typebeat.Game.Online.Chat
{
    public partial class ExternalLinkOpener : Component
    {
        [Resolved]
        private GameHost host { get; set; } = null!;

        [Resolved]
        private OsuGame? game { get; set; }

        [Resolved]
        private IDialogOverlay? dialogOverlay { get; set; }

        [Resolved]
        private INotificationOverlay? notificationOverlay { get; set; }

        [Resolved]
        private IAPIProvider api { get; set; } = null!;

        private Bindable<bool> externalLinkWarning = null!;

        [BackgroundDependencyLoader(true)]
        private void load(OsuConfigManager config)
        {
            externalLinkWarning = config.GetBindable<bool>(OsuSetting.ExternalLinkWarning);
        }

        public void OpenUrlExternally(string url, LinkWarnMode warnMode = LinkWarnMode.Default)
        {
            bool isTrustedDomain;

            if (url.StartsWith('/'))
            {
                url = $"{api.Endpoints.WebsiteUrl}{url}";
                isTrustedDomain = true;
            }
            else
            {
                isTrustedDomain = TrustedDomains.IsTrustedUrl(url, api.Endpoints);
            }

            if (!url.CheckIsValidUrl())
            {
                notificationOverlay?.Post(new SimpleErrorNotification
                {
                    Text = NotificationsStrings.UnsupportedOrDangerousUrlProtocol(url),
                });

                return;
            }

            bool shouldWarn;

            switch (warnMode)
            {
                case LinkWarnMode.Default:
                    shouldWarn = externalLinkWarning.Value && !isTrustedDomain;
                    break;

                case LinkWarnMode.AlwaysWarn:
                    shouldWarn = true;
                    break;

                case LinkWarnMode.NeverWarn:
                    shouldWarn = false;
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(warnMode), warnMode, null);
            }

            if (dialogOverlay != null && shouldWarn)
                dialogOverlay.Push(new ExternalLinkDialog(url, () => host.OpenUrlExternally(url), () => game?.CopyToClipboard(url)));
            else
                host.OpenUrlExternally(url);
        }

        public partial class ExternalLinkDialog : PopupDialog
        {
            public ExternalLinkDialog(string url, Action openExternalLinkAction, Action copyExternalLinkAction)
            {
                HeaderText = DialogStrings.CautionHeaderText;
                BodyText = DialogStrings.ExternalLinkBodyText(url);

                Icon = FontAwesome.Solid.ExclamationTriangle;

                Buttons = new PopupDialogButton[]
                {
                    new PopupDialogOkButton
                    {
                        Text = DialogStrings.ExternalLinkOkButton,
                        Action = openExternalLinkAction
                    },
                    new PopupDialogCancelButton
                    {
                        Text = CommonStrings.CopyLink,
                        Action = copyExternalLinkAction
                    },
                    new PopupDialogCancelButton
                    {
                        Text = WebCommonStrings.ButtonsCancel,
                    },
                };
            }
        }
    }
}
