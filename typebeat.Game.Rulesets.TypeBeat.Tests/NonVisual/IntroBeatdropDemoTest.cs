// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using osu.Framework.Platform;
using typebeat.Game.Screens.Menu;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Covers the editor's "demo the beatdrop" button, which reboots the game so its startup intro is
    /// soundtracked by the map being edited. The animation itself cannot be gated here (it needs the game
    /// client, and a restart), so what is pinned is everything decidable without one: a map with no beatdrop
    /// demos nothing at all, the save prompt is raised before anything is armed because a reboot discards the
    /// editor session, cancelling that prompt reboots nothing, the handoff that carries the map across the
    /// restart is written once and cleared when it is consumed, and every way it can go bad (stale, corrupt,
    /// naming a deleted map, or a platform that cannot restart itself) degrades to an ordinary intro.
    /// </summary>
    [TestFixture]
    public class IntroBeatdropDemoTest
    {
        private string tempPath = null!;
        private Storage storage = null!;

        private static readonly DateTimeOffset armed_at = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

        [SetUp]
        public void SetUp()
        {
            tempPath = Path.Combine(Path.GetTempPath(), $"typebeat-beatdrop-demo-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempPath);
            storage = new NativeStorage(tempPath);
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                Directory.Delete(tempPath, true);
            }
            catch
            {
                // best-effort cleanup of the temporary storage.
            }
        }

        #region The button's two states

        [Test]
        public void WithoutABeatdropTheButtonSaysSoInsteadOfItsNormalCaption()
        {
            Assert.Multiple(() =>
            {
                Assert.That(IntroBeatdropDemo.CanDemo(null), Is.False);
                Assert.That(IntroBeatdropDemo.CaptionFor(null).ToString(), Is.EqualTo("no beatdrop set!"));
                Assert.That(IntroBeatdropDemo.CaptionFor(null).ToString(), Is.Not.EqualTo(IntroBeatdropDemo.CAPTION));
            });
        }

        [TestCase(45210)]
        [TestCase(0)] // a beatdrop on the first millisecond is still an authored beatdrop.
        public void WithABeatdropTheButtonCarriesItsNormalCaption(double beatdrop)
        {
            Assert.Multiple(() =>
            {
                Assert.That(IntroBeatdropDemo.CanDemo(beatdrop), Is.True);
                Assert.That(IntroBeatdropDemo.CaptionFor(beatdrop).ToString(), Is.EqualTo(IntroBeatdropDemo.CAPTION));
            });
        }

        #endregion

        #region The save prompt gates the reboot

        [Test]
        public void WithoutABeatdropNothingIsPromptedAndNothingReboots()
        {
            var log = new List<string>();

            IntroBeatdropDemo.Request(null, _ => log.Add("prompt"), _ => log.Add("reboot"));

            Assert.That(log, Is.Empty, "a map with no beatdrop must not even start tearing the editor down");
        }

        [Test]
        public void ThePromptIsRaisedBeforeAnythingReboots()
        {
            var log = new List<string>();

            IntroBeatdropDemo.Request(45210,
                confirmed =>
                {
                    log.Add("prompt");
                    confirmed();
                },
                _ => log.Add("reboot"));

            Assert.That(log, Is.EqualTo(new[] { "prompt", "reboot" }));
        }

        [Test]
        public void CancellingThePromptRebootsNothing()
        {
            bool prompted = false;
            bool rebooted = false;

            // A cancelled prompt (or a save that fails) is one that simply never calls back: the editor is
            // left as it was, and the game stays up.
            IntroBeatdropDemo.Request(45210, _ => prompted = true, _ => rebooted = true);

            Assert.Multiple(() =>
            {
                Assert.That(prompted, Is.True);
                Assert.That(rebooted, Is.False);
            });
        }

        [Test]
        public void ConfirmingThePromptRebootsOnTheAuthoredBeatdrop()
        {
            double? rebootedOn = null;

            IntroBeatdropDemo.Request(45210, confirmed => confirmed(), drop => rebootedOn = drop);

            Assert.That(rebootedOn, Is.EqualTo(45210));
        }

        #endregion

        #region The one-shot handoff

        [Test]
        public void TheHandoffIsWrittenOnConfirmAndCarriesTheMapAndTimestamp()
        {
            var beatmapId = Guid.NewGuid();
            bool exited = false;

            bool started = IntroBeatdropDemo.Reboot(storage, beatmapId, 45210, () => true, () => exited = true, reportFailure, armed_at);

            var handoff = IntroBeatdropDemo.Peek(storage, armed_at);

            Assert.Multiple(() =>
            {
                Assert.That(started, Is.True);
                Assert.That(exited, Is.True, "the reboot has to actually take the game down");
                Assert.That(handoff, Is.Not.Null);
                Assert.That(handoff!.BeatmapId, Is.EqualTo(beatmapId));
                Assert.That(handoff.DropTime, Is.EqualTo(45210));
            });
        }

        [Test]
        public void ConsumingTheHandoffClearsIt()
        {
            var beatmapId = Guid.NewGuid();

            IntroBeatdropDemo.Reboot(storage, beatmapId, 45210, () => true, () => { }, reportFailure, armed_at);

            var first = IntroBeatdropDemo.Consume(storage, armed_at);
            var second = IntroBeatdropDemo.Consume(storage, armed_at);

            Assert.Multiple(() =>
            {
                Assert.That(first, Is.Not.Null);
                Assert.That(first!.BeatmapId, Is.EqualTo(beatmapId));
                // One shot: the launch after a demo is an ordinary one, and the file is gone rather than
                // merely ignored, so nothing can resurrect it.
                Assert.That(second, Is.Null);
                Assert.That(storage.Exists(IntroBeatdropDemo.HANDOFF_FILENAME), Is.False);
            });
        }

        [Test]
        public void AnUnparseableHandoffIsStillCleared()
        {
            // The values likeliest to survive a parse are exactly the corrupt ones, so consumption deletes
            // before it reads: a value this code cannot understand costs one odd startup, not every startup.
            write("this is not a handoff");

            var consumed = IntroBeatdropDemo.Consume(storage, armed_at);

            Assert.Multiple(() =>
            {
                Assert.That(consumed, Is.Null);
                Assert.That(storage.Exists(IntroBeatdropDemo.HANDOFF_FILENAME), Is.False);
            });
        }

        [Test]
        public void PeekingLeavesTheHandoffForTheIntroToConsume()
        {
            // The loader peeks (to force the one intro sequence that times a beatdrop) before the intro
            // screen exists; only the intro screen may consume.
            var beatmapId = Guid.NewGuid();
            IntroBeatdropDemo.Arm(storage, beatmapId, 45210, armed_at);

            Assert.Multiple(() =>
            {
                Assert.That(IntroBeatdropDemo.Peek(storage, armed_at)?.BeatmapId, Is.EqualTo(beatmapId));
                Assert.That(IntroBeatdropDemo.Peek(storage, armed_at)?.BeatmapId, Is.EqualTo(beatmapId));
                Assert.That(IntroBeatdropDemo.Consume(storage, armed_at)?.BeatmapId, Is.EqualTo(beatmapId));
                Assert.That(IntroBeatdropDemo.Peek(storage, armed_at), Is.Null);
            });
        }

        [Test]
        public void NoHandoffAtAllIsSimplyNoDemo()
        {
            Assert.Multiple(() =>
            {
                Assert.That(IntroBeatdropDemo.Consume(storage, armed_at), Is.Null);
                Assert.That(IntroBeatdropDemo.Peek(storage, armed_at), Is.Null);
            });
        }

        [Test]
        public void ClearingBacksOutAnArmedDemo()
        {
            IntroBeatdropDemo.Arm(storage, Guid.NewGuid(), 45210, armed_at);
            IntroBeatdropDemo.Clear(storage);

            Assert.That(IntroBeatdropDemo.Peek(storage, armed_at), Is.Null);
        }

        #endregion

        #region A stale or dangling handoff degrades to a normal intro

        [Test]
        public void AHandoffOlderThanItsLifetimeIsIgnored()
        {
            IntroBeatdropDemo.Arm(storage, Guid.NewGuid(), 45210, armed_at);

            Assert.Multiple(() =>
            {
                // A reboot is immediate, so a handoff still parked much later was stranded rather than
                // requested, and firing it would be a demo out of nowhere.
                Assert.That(IntroBeatdropDemo.Peek(storage, armed_at + IntroBeatdropDemo.HANDOFF_LIFETIME - TimeSpan.FromSeconds(1)), Is.Not.Null);
                Assert.That(IntroBeatdropDemo.Peek(storage, armed_at + IntroBeatdropDemo.HANDOFF_LIFETIME + TimeSpan.FromSeconds(1)), Is.Null);
            });
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase("nonsense")]
        [TestCase("1|not-a-guid|45210|1786363200000")]
        [TestCase("1|00000000-0000-0000-0000-000000000000|45210|1786363200000")] // an empty id names no map.
        [TestCase("1|8bd0f6a9-6f5b-4c1e-93f5-3f0a0d5b9c11|nan|1786363200000")]
        [TestCase("1|8bd0f6a9-6f5b-4c1e-93f5-3f0a0d5b9c11|-5|1786363200000")] // a beatdrop is never negative.
        [TestCase("1|8bd0f6a9-6f5b-4c1e-93f5-3f0a0d5b9c11|45210|not-a-time")]
        [TestCase("1|8bd0f6a9-6f5b-4c1e-93f5-3f0a0d5b9c11|45210|99999999999999999")] // out of DateTimeOffset range.
        [TestCase("1|8bd0f6a9-6f5b-4c1e-93f5-3f0a0d5b9c11|45210")] // a shape from some other version.
        [TestCase("0|8bd0f6a9-6f5b-4c1e-93f5-3f0a0d5b9c11|45210|1786363200000")] // an older version's line.
        [TestCase("2|8bd0f6a9-6f5b-4c1e-93f5-3f0a0d5b9c11|45210|1786363200000")] // a newer version's line.
        public void AMalformedOrForeignHandoffIsIgnoredRatherThanGuessedAt(string contents)
        {
            write(contents);

            Assert.That(IntroBeatdropDemo.Consume(storage, armed_at), Is.Null);
        }

        [Test]
        public void AHandoffNamingAMapThatIsGoneResolvesToNothing()
        {
            var handoff = new IntroBeatdropDemo.Handoff(Guid.NewGuid(), 45210);

            Assert.Multiple(() =>
            {
                // Deleted (or unreadable, which the intro screen also turns into a null lookup).
                Assert.That(IntroBeatdropDemo.Resolve<string>(handoff, _ => null), Is.Null);
                // ...and a lookup that blows up must not take startup down with it.
                Assert.That(IntroBeatdropDemo.Resolve<string>(handoff, _ => throw new InvalidOperationException("realm is gone")), Is.Null);
                // Nothing parked resolves to nothing without a lookup even being attempted.
                Assert.That(IntroBeatdropDemo.Resolve<string>(null, _ => throw new InvalidOperationException("must not be called")), Is.Null);
            });
        }

        [Test]
        public void AHandoffNamingALiveMapResolvesToIt()
        {
            var beatmapId = Guid.NewGuid();
            var handoff = new IntroBeatdropDemo.Handoff(beatmapId, 45210);

            Assert.That(IntroBeatdropDemo.Resolve(handoff, id => id.ToString()), Is.EqualTo(beatmapId.ToString()));
        }

        #endregion

        #region Platforms that cannot restart themselves

        [Test]
        public void APlatformThatCannotRestartRebootsNothingAndLeavesNoHandoffBehind()
        {
            // OsuGameBase.RestartAppWhenExited() returns false off desktop. Quitting anyway would cost the
            // user their editor session and demo nothing, and an armed handoff waiting on a launch that has
            // to be done by hand would fire at some baffling later moment, so both are backed out.
            bool exited = false;
            string? reported = null;

            bool started = IntroBeatdropDemo.Reboot(storage, Guid.NewGuid(), 45210, () => false, () => exited = true, m => reported = m, armed_at);

            Assert.Multiple(() =>
            {
                Assert.That(started, Is.False);
                Assert.That(exited, Is.False);
                Assert.That(storage.Exists(IntroBeatdropDemo.HANDOFF_FILENAME), Is.False);
                Assert.That(IntroBeatdropDemo.Peek(storage, armed_at), Is.Null);
                Assert.That(reported, Is.Not.Null, "the user has to be told why nothing happened");
            });
        }

        [Test]
        public void AHandoffThatCannotBeWrittenRebootsNothing()
        {
            // Storage that cannot be written to must not be followed by a restart: the game would come back
            // up on an ordinary intro, having thrown the editor session away for nothing.
            var unwritable = new UnwritableStorage(tempPath);

            bool restartQueued = false;
            bool exited = false;
            string? reported = null;

            bool started = IntroBeatdropDemo.Reboot(unwritable, Guid.NewGuid(), 45210, () => restartQueued = true, () => exited = true, m => reported = m, armed_at);

            Assert.Multiple(() =>
            {
                Assert.That(started, Is.False);
                Assert.That(restartQueued, Is.False);
                Assert.That(exited, Is.False);
                Assert.That(reported, Is.Not.Null);
            });
        }

        #endregion

        #region The demo overrules the intro pool

        [Test]
        public void TheDemoIgnoresIntroPoolMembership()
        {
            // The map is explicitly kept out of the intro pool from song select ("Use on game intro"), so a
            // real startup would never pick it...
            bool? inclusion = false;
            var beatmapId = Guid.NewGuid();

            Assert.That(IntroBeatdropPool.IsCandidate(inclusion, true), Is.False);

            // ...and it demos anyway, on its own authored timestamp: the point is previewing the map you are
            // editing whether or not it has been opted in.
            IntroBeatdropDemo.Request(45210, confirmed => confirmed(),
                drop => IntroBeatdropDemo.Reboot(storage, beatmapId, drop, () => true, () => { }, reportFailure, armed_at));

            var handoff = IntroBeatdropDemo.Consume(storage, armed_at);

            Assert.Multiple(() =>
            {
                Assert.That(handoff, Is.Not.Null);
                Assert.That(handoff!.BeatmapId, Is.EqualTo(beatmapId));
                Assert.That(handoff.DropTime, Is.EqualTo(45210));
                // and nothing on this path can write the override back: it is never handed one.
                Assert.That(inclusion, Is.False);
            });
        }

        [Test]
        public void TheDemoDoesNotFallBackToAPreviewPointTheWayThePoolDoes()
        {
            // A map opted into the pool without a beatdrop starts the intro at its preview point instead.
            Assert.That(IntroBeatdropPool.ResolveDropTime(null, 30000), Is.EqualTo(30000));

            // The demo has no such fallback: with nothing authored there is nothing to preview, and
            // restarting into an intro on a stand-in would be heard as this map's own beatdrop.
            bool rebooted = false;
            IntroBeatdropDemo.Request(null, confirmed => confirmed(), _ => rebooted = true);

            Assert.Multiple(() =>
            {
                Assert.That(IntroBeatdropDemo.CanDemo(null), Is.False);
                Assert.That(rebooted, Is.False);
            });
        }

        #endregion

        private void write(string contents) => File.WriteAllText(Path.Combine(tempPath, IntroBeatdropDemo.HANDOFF_FILENAME), contents);

        private static void reportFailure(string message) => Assert.Fail($"the demo backed out unexpectedly: {message}");

        /// <summary>
        /// Storage that refuses every path, standing in for a disk that will not take the handoff.
        /// </summary>
        private class UnwritableStorage : NativeStorage
        {
            public UnwritableStorage(string path)
                : base(path)
            {
            }

            public override string GetFullPath(string path, bool createIfNotExisting = false)
                => throw new IOException("storage is unavailable");
        }
    }
}
