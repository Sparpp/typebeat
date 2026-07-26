// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Audio.Sample;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using typebeat.Game.Overlays;
using typebeat.Game.Screens.Edit.Submission;
using osuTK;

namespace typebeat.Game.Screens.ImportLyrics
{
    /// <summary>
    /// The import flow's progress panel. Reuses the beatmap submission window's
    /// <see cref="SubmissionStageProgress"/> rows verbatim (same bar, spinner, tick/cross/ban icons,
    /// and the same bss-progress / bss-stage-N / generic-error / notification-cancel samples), so
    /// importing looks and sounds like submitting.
    ///
    /// Where submission knows its four steps up front, an import's route is decided as it runs
    /// (local aligner, server aligner, or straight from line stamps), so rows are revealed as their
    /// stage is first reached: entering a stage completes every row above it. Raw pipeline chatter
    /// is classified by <see cref="ImportProgressParser"/> and never rendered; unrecognised lines
    /// leave the current row alone rather than printing internals.
    /// </summary>
    public partial class ImportProgressDisplay : CompositeDrawable
    {
        private FillFlowContainer stages = null!;

        private Sample? completeSample;

        private readonly List<Row> rows = new List<Row>();

        /// <summary>
        /// Index of the first row belonging to the current attempt. A fallback (the local aligner
        /// bowing out mid-run, say) abandons everything above it, so the next attempt's stages get
        /// their own rows instead of reopening cancelled ones.
        /// </summary>
        private int roundStart;

        private ImportStage? currentStage;

        [BackgroundDependencyLoader]
        private void load(OverlayColourProvider colourProvider, AudioManager audio)
        {
            RelativeSizeAxes = Axes.X;
            AutoSizeAxes = Axes.Y;

            InternalChild = new Container
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                AutoSizeDuration = 400,
                AutoSizeEasing = Easing.OutQuint,
                Masking = true,
                CornerRadius = 8,
                Children = new Drawable[]
                {
                    new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = colourProvider.Background5,
                    },
                    stages = new FillFlowContainer
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Direction = FillDirection.Vertical,
                        Padding = new MarginPadding(16),
                        Spacing = new Vector2(0, 5),
                    }
                }
            };

            completeSample = audio.Samples.Get(@"UI/bss-complete");
        }

        /// <summary>Clears every row, for a fresh attempt after a failed one.</summary>
        public void Reset()
        {
            stages.Clear();
            rows.Clear();
            roundStart = 0;
            currentStage = null;
        }

        /// <summary>
        /// Feeds one raw progress line from the import pipeline. Update thread only.
        /// </summary>
        public void Report(string? line)
        {
            string cleaned = ImportProgressParser.Clean(line);

            if (cleaned.Length == 0)
                return;

            var update = ImportProgressParser.Parse(cleaned);
            enter(update.Stage ?? currentStage ?? ImportStage.Preparing, update.Progress);
        }

        /// <summary>Marks the import finished: every outstanding row ticks over and the fanfare plays.</summary>
        public void Complete()
        {
            foreach (var row in rows)
                finish(row, cancelled: false);

            completeSample?.Play();
        }

        /// <summary>
        /// Marks the import failed. The row keeps its short label and shows
        /// <paramref name="error"/> beside it, so the actionable detail is never lost.
        /// </summary>
        public void Fail(string? error)
        {
            var row = rows.LastOrDefault(r => !r.Finished);

            if (row == null)
            {
                row = createRow(currentStage ?? ImportStage.Preparing);
                rows.Add(row);
            }

            row.Drawable.SetFailed(error ?? "the import failed");
            row.Finished = true;
        }

        private void enter(ImportStage stage, float? progress)
        {
            if (stage == ImportStage.FallingBack)
            {
                // Everything the abandoned aligner was doing is dead; show it as cancelled rather
                // than completed, then note the handover as an instantaneous step of its own.
                for (int i = roundStart; i < rows.Count; i++)
                    finish(rows[i], cancelled: true);

                var notice = createRow(ImportStage.FallingBack);
                notice.Drawable.SetCompleted();
                notice.Finished = true;
                rows.Add(notice);

                roundStart = rows.Count;
                currentStage = null;
                return;
            }

            int index = indexOf(stage);

            if (index >= 0 && rows[index].Finished)
            {
                // A step that has already ticked over saying something else (the pipeline logs its
                // audio prep after separation has begun, say). The display never walks backwards:
                // hand the line to whatever row is still running, for its progress fraction alone.
                index = lastUnfinishedIndex();

                if (index < 0)
                    return;
            }
            else if (index < 0)
            {
                index = rows.Count;
                rows.Add(createRow(stage));
            }

            for (int i = roundStart; i < index; i++)
                finish(rows[i], cancelled: false);

            var current = rows[index];

            // Lines within a stage alternate between carrying a fraction and not (a tqdm tick, then
            // a plain log line). Holding the last known fraction stops the bar flickering away.
            current.LastProgress = progress ?? current.LastProgress;
            current.Drawable.SetInProgress(current.LastProgress);
            current.Finished = false;

            currentStage = current.Stage;
        }

        /// <summary>Index of the current attempt's row for a stage, or -1.</summary>
        private int indexOf(ImportStage stage)
        {
            for (int i = roundStart; i < rows.Count; i++)
            {
                if (rows[i].Stage == stage)
                    return i;
            }

            return -1;
        }

        private int lastUnfinishedIndex()
        {
            for (int i = rows.Count - 1; i >= roundStart; i--)
            {
                if (!rows[i].Finished)
                    return i;
            }

            return -1;
        }

        private static void finish(Row row, bool cancelled)
        {
            if (row.Finished)
                return;

            if (cancelled)
                row.Drawable.SetCanceled();
            else
                row.Drawable.SetCompleted();

            row.Finished = true;
        }

        private Row createRow(ImportStage stage)
        {
            var drawable = new SubmissionStageProgress
            {
                StageDescription = ImportProgressParser.LabelFor(stage),
                StageIndex = rows.Count,
                Anchor = Anchor.TopCentre,
                Origin = Anchor.TopCentre,
                Alpha = 0,
            };

            stages.Add(drawable);
            drawable.FadeIn(200, Easing.OutQuint);

            return new Row(stage, drawable);
        }

        private class Row
        {
            public readonly ImportStage Stage;
            public readonly SubmissionStageProgress Drawable;

            public bool Finished;
            public float? LastProgress;

            public Row(ImportStage stage, SubmissionStageProgress drawable)
            {
                Stage = stage;
                Drawable = drawable;
            }
        }
    }
}
