// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Screens;
using osu.Framework.Testing;
using typebeat.Game.Graphics.Cursor;
using typebeat.Game.Screens;
using typebeat.Game.Screens.ImportLyrics;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Backlog 409: the import screen's "automatic alignment" choice is greyed out and unticked while
    /// the local auto-aligner is not installed, and hovering it says where to install it, instead of
    /// letting the player press import and only then hit the importer's "not installed" failure.
    /// </summary>
    [TestFixture]
    public partial class TestSceneImportLyricsAlignerGate : ScreenTestScene
    {
        [Cached(typeof(ILocalAlignerManager))]
        private readonly FakeAlignerManager aligner = new FakeAlignerManager();

        private ImportLyricsScreen screen = null!;

        private void loadImportScreen(bool installed, bool needsRepair = false, string? installedVersion = "9", string? shippedVersion = "9")
        {
            AddStep($"aligner installed: {installed}, needs repair: {needsRepair}, v{installedVersion} of v{shippedVersion}", () =>
            {
                aligner.IsInstalled = installed;
                aligner.NeedsRepair = needsRepair;
                aligner.InstalledVersion = installedVersion;
                aligner.ShippedVersion = shippedVersion;
            });
            AddStep("load import screen", () => LoadScreen(screen = new ImportLyricsScreen()));
            AddUntilStep("import screen current", () => screen.IsCurrentScreen());
        }

        [Test]
        public void NotInstalledGreysOutTheChoiceAndSaysWhereToInstall()
        {
            loadImportScreen(installed: false);

            AddAssert("checkbox disabled", () => screen.AutomaticAlignment.Current.Disabled);
            AddAssert("checkbox unticked", () => !screen.AutomaticAlignment.Current.Value);
            AddAssert("tooltip names the settings path", () => tooltipOf(screen) == ImportLyricsScreen.ALIGNER_NOT_INSTALLED_TOOLTIP
                                                                && tooltipOf(screen).Contains("Settings > Experimental > Lyric timing", StringComparison.Ordinal));

            // The hover has to reach a DISABLED checkbox for the tooltip to be any use.
            AddStep("hover the checkbox", () => InputManager.MoveMouseTo(screen.AutomaticAlignment));
            AddUntilStep("tooltip shown with the install hint", () => shownTooltipText().Contains("Lyrictiming", StringComparison.Ordinal));

            // Clicking a disabled checkbox must not tick it.
            AddStep("click the checkbox", () => InputManager.Click(osuTK.Input.MouseButton.Left));
            AddAssert("still unticked", () => !screen.AutomaticAlignment.Current.Value);
        }

        [Test]
        public void IncompleteInstallPointsAtRepair()
        {
            loadImportScreen(installed: false, needsRepair: true);

            AddAssert("checkbox disabled", () => screen.AutomaticAlignment.Current.Disabled);
            AddAssert("tooltip says repair", () => tooltipOf(screen) == ImportLyricsScreen.ALIGNER_NEEDS_REPAIR_TOOLTIP);
        }

        [Test]
        public void InstalledLeavesTheChoiceEnabledWithNoTooltip()
        {
            loadImportScreen(installed: true);

            AddAssert("checkbox enabled", () => !screen.AutomaticAlignment.Current.Disabled);
            AddAssert("no tooltip", () => string.IsNullOrEmpty(tooltipOf(screen)));
            AddStep("tick it", () => screen.AutomaticAlignment.Current.Value = true);
            AddAssert("ticked", () => screen.AutomaticAlignment.Current.Value);
        }

        /// <summary>
        /// Backlog 410: an installed aligner whose version is not the one this build ships is refused
        /// by the importer, older or newer alike, so the choice is greyed out with a tooltip naming
        /// both versions and where to update it.
        /// </summary>
        [TestCase("8", "Your local auto-aligner is out of date (v8, this version of type!beat needs v9). "
                       + "Update it in Settings > Experimental > Lyric timing to use automatic alignment.")]
        [TestCase(null, "Your local auto-aligner is out of date (v1, this version of type!beat needs v9). "
                        + "Update it in Settings > Experimental > Lyric timing to use automatic alignment.")]
        [TestCase("10", "Your local auto-aligner is from a newer version of type!beat (v10, this version of type!beat needs v9). "
                        + "Reinstall it in Settings > Experimental > Lyric timing to use automatic alignment.")]
        public void MismatchedVersionGreysOutTheChoiceAndSaysWhereToUpdate(string? installedVersion, string expectedTooltip)
        {
            loadImportScreen(installed: true, installedVersion: installedVersion, shippedVersion: "9");

            AddAssert("checkbox disabled", () => screen.AutomaticAlignment.Current.Disabled);
            AddAssert("checkbox unticked", () => !screen.AutomaticAlignment.Current.Value);
            AddAssert("tooltip names both versions and the place to update", () => tooltipOf(screen) == expectedTooltip);
        }

        [Test]
        public void MatchingVersionLeavesTheChoiceEnabled()
        {
            loadImportScreen(installed: true, installedVersion: "9", shippedVersion: "9");

            AddAssert("checkbox enabled", () => !screen.AutomaticAlignment.Current.Disabled);
            AddAssert("no tooltip", () => string.IsNullOrEmpty(tooltipOf(screen)));
        }

        /// <summary>A player who updates from settings and comes back finds the choice enabled again.</summary>
        [Test]
        public void ResumingAfterAnUpdateReEnablesTheChoice()
        {
            loadImportScreen(installed: true, installedVersion: "8", shippedVersion: "9");
            AddAssert("checkbox disabled", () => screen.AutomaticAlignment.Current.Disabled);

            coverAndResume(() => aligner.InstalledVersion = "9");
            AddAssert("enabled after the update", () => !screen.AutomaticAlignment.Current.Disabled);
            AddAssert("tooltip cleared", () => string.IsNullOrEmpty(tooltipOf(screen)));
        }

        /// <summary>
        /// The state is re-read when the screen becomes current again, in both directions: a player
        /// who installs and comes back finds the choice enabled, and an aligner that went away while
        /// the screen was covered unticks the choice (and the estimated vocals one that depends on it).
        /// </summary>
        [Test]
        public void ResumingReReadsTheInstall()
        {
            loadImportScreen(installed: false);
            AddAssert("checkbox disabled", () => screen.AutomaticAlignment.Current.Disabled);

            coverAndResume(() => aligner.IsInstalled = true);
            AddAssert("enabled after the install", () => !screen.AutomaticAlignment.Current.Disabled);
            AddAssert("tooltip cleared", () => string.IsNullOrEmpty(tooltipOf(screen)));

            AddStep("tick it", () => screen.AutomaticAlignment.Current.Value = true);

            coverAndResume(() => aligner.IsInstalled = false);
            AddAssert("disabled again", () => screen.AutomaticAlignment.Current.Disabled);
            AddAssert("unticked", () => !screen.AutomaticAlignment.Current.Value);
            AddAssert("estimated vocals off and disabled with it", () => !screen.EstimatedVocals.Current.Value && screen.EstimatedVocals.Current.Disabled);
        }

        private void coverAndResume(Action change)
        {
            BlankScreen? cover = null;

            AddStep("push a screen on top", () => LoadScreen(cover = new BlankScreen()));
            AddUntilStep("cover current", () => cover!.IsCurrentScreen());
            AddStep("change the install", change);
            AddStep("exit the cover", () => cover!.Exit());
            AddUntilStep("import screen current again", () => screen.IsCurrentScreen());
        }

        private static string tooltipOf(ImportLyricsScreen screen) => ((IHasTooltip)screen.AutomaticAlignment).TooltipText.ToString();

        /// <summary>The visible tooltip's text, spaces dropped (the text flow lays each word out as its own sprite).</summary>
        private string shownTooltipText()
        {
            var tooltip = this.ChildrenOfType<OsuTooltipContainer.OsuTooltip>().FirstOrDefault(t => t.State.Value == Visibility.Visible);
            return tooltip == null ? string.Empty : string.Concat(tooltip.ChildrenOfType<SpriteText>().Select(s => s.Text.ToString())).Replace(" ", string.Empty);
        }

        private partial class BlankScreen : OsuScreen
        {
        }

        internal class FakeAlignerManager : ILocalAlignerManager
        {
            public bool IsInstalled { get; set; }
            public bool NeedsRepair { get; set; }
            public string? InstalledDevice => null;
            public bool GpuDetected => false;
            public string? InstalledVersion { get; set; }
            public string? ShippedVersion { get; set; }

            public Task<LyricImportResult> InstallAsync(Action<string> progress, CancellationToken token) => throw new NotSupportedException();
        }
    }

    /// <summary>
    /// A build with no aligner installer at all (nothing caches <see cref="ILocalAlignerManager"/>,
    /// as in a headless scene): the choice is dead too, with a tooltip saying so.
    /// </summary>
    [TestFixture]
    public partial class TestSceneImportLyricsAlignerGateWithoutManager : ScreenTestScene
    {
        [Test]
        public void NoManagerGreysOutTheChoice()
        {
            ImportLyricsScreen screen = null!;

            AddStep("load import screen", () => LoadScreen(screen = new ImportLyricsScreen()));
            AddUntilStep("import screen current", () => screen.IsCurrentScreen());

            AddAssert("checkbox disabled", () => screen.AutomaticAlignment.Current.Disabled);
            AddAssert("checkbox unticked", () => !screen.AutomaticAlignment.Current.Value);
            AddAssert("tooltip says unavailable", () => ((IHasTooltip)screen.AutomaticAlignment).TooltipText.ToString() == ImportLyricsScreen.ALIGNER_UNAVAILABLE_TOOLTIP);
        }
    }
}
