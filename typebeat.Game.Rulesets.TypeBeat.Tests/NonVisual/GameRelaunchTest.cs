// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Framework;
using typebeat.Game.Utils;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The restart contract: <c>RestartAppWhenExited</c> is a PROMISE, and callers spend real state on it
    /// (the beatdrop demo throws away the editor session, the migration screen has already moved the data
    /// path). It was previously answered "yes" unconditionally on desktop while queueing an updater restart
    /// that a build the updater never installed cannot perform, so the game exited and never came back.
    /// The decision is tested here; the spawn and the wait are the only parts a test cannot reach.
    /// </summary>
    [TestFixture]
    public class GameRelaunchTest
    {
        private const string app = "type!beat";
        private const string executable = @"C:\games\type!beat\type!beat.exe";

        private static readonly Func<string, bool> present = _ => true;
        private static readonly Func<string, bool> absent = _ => false;

        #region What the game can actually promise

        [Test]
        public void AWindowsBuildWithNoUpdaterStillRestartsThroughItsOwnExecutable()
        {
            // The configuration the bug was found in: a development build (0.x, no updater install beside
            // it). There is no installer to restart through, but the executable is right there, so the
            // promise can be kept and the answer is yes.
            Assert.That(GameRelaunch.Decide(false, RuntimeInfo.Platform.Windows, executable, app, present), Is.EqualTo(RelaunchMethod.OwnExecutable));
        }

        [Test]
        public void TheUpdaterIsPreferredWhereverItInstalledTheGame()
        {
            // It waits for this process itself and knows where the installed copy lives, so it wins over
            // starting a path we guessed at, on every platform.
            Assert.Multiple(() =>
            {
                Assert.That(GameRelaunch.Decide(true, RuntimeInfo.Platform.Windows, executable, app, present), Is.EqualTo(RelaunchMethod.Updater));
                Assert.That(GameRelaunch.Decide(true, RuntimeInfo.Platform.macOS, null, app, absent), Is.EqualTo(RelaunchMethod.Updater));
                Assert.That(GameRelaunch.Decide(true, RuntimeInfo.Platform.Linux, null, null, absent), Is.EqualTo(RelaunchMethod.Updater));
            });
        }

        [TestCase(RuntimeInfo.Platform.Linux)]
        [TestCase(RuntimeInfo.Platform.macOS)]
        [TestCase(RuntimeInfo.Platform.iOS)]
        [TestCase(RuntimeInfo.Platform.Android)]
        public void WithoutTheUpdaterOnlyWindowsClaimsARestart(RuntimeInfo.Platform platform)
        {
            // Starting the executable path again is not reliably right off Windows (an app bundle, an
            // AppImage mount that dies with the process), and half a restart IS the bug being fixed, so
            // these say no and let the caller take its no-restart path.
            Assert.That(GameRelaunch.Decide(false, platform, executable, app, present), Is.EqualTo(RelaunchMethod.None));
        }

        [Test]
        public void ARestartIsRefusedWhenThereIsNothingItCouldStart()
        {
            Assert.Multiple(() =>
            {
                Assert.That(GameRelaunch.Decide(false, RuntimeInfo.Platform.Windows, null, app, present), Is.EqualTo(RelaunchMethod.None),
                    "a process with no executable path names nothing to start");
                Assert.That(GameRelaunch.Decide(false, RuntimeInfo.Platform.Windows, string.Empty, app, present), Is.EqualTo(RelaunchMethod.None));
                Assert.That(GameRelaunch.Decide(false, RuntimeInfo.Platform.Windows, executable, app, absent), Is.EqualTo(RelaunchMethod.None),
                    "an executable that is not on disk cannot be started again");
                Assert.That(GameRelaunch.Decide(false, RuntimeInfo.Platform.Windows, executable, null, present), Is.EqualTo(RelaunchMethod.None),
                    "with no name to check against, the path cannot be confirmed as ours");
            });
        }

        [Test]
        public void ARestartIsRefusedWhenTheProcessIsNotTheGamesOwnExecutable()
        {
            // Run through a toolchain host the process is the host, and starting THAT again would not bring
            // the game back: it would be the same silent no-op the unconditional answer used to hide.
            Assert.Multiple(() =>
            {
                Assert.That(GameRelaunch.Decide(false, RuntimeInfo.Platform.Windows, @"C:\Program Files\dotnet\dotnet.exe", app, present), Is.EqualTo(RelaunchMethod.None));
                // ...while case is not what makes a Windows path somebody else's.
                Assert.That(GameRelaunch.Decide(false, RuntimeInfo.Platform.Windows, @"C:\games\TYPE!BEAT.EXE", app, present), Is.EqualTo(RelaunchMethod.OwnExecutable));
            });
        }

        #endregion

        #region Handing the outgoing process id to the incoming one

        [Test]
        public void TheWaitTargetSurvivesTheRoundTrip()
        {
            string[] remaining = GameRelaunch.TakeWaitTarget(new[] { GameRelaunch.ArgumentFor(4321) }, out int processId);

            Assert.Multiple(() =>
            {
                Assert.That(processId, Is.EqualTo(4321));
                Assert.That(remaining, Is.Empty, "the argument is a restart's own business and must not reach the rest of startup");
            });
        }

        [Test]
        public void OtherArgumentsAreLeftExactlyAsTheyWere()
        {
            // Startup below this reads the command line for its own purposes (an argument present at all
            // suppresses updater setup, and the first is sniffed for a file import), so the ORDER and the
            // content of everything else has to come through untouched.
            string[] args = { @"map.typb", GameRelaunch.ArgumentFor(99), @"--debug-client-id=2" };

            string[] remaining = GameRelaunch.TakeWaitTarget(args, out int processId);

            Assert.Multiple(() =>
            {
                Assert.That(processId, Is.EqualTo(99));
                Assert.That(remaining, Is.EqualTo(new[] { @"map.typb", @"--debug-client-id=2" }));
            });
        }

        [Test]
        public void AnOrdinaryLaunchWaitsForNothing()
        {
            string[] args = { @"map.typb" };

            Assert.Multiple(() =>
            {
                Assert.That(GameRelaunch.TakeWaitTarget(args, out int processId), Is.EqualTo(args));
                Assert.That(processId, Is.Zero);
                Assert.That(GameRelaunch.TakeWaitTarget(Array.Empty<string>(), out int none), Is.Empty);
                Assert.That(none, Is.Zero);
            });
        }

        [TestCase(@"--wait-for-process-exit")]
        [TestCase(@"--wait-for-process-exit=")]
        [TestCase(@"--wait-for-process-exit=nonsense")]
        [TestCase(@"--wait-for-process-exit=0")]
        [TestCase(@"--wait-for-process-exit=-1")]
        public void AWaitTargetThatNamesNoProcessIsNotWaitedOn(string arg)
        {
            // Never a value a restart wrote, so it is dropped rather than guessed at: waiting on a process
            // that was never named would hold up a launch the user made by hand.
            string[] remaining = GameRelaunch.TakeWaitTarget(new[] { arg }, out int processId);

            Assert.Multiple(() =>
            {
                Assert.That(processId, Is.Zero);
                Assert.That(remaining, Is.Empty);
            });
        }

        #endregion

        #region Waiting

        [Test]
        public void WaitingNeverBlocksOnSomethingThatCannotExit()
        {
            // Startup calls this before the host exists, so a wait that cannot end is a game that never
            // appears. Nothing to wait for, and (defensively) ourselves, both return at once.
            var waited = Task.Run(() =>
            {
                GameRelaunch.WaitForProcessExit(0);
                GameRelaunch.WaitForProcessExit(-1);
                GameRelaunch.WaitForProcessExit(Environment.ProcessId);
            });

            Assert.That(waited.Wait(TimeSpan.FromSeconds(5)), Is.True, "waiting must not hold startup up");
        }

        #endregion
    }
}
