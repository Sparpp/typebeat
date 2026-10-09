// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Screens;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Graphics.Cursor;
using typebeat.Game.Online.API;
using typebeat.Game.Screens;
using typebeat.Game.Screens.ImportLyrics;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Backlog 409: the import screen's "automatic alignment" choice is greyed out and unticked while
    /// the local auto-aligner is not installed, and hovering it says where to install it, instead of
    /// letting the player press import and only then hit the importer's "not installed" failure.
    /// Backlog 413 adds the server aligner opt-in beside it, which those gates do not apply to.
    /// </summary>
    [TestFixture]
    public partial class TestSceneImportLyricsAlignerGate : ScreenTestScene
    {
        [Cached(typeof(ILocalAlignerManager))]
        private readonly FakeAlignerManager aligner = new FakeAlignerManager();

        [Cached(typeof(ILyricMapImporter))]
        private readonly FakeImporter importer = new FakeImporter();

        private ImportLyricsScreen screen = null!;

        private DummyAPIAccess dummyApi => (DummyAPIAccess)API;

        [SetUpSteps]
        public void SetUpGateSteps()
        {
            AddStep("signed in, nothing remembered", () =>
            {
                dummyApi.SetState(APIState.Online);
                importer.Preference.Value = false;
                importer.IsolatePreference.Value = true;
                importer.CallQueue.Clear();
            });
        }

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
            AddAssert("tooltip names the settings path, then the server", () => tooltipOf(screen) == ImportLyricsScreen.AutomaticAlignmentTooltip(ImportLyricsScreen.ALIGNER_NOT_INSTALLED_TOOLTIP, true)
                                                                            && tooltipOf(screen).Contains("Settings > Experimental > Lyric timing", StringComparison.Ordinal)
                                                                            && tooltipOf(screen).EndsWith(ImportLyricsScreen.SERVER_ALIGNER_ALTERNATIVE, StringComparison.Ordinal));

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
            AddAssert("tooltip says repair", () => tooltipOf(screen) == ImportLyricsScreen.AutomaticAlignmentTooltip(ImportLyricsScreen.ALIGNER_NEEDS_REPAIR_TOOLTIP, true));
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
            AddAssert("tooltip names both versions and the place to update", () => tooltipOf(screen) == ImportLyricsScreen.AutomaticAlignmentTooltip(expectedTooltip, true));
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

        // -----------------------------------------------------------------------------------------
        // Backlog 413: the server aligner opt-in
        // -----------------------------------------------------------------------------------------

        [Test]
        public void TheServerChoiceIsLabelledAsTheOwnerWorded()
        {
            loadImportScreen(installed: true);
            AddAssert("label constant", () => ImportLyricsScreen.SERVER_ALIGNER_LABEL, () => Is.EqualTo("use server aligner (only use if you can't run the local one!)"));
            // The text flow lays each word out as its own sprite, so the shown label is compared with spaces dropped.
            AddAssert("label shown", () => string.Concat(screen.ServerAligner.ChildrenOfType<SpriteText>().Select(t => t.Text.ToString())).Replace(" ", string.Empty),
                () => Is.EqualTo(ImportLyricsScreen.SERVER_ALIGNER_LABEL.Replace(" ", string.Empty)));
            AddAssert("off by default", () => !screen.ServerAligner.Current.Value);
            AddAssert("enabled while signed in", () => !screen.ServerAligner.Current.Disabled);
            AddAssert("no tooltip while signed in", () => string.IsNullOrEmpty(serverTooltipOf(screen)));
        }

        /// <summary>A guest sees the choice greyed out, unticked, and told why; their stored choice is not touched.</summary>
        [Test]
        public void AGuestCannotTickTheServerChoice()
        {
            AddStep("signed out, server remembered", () =>
            {
                dummyApi.SetState(APIState.Offline);
                importer.Preference.Value = true;
            });
            loadImportScreen(installed: false);

            AddAssert("server choice disabled", () => screen.ServerAligner.Current.Disabled);
            AddAssert("server choice unticked", () => !screen.ServerAligner.Current.Value);
            AddAssert("tooltip says sign in", () => serverTooltipOf(screen), () => Is.EqualTo("Sign in to use the server aligner."));
            AddAssert("automatic alignment stays greyed out", () => screen.AutomaticAlignment.Current.Disabled && !screen.AutomaticAlignment.Current.Value);
            AddAssert("its tooltip tells a guest to sign in for the server", () => tooltipOf(screen),
                () => Is.EqualTo(ImportLyricsScreen.AutomaticAlignmentTooltip(ImportLyricsScreen.ALIGNER_NOT_INSTALLED_TOOLTIP, false)));
            AddAssert("the stored choice is left alone", () => importer.Preference.Value);

            // Signing in on the screen brings the remembered choice back.
            AddStep("sign in", () => dummyApi.SetState(APIState.Online));
            AddUntilStep("server choice ticked again", () => screen.ServerAligner.Current.Value && !screen.ServerAligner.Current.Disabled);
            AddAssert("and automatic alignment with it", () => screen.AutomaticAlignment.Current.Value && !screen.AutomaticAlignment.Current.Disabled);

            AddStep("sign out", () => dummyApi.SetState(APIState.Offline));
            AddUntilStep("greyed out again", () => screen.ServerAligner.Current.Disabled && !screen.ServerAligner.Current.Value);
            AddAssert("automatic alignment greyed out with no local aligner", () => screen.AutomaticAlignment.Current.Disabled && !screen.AutomaticAlignment.Current.Value);
            AddAssert("the stored choice still survives", () => importer.Preference.Value);
        }

        /// <summary>
        /// With no local aligner, ticking the server choice ticks AND enables automatic alignment (the
        /// local gates do not apply to it), estimated vocals become available, and unticking automatic
        /// alignment unticks the server choice. Every player-made change is remembered.
        /// </summary>
        [Test]
        public void TickingTheServerChoiceEnablesAutomaticAlignmentWithoutALocalAligner()
        {
            loadImportScreen(installed: false);
            AddAssert("automatic alignment greyed out", () => screen.AutomaticAlignment.Current.Disabled);

            AddStep("tick the server choice", () => screen.ServerAligner.Current.Value = true);
            AddAssert("automatic alignment ticked and enabled", () => screen.AutomaticAlignment.Current.Value && !screen.AutomaticAlignment.Current.Disabled);
            AddAssert("its tooltip cleared", () => string.IsNullOrEmpty(tooltipOf(screen)));
            AddAssert("estimated vocals available", () => !screen.EstimatedVocals.Current.Disabled);
            AddAssert("remembered", () => importer.Preference.Value);

            AddStep("untick automatic alignment", () => screen.AutomaticAlignment.Current.Value = false);
            AddAssert("server choice unticked", () => !screen.ServerAligner.Current.Value);
            AddAssert("automatic alignment greyed out again", () => screen.AutomaticAlignment.Current.Disabled);
            AddAssert("the untick is remembered", () => !importer.Preference.Value);
        }

        /// <summary>With the local aligner fine, unticking the server choice leaves automatic alignment on, now local.</summary>
        [Test]
        public void UntickingTheServerChoiceKeepsALocalAlignment()
        {
            loadImportScreen(installed: true);

            AddStep("tick the server choice", () => screen.ServerAligner.Current.Value = true);
            AddAssert("automatic alignment ticked", () => screen.AutomaticAlignment.Current.Value);
            AddStep("untick the server choice", () => screen.ServerAligner.Current.Value = false);
            AddAssert("automatic alignment still ticked and enabled", () => screen.AutomaticAlignment.Current.Value && !screen.AutomaticAlignment.Current.Disabled);
        }

        [Test]
        public void TheRememberedChoiceComesBackTicked()
        {
            AddStep("server remembered", () => importer.Preference.Value = true);
            loadImportScreen(installed: false);

            AddAssert("server choice ticked", () => screen.ServerAligner.Current.Value);
            AddAssert("automatic alignment ticked and enabled", () => screen.AutomaticAlignment.Current.Value && !screen.AutomaticAlignment.Current.Disabled);
        }

        /// <summary>The import asks the importer for the server when ticked, even with a working local aligner, and not otherwise.</summary>
        [TestCase(true)]
        [TestCase(false)]
        public void TheImportAsksForTheServerOnlyWhenTicked(bool ticked)
        {
            loadImportScreen(installed: true);

            AddStep("add audio and choose a language", () =>
            {
                screen.AddFiles(new[] { "Some Artist - A Song.mp3" });
                screen.Language.Value = BeatmapLanguage.English;
            });
            AddStep("tick automatic alignment", () => screen.AutomaticAlignment.Current.Value = true);
            AddStep($"server choice: {ticked}", () => screen.ServerAligner.Current.Value = ticked);
            AddStep("import", () => screen.ImportButton.TriggerClick());
            AddUntilStep("importer called", () => importer.Calls.Count, () => Is.EqualTo(1));
            AddAssert("automatic alignment asked for", () => importer.Calls.Single().Automatic);
            AddAssert("server asked for exactly when ticked", () => importer.Calls.Single().Server, () => Is.EqualTo(ticked));
        }

        // -----------------------------------------------------------------------------------------
        // Backlog 414: the isolate vocals choice
        // -----------------------------------------------------------------------------------------

        [Test]
        public void IsolateVocalsIsTickedByDefaultAndLabelledAsSpecified()
        {
            loadImportScreen(installed: true);

            AddAssert("label constant", () => ImportLyricsScreen.ISOLATE_VOCALS_LABEL,
                () => Is.EqualTo("isolate vocals (for the editor's vocals waveform, adds a few minutes)"));
            AddAssert("label shown", () => string.Concat(screen.IsolateVocals.ChildrenOfType<SpriteText>().Select(t => t.Text.ToString())).Replace(" ", string.Empty),
                () => Is.EqualTo(ImportLyricsScreen.ISOLATE_VOCALS_LABEL.Replace(" ", string.Empty)));
            AddAssert("ticked", () => screen.IsolateVocals.Current.Value);
            AddAssert("enabled", () => !screen.IsolateVocals.Current.Disabled);
            AddAssert("no tooltip", () => string.IsNullOrEmpty(isolateTooltipOf(screen)));
        }

        /// <summary>The player's untick is remembered, and comes back unticked on the next visit.</summary>
        [Test]
        public void IsolateVocalsRemembersThePlayersChoice()
        {
            loadImportScreen(installed: true);

            AddStep("untick it", () => screen.IsolateVocals.Current.Value = false);
            AddAssert("remembered", () => !importer.IsolatePreference.Value);

            loadImportScreen(installed: true);
            AddAssert("comes back unticked", () => !screen.IsolateVocals.Current.Value && !screen.IsolateVocals.Current.Disabled);

            AddStep("tick it again", () => screen.IsolateVocals.Current.Value = true);
            AddAssert("remembered ticked", () => importer.IsolatePreference.Value);
        }

        /// <summary>
        /// The separation is the local aligner's, so the choice greys out for the reasons automatic
        /// alignment's local path does (backlogs 409/410), with the local reason alone as its tooltip,
        /// and the stored choice is left alone.
        /// </summary>
        [TestCase(false, false, "9", ImportLyricsScreen.ALIGNER_NOT_INSTALLED_TOOLTIP)]
        [TestCase(false, true, "9", ImportLyricsScreen.ALIGNER_NEEDS_REPAIR_TOOLTIP)]
        [TestCase(true, false, "8", "Your local auto-aligner is out of date (v8, this version of type!beat needs v9). "
                                    + "Update it in Settings > Experimental > Lyric timing to use automatic alignment.")]
        public void IsolateVocalsGreysOutWithTheLocalReason(bool installed, bool needsRepair, string installedVersion, string expectedTooltip)
        {
            loadImportScreen(installed, needsRepair, installedVersion, "9");

            AddAssert("disabled", () => screen.IsolateVocals.Current.Disabled);
            AddAssert("unticked", () => !screen.IsolateVocals.Current.Value);
            AddAssert("tooltip is the local reason", () => isolateTooltipOf(screen), () => Is.EqualTo(expectedTooltip));
            AddAssert("the stored choice is left alone", () => importer.IsolatePreference.Value);

            // The server aligner sends no stem, so ticking it changes nothing here.
            AddStep("tick the server choice", () => screen.ServerAligner.Current.Value = true);
            AddAssert("still disabled and unticked", () => screen.IsolateVocals.Current.Disabled && !screen.IsolateVocals.Current.Value);

            // Fixing the aligner and coming back brings the remembered choice back.
            coverAndResume(() =>
            {
                aligner.IsInstalled = true;
                aligner.NeedsRepair = false;
                aligner.InstalledVersion = "9";
            });
            AddAssert("enabled and ticked again", () => !screen.IsolateVocals.Current.Disabled && screen.IsolateVocals.Current.Value);
            AddAssert("tooltip cleared", () => string.IsNullOrEmpty(isolateTooltipOf(screen)));
        }

        /// <summary>
        /// Local automatic alignment isolates the vocals anyway: the choice shows ticked and disabled
        /// with a tooltip saying so, without touching the stored choice, which comes back when
        /// automatic alignment goes off or moves to the server.
        /// </summary>
        [Test]
        public void LocalAutomaticAlignmentShowsIsolateVocalsTickedAndDisabled()
        {
            AddStep("untick remembered", () => importer.IsolatePreference.Value = false);
            loadImportScreen(installed: true);
            AddAssert("unticked from the stored choice", () => !screen.IsolateVocals.Current.Value);

            AddStep("tick automatic alignment", () => screen.AutomaticAlignment.Current.Value = true);
            AddAssert("ticked and disabled", () => screen.IsolateVocals.Current.Value && screen.IsolateVocals.Current.Disabled);
            AddAssert("tooltip says alignment isolates them", () => isolateTooltipOf(screen), () => Is.EqualTo("Automatic alignment already isolates the vocals."));
            AddAssert("the stored choice is left alone", () => !importer.IsolatePreference.Value);

            AddStep("tick the server choice", () => screen.ServerAligner.Current.Value = true);
            AddAssert("the server leaves it the player's choice", () => !screen.IsolateVocals.Current.Value && !screen.IsolateVocals.Current.Disabled);
            AddAssert("no tooltip", () => string.IsNullOrEmpty(isolateTooltipOf(screen)));

            AddStep("untick the server choice", () => screen.ServerAligner.Current.Value = false);
            AddAssert("local again: ticked and disabled", () => screen.IsolateVocals.Current.Value && screen.IsolateVocals.Current.Disabled);

            AddStep("untick automatic alignment", () => screen.AutomaticAlignment.Current.Value = false);
            AddAssert("back to the stored choice", () => !screen.IsolateVocals.Current.Value && !screen.IsolateVocals.Current.Disabled);
            AddAssert("still not overwritten", () => !importer.IsolatePreference.Value);
        }

        /// <summary>The import asks for isolated vocals exactly as the choice is shown.</summary>
        [TestCase(true)]
        [TestCase(false)]
        public void TheImportPassesTheIsolateVocalsChoice(bool ticked)
        {
            loadImportScreen(installed: true);

            AddStep("add audio and choose a language", () =>
            {
                screen.AddFiles(new[] { "Some Artist - A Song.mp3" });
                screen.Language.Value = BeatmapLanguage.English;
            });
            AddStep($"isolate vocals: {ticked}", () => screen.IsolateVocals.Current.Value = ticked);
            AddStep("import", () => screen.ImportButton.TriggerClick());
            AddUntilStep("importer called", () => importer.Calls.Count, () => Is.EqualTo(1));
            AddAssert("no automatic alignment", () => !importer.Calls.Single().Automatic);
            AddAssert("isolation asked for exactly when ticked", () => importer.Calls.Single().Isolate, () => Is.EqualTo(ticked));
        }

        [Test]
        public void AGreyedOutIsolateVocalsChoiceIsNotAskedFor()
        {
            loadImportScreen(installed: false);

            AddStep("add audio and choose a language", () =>
            {
                screen.AddFiles(new[] { "Some Artist - A Song.mp3" });
                screen.Language.Value = BeatmapLanguage.English;
            });
            AddStep("import", () => screen.ImportButton.TriggerClick());
            AddUntilStep("importer called", () => importer.Calls.Count, () => Is.EqualTo(1));
            AddAssert("isolation not asked for", () => !importer.Calls.Single().Isolate);
            AddAssert("the stored choice survives", () => importer.IsolatePreference.Value);
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

        private static string serverTooltipOf(ImportLyricsScreen screen) => ((IHasTooltip)screen.ServerAligner).TooltipText.ToString();

        private static string isolateTooltipOf(ImportLyricsScreen screen) => ((IHasTooltip)screen.IsolateVocals).TooltipText.ToString();

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

        /// <summary>Records what each import asked for and fails it at once; holds the remembered server choice.</summary>
        internal class FakeImporter : ILyricMapImporter
        {
            public readonly Bindable<bool> Preference = new Bindable<bool>();

            /// <summary>The remembered isolate vocals choice (backlog 414), on by default like the real setting.</summary>
            public readonly Bindable<bool> IsolatePreference = new Bindable<bool>(true);

            public readonly System.Collections.Concurrent.ConcurrentQueue<(bool Automatic, bool Server, bool Isolate)> CallQueue =
                new System.Collections.Concurrent.ConcurrentQueue<(bool Automatic, bool Server, bool Isolate)>();

            public System.Collections.Generic.List<(bool Automatic, bool Server, bool Isolate)> Calls => CallQueue.ToList();

            public Bindable<bool>? ServerAlignerPreference => Preference.GetBoundCopy();

            public Bindable<bool>? IsolateVocalsPreference => IsolatePreference.GetBoundCopy();

            public (string Artist, string Title) GuessArtistTitle(string audioPath) => ("Some Artist", "A Song");

            public Task<LyricImportResult> BuildOszAsync(string audioPath, string? lyricsPath, string artist, string title, Action<string> progress, CancellationToken token,
                                                         bool useAutomaticAlignment, BeatmapLanguage language, AlignerVocalMode vocalMode = AlignerVocalMode.Aligned,
                                                         bool useServerAligner = false, bool isolateVocals = false)
            {
                CallQueue.Enqueue((useAutomaticAlignment, useServerAligner, isolateVocals));
                return Task.FromResult(LyricImportResult.Fail("stub"));
            }

            public Task<(LyricImportResult Result, string? TimingJson)> ProduceTimingJsonAsync(string audioPath, string lyricsContent, string artist, string title, Action<string> progress,
                                                                                            CancellationToken token, bool useAutomaticAlignment = true, string? language = null,
                                                                                            AlignerVocalMode vocalMode = AlignerVocalMode.Aligned)
                => throw new NotSupportedException();
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
            AddAssert("tooltip says unavailable", () => ((IHasTooltip)screen.AutomaticAlignment).TooltipText.ToString()
                                                           == ImportLyricsScreen.AutomaticAlignmentTooltip(ImportLyricsScreen.ALIGNER_UNAVAILABLE_TOOLTIP, true));
        }
    }
}
