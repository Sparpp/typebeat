// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Audio.Sample;
using osu.Framework.Audio.Track;
using typebeat.Game.Beatmaps.ControlPoints;
using typebeat.Game.Graphics.Containers;

namespace typebeat.Game.Screens.Edit.Timing
{
    /// <summary>One editor-wide metronome following the track's timing sections and meter.</summary>
    public partial class EditorMetronome : BeatSyncedContainer
    {
        public event Action<int>? Ticked;

        private Sample? tick;
        private Sample? downbeat;

        [Resolved]
        private EditorTimingSettings settings { get; set; } = null!;

        public override bool HandlePositionalInput => false;
        public override bool HandleNonPositionalInput => false;

        public EditorMetronome()
        {
            // As in osu! lazer, scrubbing to the middle of a beat must not produce a late click.
            AllowMistimedEventFiring = false;
        }

        [BackgroundDependencyLoader]
        private void load(AudioManager audio)
        {
            tick = audio.Samples.Get("Editor/metronomelow");
            downbeat = audio.Samples.Get("Editor/metronomehigh");
        }

        protected override void OnNewBeat(int beatIndex, TimingControlPoint timingPoint, EffectControlPoint effectPoint, ChannelAmplitudes amplitudes)
        {
            base.OnNewBeat(beatIndex, timingPoint, effectPoint, amplitudes);

            if (!IsBeatSyncedWithTrack || !settings.Metronome.Value || BeatSyncSource.ControlPoints?.TimingPoints.Count == 0)
                return;

            int meter = timingPoint.TimeSignature.Numerator;
            int beat = (beatIndex % meter + meter) % meter;
            (beat == 0 ? downbeat : tick)?.Play();
            Ticked?.Invoke(beat);
        }
    }
}
