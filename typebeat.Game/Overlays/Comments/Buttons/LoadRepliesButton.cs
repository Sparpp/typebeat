// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using osu.Framework.Graphics;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Resources.Localisation.Web;

namespace typebeat.Game.Overlays.Comments.Buttons
{
    public partial class LoadRepliesButton : LoadingButton
    {
        private ButtonContent content;

        public LoadRepliesButton()
        {
            AutoSizeAxes = Axes.Both;
        }

        protected override Drawable CreateContent() => content = new ButtonContent();

        protected override void OnLoadStarted() => content.ToggleTextVisibility(false);

        protected override void OnLoadFinished() => content.ToggleTextVisibility(true);

        private partial class ButtonContent : CommentRepliesButton
        {
            public ButtonContent()
            {
                Text = CommentsStrings.LoadReplies;
            }
        }
    }
}
