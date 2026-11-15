// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Specialized;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using typebeat.Game.Rulesets.UI;
using typebeat.Game.Skinning;

namespace typebeat.Game.Screens.Play.HUD
{
    /// <summary>
    /// A flowing display of all gameplay keys. Individual keys can be added using <see cref="InputTrigger"/> implementations.
    /// </summary>
    public abstract partial class KeyCounterDisplay : Container, ISerialisableDrawable
    {
        /// <summary>
        /// Whether the key counter is shown. This is true by default, but can be changed; in gameplay it
        /// follows whether a replay is loaded, so the counter shows when watching a replay only.
        /// </summary>
        /// <remarks>
        /// osu! could also show it in gameplay through a setting ("KeyOverlay", off by default). That
        /// setting had no control in type!beat and was removed in backlog 483; the component itself stays,
        /// because saved skin layouts can name it.
        /// </remarks>
        public Bindable<bool> AlwaysVisible { get; } = new Bindable<bool>(true);

        protected abstract FillFlowContainer<KeyCounter> KeyFlow { get; }

        private readonly IBindableList<InputTrigger> triggers = new BindableList<InputTrigger>();

        protected override Container<Drawable> Content { get; } = new Container
        {
            Alpha = 0,
            AutoSizeAxes = Axes.Both,
        };

        [Resolved]
        private InputCountController controller { get; set; } = null!;

        private const int duration = 100;

        protected KeyCounterDisplay()
        {
            AddInternal(Content);
        }

        [BackgroundDependencyLoader]
        private void load(DrawableRuleset? drawableRuleset)
        {
            AutoSizeAxes = Axes.Both;

            if (drawableRuleset != null)
                AlwaysVisible.BindTo(drawableRuleset.HasReplayLoaded);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            triggers.BindTo(controller.Triggers);
            triggers.BindCollectionChanged(triggersChanged, true);

            AlwaysVisible.BindValueChanged(_ => UpdateVisibility(), true);
        }

        protected void UpdateVisibility()
        {
            bool visible = AlwaysVisible.Value;

            // Isolate changing visibility of the key counters from fading this component.
            Content.FadeTo(visible ? 1 : 0, duration);

            // Ensure a valid size is immediately obtained even if partially off-screen
            // See https://github.com/ppy/osu/issues/14793.
            Content.AlwaysPresent = visible;
        }

        protected abstract KeyCounter CreateCounter(InputTrigger trigger);

        private void triggersChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            KeyFlow.Clear();
            foreach (var trigger in controller.Triggers)
                KeyFlow.Add(CreateCounter(trigger));
        }

        public bool UsesFixedAnchor { get; set; }
    }
}
