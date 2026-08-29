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
using typebeat.Game.Beatmaps;
using typebeat.Game.Configuration;
using typebeat.Game.Graphics;
using typebeat.Game.Overlays;
using typebeat.Game.Rulesets.Edit;
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
        /// Whether the last seek this timeline drove was DISPLACED by <see cref="MagnetToBeatGrid"/>,
        /// i.e. the caret is being held on a grid line rather than sitting where the cursor points.
        /// Always false while <see cref="SnapDragSeekToBeat"/> is idle. Read by
        /// <see cref="scrollToTrackTime"/>, which must not write that displacement back into the
        /// scroll position.
        /// </summary>
        private bool magnetHoldsCaret;

        /// <summary>
        /// The timeline zoom level at a 1x zoom scale.
        /// </summary>
        private float defaultTimelineZoom;

        private WaveformGraph waveform = null!;

        private TimelineTickDisplay ticks = null!;

        private TimelineTimingChangeDisplay controlPoints = null!;

        private Bindable<float> waveformOpacity = null!;
        private Bindable<bool> controlPointsVisible = null!;
        private Bindable<bool> ticksVisible = null!;

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

        private float tickAlpha = 1;

        /// <summary>
        /// Peak alpha of the beat tick display (1 by default). The user's ticks-visible setting
        /// still gates whether ticks show at all.
        /// </summary>
        public float TickAlpha
        {
            get => tickAlpha;
            set
            {
                tickAlpha = value;

                if (IsLoaded)
                    ticksVisible.TriggerChange();
            }
        }

        private double trackLengthForZoom;

        /// <summary>
        /// When armed, dragging this timeline magnets the seek to the nearest beat-grid line
        /// (see <see cref="MagnetToBeatGrid"/>). Off by default; a screen that wants the snap binds
        /// this to its own toggle. Nothing else on the timeline is affected, and the scroll itself
        /// stays continuous: only the time the drag SEEKS to is pulled onto the grid.
        /// </summary>
        public readonly BindableBool SnapDragSeekToBeat = new BindableBool();

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
                ticks = new TimelineTickDisplay(),
                new Box
                {
                    Name = "zero marker",
                    RelativeSizeAxes = Axes.Y,
                    Width = TimelineTickDisplay.TICK_WIDTH / 2,
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
                        waveform = new WaveformGraph
                        {
                            RelativeSizeAxes = Axes.Both,
                            BaseColour = colours.Blue.Opacity(0.2f),
                            LowColour = colours.BlueLighter,
                            MidColour = colours.BlueDark,
                            HighColour = colours.BlueDarker,
                        },
                        centreMarker.CreateProxy(),
                        ticks.CreateProxy(),
                        userContent,
                    }
                },
            });

            waveformOpacity = config.GetBindable<float>(OsuSetting.EditorWaveformOpacity);
            controlPointsVisible = config.GetBindable<bool>(OsuSetting.EditorTimelineShowTimingChanges);
            ticksVisible = config.GetBindable<bool>(OsuSetting.EditorTimelineShowTicks);

            editorClock.TrackChanged += updateWaveform;
            updateWaveform();

            Zoom = (float)(defaultTimelineZoom * editorBeatmap.TimelineZoom);
        }

        private void updateWaveform()
        {
            waveform.Waveform = beatmap.Value.Waveform;
            Scheduler.AddOnce(applyVisualOffset, beatmap);
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

            ticksVisible.BindValueChanged(visible => ticks.FadeTo(visible.NewValue ? tickAlpha : 0, 200, Easing.OutQuint), true);

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
            // Both callers are the timeline's own scroll driving the clock: the live drag, and the
            // inertia that settles after the user lets go. Magneting both is what makes a flick land
            // on the grid line it stops next to instead of sliding back off it.
            double raw = TimeAtPosition(Current);
            double target = MagnetToBeatGrid(raw);

            magnetHoldsCaret = target != raw;

            editorClock.Seek(Math.Min(editorClock.TrackLength, target));
        }

        /// <summary>
        /// Where a drag-seek to <paramref name="rawTime"/> actually lands: with
        /// <see cref="SnapDragSeekToBeat"/> armed, the nearest beat-grid line (the white/red/blue
        /// ticks, i.e. the current timing point at the current beat divisor) whenever it is within
        /// <see cref="EditorSnapMagnet.RADIUS_PX"/> of the cursor, otherwise the raw time.
        /// </summary>
        public double MagnetToBeatGrid(double rawTime)
        {
            if (!SnapDragSeekToBeat.Value || Content.DrawWidth <= 0 || editorClock.TrackLength <= 0)
                return rawTime;

            double msPerPixel = editorClock.TrackLength / Content.DrawWidth;

            return EditorSnapMagnet.Magnet(rawTime, beatSnapProvider.SnapTime(rawTime), EditorSnapMagnet.RADIUS_PX * msPerPixel);
        }

        private void scrollToTrackTime()
        {
            if (editorClock.TrackLength == 0)
                return;

            // covers the case where the user starts playback after a drag is in progress.
            // we want to ensure the clock is always stopped during drags to avoid weird audio playback.
            if (handlingDragInput)
                editorClock.Stop();

            // The scroll and the clock are a round trip through each other every frame, and without
            // the magnet it is the identity: this writes back exactly what seekTrackToCurrent read.
            // A magneted seek is NOT the identity, so writing its result back would drag the scroll
            // onto the grid line as well, erasing the raw travel the user has accumulated since the
            // magnet took hold. A mouse only reports a delta on the frames it actually moves, so the
            // erase happens between one delta and the next and the cursor can never reach the radius
            // it has to reach to escape: the caret sits trapped on the line until a single frame's
            // delta clears the radius outright, which lands it in the next line's pull instead.
            // So while the drag is live the RAW cursor position stays the source of truth and the
            // scroll is left exactly where the user put it. Only the magnet's own displacement is
            // suppressed here, so with the toggle idle this is the old behaviour untouched, and once
            // the drag is released the settle is free to bring the view to rest on the line.
            if (magnetHoldsCaret && IsDragged)
                return;

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

            // Only ever true for the gesture that set it: from here the settle is what decides where
            // the view comes to rest, and it should come to rest on the line the caret is held on.
            magnetHoldsCaret = false;

            if (trackWasPlaying)
                editorClock.Start();
        }

        [Resolved]
        private IBeatSnapProvider beatSnapProvider { get; set; } = null!;

        /// <summary>
        /// The total amount of time visible on the timeline.
        /// </summary>
        public double VisibleRange => editorClock.TrackLength / Zoom;

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
