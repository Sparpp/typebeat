// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Bindables;
using osu.Framework.Graphics.UserInterface;
using typebeat.Game.Graphics.UserInterface;

namespace typebeat.Game.Screens.Edit.Timing
{
    /// <summary>
    /// The editor's timing grid controls, as entries of the top bar's Timing menu: BPM / Offset, the
    /// grid subdivision, and the Overlay Grid, Snap to Grid, Snap to Caret and Metronome toggles. They
    /// used to be a row of buttons above the timeline on every timeline screen; the menu holds the same
    /// state (<see cref="EditorTimingSettings"/> and the editor's <see cref="BindableBeatDivisor"/>), so
    /// every screen still reads them live. The Vocals Waveform toggle is built here too, beside the
    /// settings it binds, but it is a view of the waveform rather than a timing control, so the editor
    /// puts it in the View menu (<see cref="VocalsWaveform"/>).
    /// </summary>
    public class TimingMenuItems
    {
        public const string BPM_OFFSET = "BPM / Offset";
        public const string GRID_SUBDIVISION = "Grid subdivision";
        public const string OVERLAY_GRID = "Overlay grid";
        public const string SNAP_TO_GRID = "Snap to grid";
        public const string SNAP_TO_CARET = "Snap to caret";
        public const string METRONOME = "Metronome";
        public const string VOCALS_WAVEFORM = "Vocals waveform";

        /// <summary>The entries, in menu order.</summary>
        public IReadOnlyList<MenuItem> Items { get; }

        /// <summary>
        /// The Vocals Waveform toggle, shown in the View menu rather than in <see cref="Items"/>. It only
        /// works for a map that carries a vocals stem (backlog 392); see <see cref="SetVocalsStemAvailable"/>.
        /// </summary>
        public ToggleMenuItem VocalsWaveform { get; }

        private readonly EditorTimingSettings settings;
        private readonly BindableBeatDivisor divisor = new BindableBeatDivisor();
        private readonly BindableInt multiplier = new BindableInt(1);
        private readonly MenuItem subdivision;
        private readonly List<(int Option, TernaryStateRadioMenuItem Item)> subdivisionOptions = new List<(int, TernaryStateRadioMenuItem)>();

        public TimingMenuItems(EditorTimingSettings settings, BindableBeatDivisor editorDivisor, Action openTimingScreen)
        {
            this.settings = settings;
            divisor.BindTo(editorDivisor);
            multiplier.BindTo(settings.BeatMultiplier);

            // Negative options are beat MULTIPLES (-4 = a whole note, -2 = a half note), the same
            // encoding the toolbar dropdown used; the current divisor is offered even when it is not
            // one of the predefined ones, so a map saved on an unusual divisor still shows its value.
            var options = new[] { -4, -2 }.Concat(BindableBeatDivisor.PREDEFINED_DIVISORS).Append(divisor.Value).Distinct().ToArray();

            foreach (int option in options)
            {
                var item = new TernaryStateRadioMenuItem(NoteName(option), MenuItemType.Standard, _ => apply(option));
                subdivisionOptions.Add((option, item));
            }

            subdivision = new MenuItem(GRID_SUBDIVISION)
            {
                Items = subdivisionOptions.Select(o => (MenuItem)o.Item).ToArray(),
            };

            divisor.BindValueChanged(_ => syncSubdivision());
            multiplier.BindValueChanged(_ => syncSubdivision());
            syncSubdivision();

            VocalsWaveform = new ToggleMenuItem(VOCALS_WAVEFORM) { State = { BindTarget = settings.VocalsWaveform } };

            Items = new MenuItem[]
            {
                new OsuMenuItem(BPM_OFFSET, MenuItemType.Standard, openTimingScreen),
                subdivision,
                new ToggleMenuItem(OVERLAY_GRID) { State = { BindTarget = settings.ShowGrid } },
                new ToggleMenuItem(SNAP_TO_GRID) { State = { BindTarget = settings.SnapToGrid } },
                new ToggleMenuItem(SNAP_TO_CARET) { State = { BindTarget = settings.SnapToCaret } },
                new ToggleMenuItem(METRONOME) { State = { BindTarget = settings.Metronome } },
            };
        }

        /// <summary>
        /// Enables the Vocals Waveform toggle only for a map that carries a vocals stem. A setting left
        /// on by a stem-carrying map is cleared for one without, so the toggle never reads on while the
        /// timeline actually draws the full mix.
        /// </summary>
        public void SetVocalsStemAvailable(bool available)
        {
            if (!available)
                settings.VocalsWaveform.Value = false;

            VocalsWaveform.Action.Disabled = !available;
        }

        /// <summary>The option the grid is on now: a negative beat multiple, or the divisor.</summary>
        public int CurrentSubdivision => multiplier.Value > 1 ? -multiplier.Value : divisor.Value;

        public static string NoteName(int option) => option < 0 ? $"1/{4 / -option} note" : $"1/{option * 4} note";

        private void apply(int option)
        {
            multiplier.Value = option < 0 ? -option : 1;
            divisor.SetArbitraryDivisor(option < 0 ? 1 : option);
            syncSubdivision();
        }

        private void syncSubdivision()
        {
            int current = CurrentSubdivision;

            foreach (var (option, item) in subdivisionOptions)
                item.State.Value = option == current ? TernaryState.True : TernaryState.False;

            subdivision.Text.Value = $"{GRID_SUBDIVISION}: {NoteName(current)}";
        }
    }
}
