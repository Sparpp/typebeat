// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Audio;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Input.Events;
using typebeat.Game.Audio.Effects;
using typebeat.Game.Beatmaps;
using typebeat.Game.Configuration;
using typebeat.Game.Graphics;
using typebeat.Game.Overlays;
using typebeat.Game.Rulesets.Edit;
using typebeat.Game.Screens.Edit.Timing;
using osuTK;
using osuTK.Input;

namespace typebeat.Game.Screens.Edit.Compose.Components.Timeline
{
    [Cached]
    public partial class Timeline : ZoomableScrollContainer
    {
        private const float timeline_height = 80;

        private readonly Drawable userContent;

        private bool alwaysShowControlPoints;

        public bool AlwaysShowControlPoints
        {
            get => alwaysShowControlPoints;
            set
            {
                if (value == alwaysShowControlPoints)
                    return;

                alwaysShowControlPoints = value;
                controlPointsVisible.TriggerChange();
            }
        }

        [Resolved]
        private EditorClock editorClock { get; set; } = null!;

        [Resolved]
        private EditorBeatmap editorBeatmap { get; set; } = null!;

        [Resolved]
        private IBindable<WorkingBeatmap> beatmap { get; set; } = null!;

        /// <summary>
        /// The timeline's scroll position in the last frame.
        /// </summary>
        private double lastScrollPosition;

        /// <summary>
        /// The track time in the last frame.
        /// </summary>
        private double lastTrackTime;

        /// <summary>
        /// Whether the user is currently dragging the timeline.
        /// </summary>
        private bool handlingDragInput;

        /// <summary>
        /// Whether the track was playing before a user drag event.
        /// </summary>
        private bool trackWasPlaying;

        /// <summary>
        /// The timeline zoom level at a 1x zoom scale.
        /// </summary>
        private float defaultTimelineZoom;

        /// <summary>
        /// The waveform as the map will SOUND (see <see cref="updateWaveformGain"/>): the same peaks,
        /// scaled by the map's own gain. It is clipped, which is the point rather than a detail - a peak
        /// the amplifier pushes past full scale is drawn flat against the edge instead of running off the
        /// timeline, so a gain that clips is legible here before it is audible.
        /// </summary>
        /// <remarks>
        /// <para>The clip is the TIMELINE'S OWN masking (a scroll container masks to its bounds), whose
        /// height is exactly the strip the graph sits in, and deliberately not a masking container
        /// wrapped around the graph. The framework's waveform draw node walks every point inside the
        /// innermost masking container's bounds, so a clip around the graph, which spans the whole
        /// ZOOMED content, made it emit a quad for every point of the song on every frame whether it
        /// was on screen or not: 29k quads at the default zoom and the whole 183k of a 3 minute song
        /// from half zoom inwards, around 45 ms of draw thread work a frame before a single vertex
        /// reached the GPU. Clipped by the timeline, it walks only the points across the visible
        /// width.</para>
        ///
        /// <para>One visible difference to the old clip: the graph is shifted
        /// <see cref="Editor.WAVEFORM_VISUAL_OFFSET"/> earlier, and that sliver now shows left of the
        /// zero marker when the view is at the very start, as it did before the gain display.</para>
        /// </remarks>
        private WaveformGraph waveform = null!;

        private double appliedWaveformGain = -1;

        private TimelineTimingChangeDisplay controlPoints = null!;

        private Bindable<float> waveformOpacity = null!;
        private Bindable<bool> controlPointsVisible = null!;

        private float? waveformOpacityOverride;

        /// <summary>
        /// When set, overrides the user's <see cref="OsuSetting.EditorWaveformOpacity"/> for this
        /// timeline instance (screens where the waveform is the primary reading surface).
        /// </summary>
        public float? WaveformOpacityOverride
        {
            get => waveformOpacityOverride;
            set
            {
                waveformOpacityOverride = value;

                if (IsLoaded)
                    updateWaveformOpacity();
            }
        }

        private double trackLengthForZoom;

        public Timeline(Drawable userContent)
        {
            this.userContent = userContent;

            RelativeSizeAxes = Axes.X;
            Height = timeline_height;

            ZoomDuration = 200;
            ZoomEasing = Easing.OutQuint;
            ScrollbarVisible = false;
        }

        [BackgroundDependencyLoader]
        private void load(OsuColour colours, OverlayColourProvider colourProvider, OsuConfigManager config)
        {
            CentreMarker centreMarker;

            // We don't want the centre marker to scroll
            AddInternal(centreMarker = new CentreMarker
            {
                Anchor = Anchor.TopCentre,
                Origin = Anchor.TopCentre,
                Width = 8,
                TriangleHeightRatio = 0.8f,
                Colour = colourProvider.Colour2
            });

            AddRange(new Drawable[]
            {
                new Box
                {
                    Name = "zero marker",
                    RelativeSizeAxes = Axes.Y,
                    Width = 1.5f,
                    Origin = Anchor.TopCentre,
                    Colour = colourProvider.Background1,
                },
                controlPoints = new TimelineTimingChangeDisplay
                {
                    RelativeSizeAxes = Axes.Both,
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                },
                new Container
                {
                    RelativeSizeAxes = Axes.X,
                    Height = timeline_height,
                    Children = new[]
                    {
                        // No masking container around the graph: see the remarks on waveform.
                        waveform = new TimelineWaveformGraph
                        {
                            RelativeSizeAxes = Axes.Both,
                            // Scalings happen about the graph's VERTICAL CENTRE, because that is where
                            // the waveform is drawn from: a gain has to grow it both ways, not just
                            // downwards.
                            Anchor = Anchor.Centre,
                            Origin = Anchor.Centre,
                            BaseColour = colours.Blue.Opacity(0.2f),
                            LowColour = colours.BlueLighter,
                            MidColour = colours.BlueDark,
                            HighColour = colours.BlueDarker,
                        },
                        centreMarker.CreateProxy(),
                        userContent,
                    }
                },
            });

            AddInternal(new TimingGrid(() =>
            {
                return (TimeAtPosition(Content.ToLocalSpace(ScreenSpaceDrawQuad.TopLeft).X),
                    TimeAtPosition(Content.ToLocalSpace(ScreenSpaceDrawQuad.TopRight).X));
            }, optional: false));

            waveformOpacity = config.GetBindable<float>(OsuSetting.EditorWaveformOpacity);
            controlPointsVisible = config.GetBindable<bool>(OsuSetting.EditorTimelineShowTimingChanges);

            editorClock.TrackChanged += updateWaveform;

            // The vocals-stem toggle (backlog 392) is a VIEW-ONLY swap: only the graph's waveform
            // source moves, never the track or the editor clock.
            timingSettings.VocalsWaveform.BindValueChanged(_ => updateWaveform());
            updateWaveform();

            Zoom = (float)(defaultTimelineZoom * editorBeatmap.TimelineZoom);
        }

        private void updateWaveform()
        {
            // VIEW-ONLY: the toggle picks WHICH waveform the graph draws (the map's full mix or the
            // isolated vocals stem). The track, the editor clock and everything downstream of them
            // are untouched, so playback and timing still run on the full mix. A map with no stem,
            // or a toggle asked for on one whose stem will not decode, falls back to the full mix.
            var vocalsStem = timingSettings.VocalsWaveform.Value ? beatmap.Value.VocalsStemWaveform : null;
            waveform.Waveform = vocalsStem ?? beatmap.Value.Waveform;
            Scheduler.AddOnce(applyVisualOffset, beatmap);
        }

        /// <summary>
        /// Draws the waveform AT THE GAIN THE MAP WILL PLAY AT (see
        /// <see cref="BeatmapMetadata.AudioGain"/> and <see cref="Audio.Effects.AudioGain"/>), so the
        /// peaks on the timeline are the peaks the player will hear rather than the ones inside the
        /// audio file: a mapper raising the gain can watch the take get louder and see where it starts
        /// running past the top, which is the same reading the gain bar's own indicator gives them in
        /// words. Honest about its limits: this is a vertical scale of the same analysis, so what it
        /// shows past the edges is clipped because the amplifier clips it, not because the waveform was
        /// re-measured after the boost.
        /// </summary>
        private void updateWaveformGain()
        {
            double gain = beatmap.Value?.Metadata.AudioGain ?? BeatmapMetadata.DEFAULT_AUDIO_GAIN;

            if (gain == appliedWaveformGain)
                return;

            appliedWaveformGain = gain;
            waveform.Scale = new Vector2(1, (float)gain);
        }

        private void applyVisualOffset(IBindable<WorkingBeatmap> beatmap)
        {
            waveform.RelativePositionAxes = Axes.X;

            if (beatmap.Value.Track.Length > 0)
                waveform.X = -(float)(Editor.WAVEFORM_VISUAL_OFFSET / beatmap.Value.Track.Length);
            else
            {
                // sometimes this can be the case immediately after a track switch.
                // reschedule with the hope that the track length eventually populates.
                Scheduler.AddOnce(applyVisualOffset, beatmap);
            }
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            waveformOpacity.BindValueChanged(_ => updateWaveformOpacity(), true);

            controlPointsVisible.BindValueChanged(visible =>
            {
                if (visible.NewValue || alwaysShowControlPoints)
                    controlPoints.FadeIn(400, Easing.OutQuint);
                else
                    controlPoints.FadeOut(200, Easing.OutQuint);
            }, true);
        }

        private void updateWaveformOpacity() =>
            waveform.FadeTo(waveformOpacityOverride ?? waveformOpacity.Value, 200, Easing.OutQuint);

        protected override void Update()
        {
            base.Update();

            // The extrema of track time should be positioned at the centre of the container when scrolled to the start or end
            Content.Margin = new MarginPadding { Horizontal = DrawWidth / 2 };

            // This needs to happen after transforms are updated, but before the scroll position is updated in base.UpdateAfterChildren
            if (editorClock.IsRunning)
                scrollToTrackTime();

            // Read every frame rather than bound: the gain is a plain value on the beatmap's metadata,
            // which the mapper moves from the setup screen, so the timeline follows the bar live.
            updateWaveformGain();

            if (editorClock.TrackLength != trackLengthForZoom)
            {
                defaultTimelineZoom = getZoomLevelForVisibleMilliseconds(6000);

                float minimumZoom = getZoomLevelForVisibleMilliseconds(10000);
                float maximumZoom = getZoomLevelForVisibleMilliseconds(500);

                float initialZoom = (float)Math.Clamp(defaultTimelineZoom * (editorBeatmap.TimelineZoom == 0 ? 1 : editorBeatmap.TimelineZoom), minimumZoom, maximumZoom);

                SetupZoom(initialZoom, minimumZoom, maximumZoom);

                float getZoomLevelForVisibleMilliseconds(double milliseconds) => Math.Max(1, (float)(editorClock.TrackLength / milliseconds));

                trackLengthForZoom = editorClock.TrackLength;
            }
        }

        protected override bool OnScroll(ScrollEvent e)
        {
            // if this is not a precision scroll event, let the editor handle the seek itself (for snapping support)
            if (!e.AltPressed && !e.IsPrecise)
                return false;

            return base.OnScroll(e);
        }

        protected override void OnZoomChanged()
        {
            base.OnZoomChanged();
            editorBeatmap.TimelineZoom = Zoom / defaultTimelineZoom;
        }

        protected override void UpdateAfterChildren()
        {
            base.UpdateAfterChildren();

            if (handlingDragInput)
                seekTrackToCurrent();
            else if (!editorClock.IsRunning)
            {
                // The track isn't running. There are three cases we have to be wary of:
                // 1) The user flick-drags on this timeline and we are applying an interpolated seek on the clock, until interrupted by 2 or 3.
                // 2) The user changes the track time through some other means (scrolling in the editor or overview timeline; clicking a hitobject etc.). We want the timeline to track the clock's time.
                // 3) An ongoing seek transform is running from an external seek. We want the timeline to track the clock's time.

                // The simplest way to cover the first two cases is by checking whether the scroll position has changed and the audio hasn't been changed externally
                // Checking IsSeeking covers the third case, where the transform may not have been applied yet.
                if (Current != lastScrollPosition && editorClock.CurrentTime == lastTrackTime && !editorClock.IsSeeking)
                    seekTrackToCurrent();
                else
                    scrollToTrackTime();
            }

            lastScrollPosition = Current;
            lastTrackTime = editorClock.CurrentTime;
        }

        private void seekTrackToCurrent()
        {
            double time = Math.Clamp(TimeAtPosition(Current), 0, editorClock.TrackLength);

            // A held drag also updates on stationary frames. Avoid repeatedly queueing the same
            // audio seek while preserving every changed scrub position.
            if (double.IsFinite(time) && time != editorClock.CurrentTime)
                editorClock.Seek(time);
        }

        private void scrollToTrackTime()
        {
            if (editorClock.TrackLength == 0)
                return;

            // covers the case where the user starts playback after a drag is in progress.
            // we want to ensure the clock is always stopped during drags to avoid weird audio playback.
            if (handlingDragInput)
                editorClock.Stop();

            float position = PositionAtTime(editorClock.CurrentTime);
            ScrollTo(position, false);
        }

        protected override bool OnMouseDown(MouseDownEvent e)
        {
            if (base.OnMouseDown(e))
                beginUserDrag();

            // handling right button as well breaks context menus inside the timeline, only handle left button for now.
            return e.Button == MouseButton.Left;
        }

        protected override void OnMouseUp(MouseUpEvent e)
        {
            endUserDrag();
            base.OnMouseUp(e);
        }

        private void beginUserDrag()
        {
            handlingDragInput = true;
            trackWasPlaying = editorClock.IsRunning;
            editorClock.Stop();
        }

        private void endUserDrag()
        {
            handlingDragInput = false;
            editorClock.Seek(timingSettings.Snap(editorClock.CurrentTime, editorBeatmap.ControlPointInfo, beatDivisor.Value));

            if (trackWasPlaying)
                editorClock.Start();
        }

        [Resolved]
        private IBeatSnapProvider beatSnapProvider { get; set; } = null!;

        [Resolved]
        private EditorTimingSettings timingSettings { get; set; } = null!;

        [Resolved]
        private BindableBeatDivisor beatDivisor { get; set; } = null!;

        /// <summary>
        /// The total amount of time visible on the timeline.
        /// </summary>
        public double VisibleRange => editorClock.TrackLength / CurrentZoom.Value;

        public double TimeAtPosition(double x)
        {
            return x / Content.DrawWidth * editorClock.TrackLength;
        }

        public float PositionAtTime(double time)
        {
            return (float)(time / editorClock.TrackLength * Content.DrawWidth);
        }

        public SnapResult FindSnappedPositionAndTime(Vector2 screenSpacePosition)
        {
            double time = TimeAtPosition(Content.ToLocalSpace(screenSpacePosition).X);
            return new SnapResult(screenSpacePosition, beatSnapProvider.SnapTime(time));
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            if (editorClock.IsNotNull())
                editorClock.TrackChanged -= updateWaveform;
        }
    }
}
