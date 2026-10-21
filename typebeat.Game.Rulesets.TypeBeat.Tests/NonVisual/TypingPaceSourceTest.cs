// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Screens.Select;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Song select's pace figures: the server's when its set lookup carries them for this exact version of the
    /// map and no mod changes the pace, the local ones otherwise, so song select prints what the set overlay
    /// and the website print whenever it can and still shows a pace offline.
    /// </summary>
    [TestFixture]
    public class TypingPaceSourceTest
    {
        private const string md5 = "0123456789abcdef0123456789abcdef";

        private static readonly TypingPaceProfile local = new TypingPaceProfile
        {
            WpmCurve = new[] { 10.0, 40, 20 },
            PeakWpm = 40,
            TargetWpm = 30,
            AverageWpm = 25,
        };

        [Test]
        public void ServerFiguresWinWhenPresent()
        {
            var shown = TypingPaceSource.Choose(local, server(), installed(), modsChangePace: false);

            Assert.That(shown, Is.Not.SameAs(local));
            Assert.That(shown!.PeakWpm, Is.EqualTo(95));
            Assert.That(shown.TargetWpm, Is.EqualTo(70));
            Assert.That(shown.AverageWpm, Is.EqualTo(62));
            Assert.That(shown.WpmCurve, Is.EqualTo(new[] { 50.0, 95, 0 }));
        }

        [Test]
        public void OfflineShowsTheLocalFigures()
        {
            // Offline, the lookup completes with no set (or never runs), so there is no online difficulty at all.
            Assert.That(TypingPaceSource.Choose(local, null, installed(), modsChangePace: false), Is.SameAs(local));
        }

        [Test]
        public void ServerWithoutFiguresShowsTheLocalOnes()
        {
            Assert.Multiple(() =>
            {
                Assert.That(TypingPaceSource.Choose(local, server(b => b.PeakWpm = null), installed(), false), Is.SameAs(local), "no peak");
                Assert.That(TypingPaceSource.Choose(local, server(b => b.TargetWpm = null), installed(), false), Is.SameAs(local), "no target");
                Assert.That(TypingPaceSource.Choose(local, server(b => b.AverageWpm = null), installed(), false), Is.SameAs(local), "no average");
                Assert.That(TypingPaceSource.Choose(local, server(b => b.WpmCurve = null), installed(), false), Is.SameAs(local), "no curve");
                Assert.That(TypingPaceSource.Choose(local, server(b => b.WpmCurve = new[] { 0f, 0f }), installed(), false), Is.SameAs(local), "nothing to draw");
            });
        }

        [Test]
        public void ServerFiguresForAnotherVersionAreNotUsed()
        {
            Assert.Multiple(() =>
            {
                Assert.That(TypingPaceSource.Choose(local, server(b => b.Checksum = "ffffffffffffffffffffffffffffffff"), installed(), false), Is.SameAs(local),
                    "a local copy that differs from the online version is paced locally");
                Assert.That(TypingPaceSource.Choose(local, server(b => b.OnlineID = 2), installed(), false), Is.SameAs(local), "another difficulty");

                var unsubmitted = installed();
                unsubmitted.OnlineID = -1;
                Assert.That(TypingPaceSource.Choose(local, server(b => b.OnlineID = -1), unsubmitted, false), Is.SameAs(local), "not online at all");
            });
        }

        [Test]
        public void APaceChangingModShowsTheLocalFigures()
        {
            // The server stores the unmodded map; a rate mod or Literate is only measured here.
            Assert.That(TypingPaceSource.Choose(local, server(), installed(), modsChangePace: true), Is.SameAs(local));
        }

        [Test]
        public void NothingAnywhereShowsNothing()
            => Assert.That(TypingPaceSource.Choose(null, null, installed(), false), Is.Null);

        private static BeatmapInfo installed() => new BeatmapInfo { OnlineID = 1, MD5Hash = md5 };

        private static APIBeatmap server(System.Action<APIBeatmap>? change = null)
        {
            var beatmap = new APIBeatmap
            {
                OnlineID = 1,
                Checksum = md5,
                PeakWpm = 95,
                TargetWpm = 70,
                AverageWpm = 62,
                WpmCurve = new[] { 50f, 95f, 0f },
            };

            change?.Invoke(beatmap);
            return beatmap;
        }
    }
}
