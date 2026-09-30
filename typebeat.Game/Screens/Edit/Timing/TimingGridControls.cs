// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Localisation;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Graphics.UserInterfaceV2;
using osuTK;

namespace typebeat.Game.Screens.Edit.Timing
{
    public partial class TimingGridControls : FillFlowContainer
    {
        private readonly BindableBeatDivisor currentDivisor = new BindableBeatDivisor();
        private readonly BindableInt currentMultiplier = new BindableInt(1);

        [BackgroundDependencyLoader]
        private void load(EditorTimingSettings settings, BindableBeatDivisor divisor, Editor? editor)
        {
            currentDivisor.BindTo(divisor);
            currentMultiplier.BindTo(settings.BeatMultiplier);

            RelativeSizeAxes = Axes.X;
            Height = 36;
            Direction = FillDirection.Horizontal;
            Spacing = new Vector2(8, 0);
            Padding = new MarginPadding { Horizontal = 8, Vertical = 3 };

            var subdivision = new NoteDropdown
            {
                Width = 190,
                Items = new[] { -4, -2 }.Concat(BindableBeatDivisor.PREDEFINED_DIVISORS).Append(currentDivisor.Value).Distinct(),
            };
            subdivision.Current.Value = currentMultiplier.Value > 1 ? -currentMultiplier.Value : currentDivisor.Value;
            subdivision.Current.BindValueChanged(v =>
            {
                currentMultiplier.Value = v.NewValue < 0 ? -v.NewValue : 1;
                currentDivisor.SetArbitraryDivisor(v.NewValue < 0 ? 1 : v.NewValue);
            });
            void sync() => subdivision.Current.Value = currentMultiplier.Value > 1 ? -currentMultiplier.Value : currentDivisor.Value;
            currentDivisor.BindValueChanged(_ => sync());
            currentMultiplier.BindValueChanged(_ => sync());

            AddRange(new Drawable[]
            {
                new RoundedButton
                {
                    Text = "BPM / Offset",
                    Width = 140,
                    Height = 30,
                    Action = () => { if (editor != null) editor.Mode.Value = EditorScreenMode.Timing; },
                },
                subdivision,
                new GridToggle("Overlay Grid", settings.ShowGrid),
                new GridToggle("Snap to Grid", settings.SnapToGrid),
                new GridToggle("Snap to Caret", settings.SnapToCaret),
                new GridToggle("Metronome", settings.Metronome),
            });
        }

        private partial class NoteDropdown : OsuDropdown<int>
        {
            protected override DropdownHeader CreateHeader() => new NoteDropdownHeader();

            private partial class NoteDropdownHeader : OsuDropdownHeader
            {
                public NoteDropdownHeader()
                {
                    Height = 30;
                    Margin = default;
                    Foreground.Padding = new MarginPadding { Horizontal = 10, Vertical = 5 };
                }
            }

            protected override LocalisableString GenerateItemText(int item)
                => item < 0 ? $"1/{4 / -item} note" : $"1/{item * 4} note";
        }

        private partial class GridToggle : RoundedButton
        {
            private readonly string label;
            private readonly BindableBool value;

            public GridToggle(string label, BindableBool value)
            {
                this.label = label;
                this.value = new BindableBool();
                this.value.BindTo(value);
                Width = 170;
                Height = 30;
                Action = this.value.Toggle;
            }

            protected override void LoadComplete()
            {
                base.LoadComplete();
                value.BindValueChanged(v => Text = $"{label}: {(v.NewValue ? "On" : "Off")}", true);
            }
        }
    }
}
