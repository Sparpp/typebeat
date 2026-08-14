// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Screens.Menu;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Covers the editor's "demo the beatdrop" button: what it says, and the order it does things in.
    /// The animation itself cannot be gated here (it needs the game client), so what is pinned is the part
    /// that is decidable without one: a map with no beatdrop demos nothing at all, the save prompt is raised
    /// before anything is played because playing takes the user out of the editor, cancelling that prompt
    /// plays nothing, and the whole path is blind to intro pool membership.
    /// </summary>
    [TestFixture]
    public class IntroBeatdropDemoTest
    {
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

        [Test]
        public void WithoutABeatdropNothingIsPromptedAndNothingIsPlayed()
        {
            var log = new List<string>();

            IntroBeatdropDemo.Request(null, _ => log.Add("prompt"), _ => log.Add("play"));

            Assert.That(log, Is.Empty, "a map with no beatdrop must not even start leaving the editor");
        }

        [Test]
        public void ThePromptIsRaisedBeforeAnythingIsPlayed()
        {
            var log = new List<string>();

            IntroBeatdropDemo.Request(45210,
                confirmed =>
                {
                    log.Add("prompt");
                    confirmed();
                },
                _ => log.Add("play"));

            Assert.That(log, Is.EqualTo(new[] { "prompt", "play" }));
        }

        [Test]
        public void CancellingThePromptPlaysNothing()
        {
            bool prompted = false;
            bool played = false;

            // A cancelled prompt is one that simply never calls back: the editor is left as it was.
            IntroBeatdropDemo.Request(45210, _ => prompted = true, _ => played = true);

            Assert.Multiple(() =>
            {
                Assert.That(prompted, Is.True);
                Assert.That(played, Is.False);
            });
        }

        [Test]
        public void ConfirmingThePromptPlaysTheAuthoredBeatdrop()
        {
            double? played = null;

            IntroBeatdropDemo.Request(45210, confirmed => confirmed(), drop => played = drop);

            Assert.That(played, Is.EqualTo(45210));
        }

        [Test]
        public void TheRequestCarriesTheMapAndTimestampVerbatim()
        {
            var beatmapInfo = new BeatmapInfo();
            var demo = new IntroBeatdropDemo(beatmapInfo, 45210);

            Assert.Multiple(() =>
            {
                Assert.That(demo.BeatmapInfo, Is.SameAs(beatmapInfo));
                Assert.That(demo.DropTime, Is.EqualTo(45210));
            });
        }

        [Test]
        public void DemoIgnoresIntroPoolMembership()
        {
            // The map is explicitly kept out of the intro pool from song select ("Use on game intro"), so a
            // real startup would never pick it...
            bool? inclusion = false;
            const double authored_beatdrop = 45210;

            Assert.That(IntroBeatdropPool.IsCandidate(inclusion, true), Is.False);

            // ...and it demos anyway, on its own authored timestamp: the point is previewing the map you are
            // editing whether or not it has been opted in.
            double? played = null;
            IntroBeatdropDemo.Request(authored_beatdrop, confirmed => confirmed(), drop => played = drop);

            Assert.Multiple(() =>
            {
                Assert.That(IntroBeatdropDemo.CanDemo(authored_beatdrop), Is.True);
                Assert.That(played, Is.EqualTo(authored_beatdrop));
                // and nothing on this path can write the override back: it is never handed one.
                Assert.That(inclusion, Is.False);
            });
        }

        [Test]
        public void DemoDoesNotFallBackToAPreviewPointTheWayThePoolDoes()
        {
            // A map opted into the pool without a beatdrop starts the intro at its preview point instead.
            Assert.That(IntroBeatdropPool.ResolveDropTime(null, 30000), Is.EqualTo(30000));

            // The demo has no such fallback: with nothing authored there is nothing to preview, and playing
            // the intro on a stand-in would be heard as this map's own beatdrop.
            bool played = false;
            IntroBeatdropDemo.Request(null, confirmed => confirmed(), _ => played = true);

            Assert.Multiple(() =>
            {
                Assert.That(IntroBeatdropDemo.CanDemo(null), Is.False);
                Assert.That(played, Is.False);
            });
        }

        [Test]
        public void RequestingTwiceInARowRepeatsTheWholeSequence()
        {
            var log = new List<string>();

            Action<Action> prompt = confirmed =>
            {
                log.Add("prompt");
                confirmed();
            };

            IntroBeatdropDemo.Request(1000, prompt, drop => log.Add($"play {drop}"));
            IntroBeatdropDemo.Request(2000, prompt, drop => log.Add($"play {drop}"));

            Assert.That(log, Is.EqualTo(new[] { "prompt", "play 1000", "prompt", "play 2000" }));
        }
    }
}
