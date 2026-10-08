// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using typebeat.Game.Overlays;
using typebeat.Game.Overlays.Notifications;
using typebeat.Game.Screens.ImportLyrics;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Backlog 410: an installed aligner that is not the version this build ships gets ONE
    /// notification a session, saying automatic alignment is off until it is updated; clicking it
    /// opens settings. A matching (or absent) aligner gets none.
    /// </summary>
    [TestFixture]
    public partial class TestSceneLocalAlignerVersionNotifier : OsuTestScene
    {
        [Cached(typeof(ILocalAlignerManager))]
        private readonly TestSceneImportLyricsAlignerGate.FakeAlignerManager aligner = new TestSceneImportLyricsAlignerGate.FakeAlignerManager();

        [Cached(typeof(INotificationOverlay))]
        private readonly RecordingNotificationOverlay notifications = new RecordingNotificationOverlay();

        private LocalAlignerVersionNotifier notifier = null!;
        private int settingsOpened;

        private void startSession(bool installed, string? installedVersion, string? shippedVersion)
        {
            AddStep($"aligner installed: {installed}, v{installedVersion} of v{shippedVersion}", () =>
            {
                notifications.Posted.Clear();
                settingsOpened = 0;
                aligner.IsInstalled = installed;
                aligner.NeedsRepair = false;
                aligner.InstalledVersion = installedVersion;
                aligner.ShippedVersion = shippedVersion;
                Child = notifier = new LocalAlignerVersionNotifier(() => settingsOpened++);
            });
            AddUntilStep("startup check finished", () => notifier.ChecksCompleted == 1);
        }

        [Test]
        public void MismatchPostsOnceAndClickingItOpensSettings()
        {
            startSession(installed: true, installedVersion: "8", shippedVersion: "9");

            AddAssert("one notification", () => notifications.Posted.Count == 1);
            AddAssert("it says alignment is off and names both versions", () => notifications.Posted.Single().Text.ToString()
                                                                                == "Automatic alignment is off: your local auto-aligner is out of date (v8, this version of type!beat needs v9). "
                                                                                + "Click here to update it in Settings.");

            // A second check in the same session (nothing calls one today, but nothing may make it nag).
            AddStep("check again", () => notifier.Check());
            AddUntilStep("second check finished", () => notifier.ChecksCompleted == 2);
            AddAssert("still one notification", () => notifications.Posted.Count == 1);

            AddStep("click it", () => notifications.Posted.Single().Activated?.Invoke());
            AddAssert("settings opened", () => settingsOpened == 1);
        }

        [Test]
        public void NewerInstallIsAMismatchToo()
        {
            startSession(installed: true, installedVersion: "10", shippedVersion: "9");

            AddAssert("one notification", () => notifications.Posted.Count == 1);
            AddAssert("it says reinstall", () => notifications.Posted.Single().Text.ToString().Contains("Click here to reinstall it in Settings.", System.StringComparison.Ordinal));
        }

        [Test]
        public void MatchingVersionPostsNothing()
        {
            startSession(installed: true, installedVersion: "9", shippedVersion: "9");
            AddAssert("no notification", () => notifications.Posted.Count == 0);
        }

        [Test]
        public void NothingInstalledPostsNothing()
        {
            startSession(installed: false, installedVersion: null, shippedVersion: "9");
            AddAssert("no notification", () => notifications.Posted.Count == 0);
        }

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
