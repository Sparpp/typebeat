// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Input.Events;
using osu.Framework.Input.States;
using osu.Framework.Testing;
using typebeat.Game.Input.Bindings;
using typebeat.Game.Beatmaps;
using typebeat.Game.Graphics.Containers;
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
using osuTK.Input;

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

            AddAssert("dropdown reflects the unset map", () => languageDropdown().SelectedValues.Value, () => Is.Empty);
            AddAssert("selectable languages are offered in display order", () => languageDropdown().Items.ToArray(),
                () => Is.EqualTo(BeatmapLanguageExtensions.DisplayOrder));

            AddStep("pick japanese", () => languageDropdown().Toggle(BeatmapLanguage.Japanese));

            AddAssert("metadata updated", () => EditorBeatmap.Metadata.Language, () => Is.EqualTo(BeatmapLanguage.Japanese));

            // The whole point of storing it on metadata: it has to reach the file the server reads.
            AddAssert("encodes into the .osu", encodedMap, () => Does.Contain("Language:japanese"));

            AddStep("clear notifications", () => notifications.Posted.Clear());
            AddStep("try to submit", submit);

            AddAssert("no longer refused for language", () => notifications.Posted.OfType<SimpleNotification>().Any(
                n => n.Text.ToString() == BeatmapSubmissionStrings.LanguageMustBeSetBeforeSubmission.ToString()), () => Is.False);
        }

        /// <summary>
        /// Backlog 472: the setup screen's Language control is a multi-select. Driven with the real mouse: open the menu,
        /// click two languages, and both stay picked, highlighted and ticked while the menu stays open for the next pick,
        /// the header names both, and the metadata (and so the file the server reads, and the saved map) carries both.
        /// </summary>
        [Test]
        public void TestTwoLanguagesCanBePickedAndBothAreTickedAndSaved()
        {
            AddStep("switch to setup", () => Editor.Mode.Value = EditorScreenMode.SongSetup);
            AddUntilStep("setup screen shown", () => Editor.ChildrenOfType<SetupScreen>().Any());

            // To the TOP of the setup screen, so the open menu below it fits on screen.
            AddStep("scroll dropdown to the top", () =>
            {
                var scroll = languageDropdown().FindClosestParent<OsuScrollContainer>()!;
                scroll.ScrollTo(scroll.GetChildPosInContent(languageDropdown()), animated: false);
            });
            AddStep("open the dropdown", () =>
            {
                InputManager.MoveMouseTo(languageDropdown().ChildrenOfType<DropdownHeader>().Single());
                InputManager.Click(MouseButton.Left);
            });
            AddUntilStep("menu open", () => menuState(), () => Is.EqualTo(MenuState.Open));

            clickItem(BeatmapLanguage.Japanese);
            AddAssert("menu still open after a pick", () => menuState(), () => Is.EqualTo(MenuState.Open));
            clickItem(BeatmapLanguage.English);

            AddAssert("both picked, in picking order", () => languageDropdown().SelectedValues.Value,
                () => Is.EqualTo(new[] { BeatmapLanguage.Japanese, BeatmapLanguage.English }));
            AddAssert("both ticked", tickedItems, () => Is.EquivalentTo(new[] { BeatmapLanguage.Japanese, BeatmapLanguage.English }));
            AddStep("move mouse off the menu", () => InputManager.MoveMouseTo(languageDropdown().ChildrenOfType<DropdownHeader>().Single()));
            AddUntilStep("both highlighted", highlightedItems, () => Is.EquivalentTo(new[] { BeatmapLanguage.Japanese, BeatmapLanguage.English }));
            AddAssert("header names both", () => headerShows("Japanese, English"));

            AddAssert("metadata holds both", () => EditorBeatmap.Metadata.Languages,
                () => Is.EqualTo(new[] { BeatmapLanguage.Japanese, BeatmapLanguage.English }));
            AddAssert("primary is Japanese", () => EditorBeatmap.Metadata.Language, () => Is.EqualTo(BeatmapLanguage.Japanese));
            AddAssert("encodes both into the .osu", encodedMap, () => Does.Contain("Language:japanese").And.Contain("Languages:japanese,english"));

            AddAssert("save succeeds", () => Editor.Save());
            AddAssert("nothing left unsaved", () => Editor.HasUnsavedChanges, () => Is.False);

            // Exclusivity: Instrumental clears every language, and the ticks follow.
            clickItem(BeatmapLanguage.Instrumental);
            AddAssert("instrumental alone", () => EditorBeatmap.Metadata.Languages, () => Is.EqualTo(new[] { BeatmapLanguage.Instrumental }));
            AddAssert("only instrumental ticked", tickedItems, () => Is.EquivalentTo(new[] { BeatmapLanguage.Instrumental }));

            // Undo restores the editor's SAVED state (an encoded .osu, decoded back through RulesetBeatmapChangeHandler),
            // so both languages coming back proves the pair survives the editor's own write and read, and the dropdown
            // follows the restored metadata.
            AddStep("undo", () => Editor.Undo());
            AddAssert("both restored from the saved state", () => EditorBeatmap.Metadata.Languages,
                () => Is.EqualTo(new[] { BeatmapLanguage.Japanese, BeatmapLanguage.English }));
            AddUntilStep("dropdown follows the undo", tickedItems, () => Is.EquivalentTo(new[] { BeatmapLanguage.Japanese, BeatmapLanguage.English }));

            // Unpicking both leaves the map unspecified again.
            clickItem(BeatmapLanguage.Japanese);
            clickItem(BeatmapLanguage.English);
            AddAssert("nothing picked", () => EditorBeatmap.Metadata.Languages, () => Is.Empty);
            AddAssert("header reads unspecified", () => headerShows("Unspecified"));
        }

        private MenuState menuState() => languageDropdown().ChildrenOfType<Dropdown<BeatmapLanguage>.DropdownMenu>().Single().State;

        private bool headerShows(string text)
            => languageDropdown().ChildrenOfType<DropdownHeader>().Single()
                                 .ChildrenOfType<SpriteText>().Any(t => t.Text.ToString() == text);

        private void clickItem(BeatmapLanguage language)
        {
            AddStep($"scroll to {language}", () =>
            {
                var item = menuItem(language);
                item.FindClosestParent<OsuScrollContainer>()?.ScrollIntoView(item, animated: false);
            });
            AddStep($"click {language}", () =>
            {
                InputManager.MoveMouseTo(menuItem(language));
                InputManager.Click(MouseButton.Left);
            });
        }

        private FormMultiDropdown<BeatmapLanguage>.DrawableMultiSelectMenuItem menuItem(BeatmapLanguage language)
            => languageDropdown().ChildrenOfType<FormMultiDropdown<BeatmapLanguage>.DrawableMultiSelectMenuItem>()
                                 .Single(i => ((DropdownMenuItem<BeatmapLanguage>)i.Item).Value == language);

        private BeatmapLanguage[] tickedItems()
            => languageDropdown().ChildrenOfType<FormMultiDropdown<BeatmapLanguage>.DrawableMultiSelectMenuItem>()
                                 .Where(i => i.Checked && i.ChildrenOfType<SpriteIcon>().Any(s => s.Icon.Equals(FontAwesome.Solid.Check) && s.Alpha == 1))
                                 .Select(i => ((DropdownMenuItem<BeatmapLanguage>)i.Item).Value)
                                 .ToArray();

        /// <summary>
        /// The items whose highlight background is fully shown. The last item the mouse passed over stays PRESELECTED (and so
        /// shows the hover highlight) until another is, so a preselected item counts only when it is also checked: an
        /// unchecked item must never show a highlight of its own.
        /// </summary>
        private BeatmapLanguage[] highlightedItems()
            => languageDropdown().ChildrenOfType<FormMultiDropdown<BeatmapLanguage>.DrawableMultiSelectMenuItem>()
                                 .Where(i => i.HighlightAlpha == 1 && (i.Checked || !i.IsPreSelected))
                                 .Select(i => ((DropdownMenuItem<BeatmapLanguage>)i.Item).Value)
                                 .ToArray();

        private string encodedMap()
        {
            var writer = new System.IO.StringWriter();
            new TypeBeatRuleset().EncodeToNativeFormat(EditorBeatmap.PlayableBeatmap, null, writer);
            return writer.ToString();
        }

        /// <summary>
        /// Drives the real hotkey path (Editor.OnPressed -> submitBeatmap), which is the same code
        /// the File menu item runs; there is no shorter way in that still exercises the guard.
        /// </summary>
        private void submit() => Editor.OnPressed(
            new KeyBindingPressEvent<GlobalAction>(new InputState(), GlobalAction.EditorSubmitBeatmap));

        private FormMultiEnumDropdown<BeatmapLanguage> languageDropdown()
            => Editor.ChildrenOfType<FormMultiEnumDropdown<BeatmapLanguage>>().Single();

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
