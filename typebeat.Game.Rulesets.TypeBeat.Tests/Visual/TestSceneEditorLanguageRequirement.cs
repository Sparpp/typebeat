// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Input.Events;
using osu.Framework.Input.States;
using osu.Framework.Testing;
using typebeat.Game.Input.Bindings;
using typebeat.Game.Beatmaps;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Localisation;
using typebeat.Game.Online.API;
using typebeat.Game.Overlays;
using typebeat.Game.Overlays.Notifications;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;
using typebeat.Game.Screens.Edit.Setup;
using typebeat.Game.Screens.Edit.Submission;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// The mapper-facing half of the song-language requirement (task 58): the Language dropdown in
    /// song setup writes through to the beatmap's metadata, and submission is REFUSED, with an
    /// actionable message, until a real language is chosen.
    ///
    /// This is the enforcement point that makes the website's language: search and language chip
    /// meaningful for new maps, so it is worth a real editor here rather than a unit test on the
    /// predicate: the value the guard reads has to be the one the dropdown wrote, through the
    /// editor's own metadata plumbing.
    /// </summary>
    public partial class TestSceneEditorLanguageRequirement : EditorTestScene
    {
        [Cached(typeof(INotificationOverlay))]
        private readonly RecordingNotificationOverlay notifications = new RecordingNotificationOverlay();

        protected override Ruleset CreateEditorRuleset() => new TypeBeatRuleset();

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            // Non-empty on purpose: the empty-beatmap guard runs BEFORE the language guard, so an
            // empty map would never reach the assertion this fixture is about.
            var beatmap = new Beatmap
            {
                HitObjects = new List<Rulesets.Objects.HitObject>
                {
                    new TypeBeatHitObject
                    {
                        StartTime = 1000,
                        LineIndex = 0,
                        Granularity = Beatmaps.TimingGranularity.Word,
                        Line = new Beatmaps.LyricLine
                        {
                            RawText = "hello world",
                            StartTime = 1000,
                            EndTime = 3000,
                            SingEndTime = 2800,
                            Units = new[]
                            {
                                new Beatmaps.TimedUnit { Text = "hello", StartTime = 1000, EndTime = 1900, Source = Beatmaps.TimingSource.Explicit },
                                new Beatmaps.TimedUnit { Text = "world", StartTime = 1900, EndTime = 2800, Source = Beatmaps.TimingSource.Explicit },
                            },
                        },
                    },
                },
            };

            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.BeatmapInfo.Metadata.Artist = "Synth Rider";
            beatmap.BeatmapInfo.Metadata.Title = "Neon Nights";

            return beatmap;
        }

        [SetUpSteps]
        public void SetUpLanguageSteps()
        {
            AddStep("clear notifications", () => notifications.Posted.Clear());

            // The submit menu item and hotkey are both gated on the submission service being
            // configured, which DummyAPIAccess leaves unset.
            AddStep("enable submission endpoint", () =>
            {
                ((DummyAPIAccess)API).Endpoints.BeatmapSubmissionServiceUrl = "http://localhost/bss";
                ((DummyAPIAccess)API).SetState(APIState.Online);
            });
        }

        [Test]
        public void TestNewMapStartsUnspecifiedAndCannotBeSubmitted()
        {
            AddAssert("map starts with no language", () => EditorBeatmap.Metadata.Language, () => Is.EqualTo(BeatmapLanguage.Unspecified));

            AddStep("try to submit", submit);

            AddAssert("submission was refused", () => notifications.Posted.OfType<SimpleNotification>().Any(
                n => n.Text.ToString() == BeatmapSubmissionStrings.LanguageMustBeSetBeforeSubmission.ToString()));
            AddAssert("submission screen not pushed", () => !Editor.ChildrenOfType<BeatmapSubmissionScreen>().Any());
        }

        [Test]
        public void TestSetupDropdownWritesThroughAndUnblocksSubmission()
        {
            AddStep("switch to setup", () => Editor.Mode.Value = EditorScreenMode.SongSetup);
            AddUntilStep("setup screen shown", () => Editor.ChildrenOfType<SetupScreen>().Any());

            AddAssert("dropdown reflects the unset map", () => languageDropdown().Current.Value, () => Is.EqualTo(BeatmapLanguage.Unspecified));
            AddAssert("every real language is offered", () => languageDropdown().Items.ToHashSet(),
                () => Is.EquivalentTo(System.Enum.GetValues<BeatmapLanguage>()));

            AddStep("pick japanese", () => languageDropdown().Current.Value = BeatmapLanguage.Japanese);

            AddAssert("metadata updated", () => EditorBeatmap.Metadata.Language, () => Is.EqualTo(BeatmapLanguage.Japanese));

            // The whole point of storing it on metadata: it has to reach the file the server reads.
            AddAssert("encodes into the .osu", () =>
            {
                var writer = new System.IO.StringWriter();
                new TypeBeatRuleset().EncodeToNativeFormat(EditorBeatmap.PlayableBeatmap, null, writer);
                return writer.ToString();
            }, () => Does.Contain("Language:japanese"));

            AddStep("clear notifications", () => notifications.Posted.Clear());
            AddStep("try to submit", submit);

            AddAssert("no longer refused for language", () => notifications.Posted.OfType<SimpleNotification>().Any(
                n => n.Text.ToString() == BeatmapSubmissionStrings.LanguageMustBeSetBeforeSubmission.ToString()), () => Is.False);
        }

        /// <summary>
        /// Drives the real hotkey path (Editor.OnPressed -> submitBeatmap), which is the same code
        /// the File menu item runs; there is no shorter way in that still exercises the guard.
        /// </summary>
        private void submit() => Editor.OnPressed(
            new KeyBindingPressEvent<GlobalAction>(new InputState(), GlobalAction.EditorSubmitBeatmap));

        private FormEnumDropdown<BeatmapLanguage> languageDropdown()
            => Editor.ChildrenOfType<FormEnumDropdown<BeatmapLanguage>>().Single();

        private class RecordingNotificationOverlay : INotificationOverlay
        {
            public readonly List<Notification> Posted = new List<Notification>();

            public void Post(Notification notification) => Posted.Add(notification);

            public void Hide()
            {
            }

            public IBindable<int> UnreadCount { get; } = new Bindable<int>();

            public IEnumerable<Notification> AllNotifications => Posted;
        }
    }
}
