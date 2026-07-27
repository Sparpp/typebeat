// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using NUnit.Framework;
using osu.Framework.Graphics.Sprites;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Localisation;
using typebeat.Game.Screens.Menu;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Covers which beatmaps may soundtrack the game intro, and the song select "Use on game intro"
    /// toggle that overrides it. The important invariant is that the toggle only ever writes the
    /// user-side override: the authored "Intro beatdrop (ms)" timestamp (map content) is never touched,
    /// so an accidental untick cannot destroy a hand-found value.
    /// </summary>
    [TestFixture]
    public class IntroBeatdropPoolTest
    {
        [Test]
        public void MembershipFollowsBeatdropByDefault()
        {
            Assert.Multiple(() =>
            {
                Assert.That(IntroBeatdropPool.IsCandidate(null, true), Is.True);
                Assert.That(IntroBeatdropPool.IsCandidate(null, false), Is.False);
            });
        }

        [Test]
        public void OverrideWinsOverBeatdrop()
        {
            Assert.Multiple(() =>
            {
                // unticked despite an authored beatdrop
                Assert.That(IntroBeatdropPool.IsCandidate(false, true), Is.False);
                // ticked despite no beatdrop
                Assert.That(IntroBeatdropPool.IsCandidate(true, false), Is.True);
                Assert.That(IntroBeatdropPool.IsCandidate(false, false), Is.False);
                Assert.That(IntroBeatdropPool.IsCandidate(true, true), Is.True);
            });
        }

        [Test]
        public void AuthoredBeatdropWinsForDropTime()
        {
            Assert.That(IntroBeatdropPool.ResolveDropTime(45210, 30000), Is.EqualTo(45210));
        }

        [TestCase(30000, 30000)] // preview point is the natural stand-in for an unauthored drop
        [TestCase(-1, 0)] // no preview point: start of the song, always a real position in the track
        [TestCase(0, 0)]
        public void DropTimeFallsBackToPreviewPoint(int previewTime, double expected)
        {
            Assert.That(IntroBeatdropPool.ResolveDropTime(null, previewTime), Is.EqualTo(expected));
        }

        [Test]
        public void OverrideCollapsesWhenBeatdropAlreadyImpliesState()
        {
            Assert.Multiple(() =>
            {
                // re-ticking a beatdrop map (or unticking one without a beatdrop) is just "no override".
                Assert.That(IntroBeatdropPool.InclusionAfterToggle(true, true), Is.Null);
                Assert.That(IntroBeatdropPool.InclusionAfterToggle(false, false), Is.Null);

                Assert.That(IntroBeatdropPool.InclusionAfterToggle(false, true), Is.False);
                Assert.That(IntroBeatdropPool.InclusionAfterToggle(true, false), Is.True);
            });
        }

        [Test]
        public void MenuItemIsALabelledToggle()
        {
            var item = IntroBeatdropPool.CreateMenuItem(null, true, _ => { });

            Assert.Multiple(() =>
            {
                Assert.That(item, Is.InstanceOf<ToggleMenuItem>());
                Assert.That(item.Text.Value, Is.EqualTo(SongSelectStrings.UseOnGameIntro));
                Assert.That(item.GetIconForState(true), Is.EqualTo(FontAwesome.Solid.Check));
                Assert.That(item.GetIconForState(false), Is.Null);
            });
        }

        [Test]
        public void MenuItemReflectsCurrentMembership()
        {
            Assert.Multiple(() =>
            {
                Assert.That(IntroBeatdropPool.CreateMenuItem(null, true, _ => { }).State.Value, Is.True);
                Assert.That(IntroBeatdropPool.CreateMenuItem(null, false, _ => { }).State.Value, Is.False);
                Assert.That(IntroBeatdropPool.CreateMenuItem(false, true, _ => { }).State.Value, Is.False);
                Assert.That(IntroBeatdropPool.CreateMenuItem(true, false, _ => { }).State.Value, Is.True);
            });
        }

        [Test]
        public void SeedingMenuItemStateDoesNotWriteAnything()
        {
            bool written = false;

            IntroBeatdropPool.CreateMenuItem(false, true, _ => written = true);

            Assert.That(written, Is.False, "merely opening the context menu must not touch stored settings");
        }

        [Test]
        public void UntickThenRetickPreservesAuthoredBeatdrop()
        {
            // stands in for the beatmap file's "Intro beatdrop (ms)": nothing in this flow may write to it.
            double? authoredBeatdrop = 45210;
            bool? stored = null;

            var item = createMenuItemFor(stored, authoredBeatdrop, i => stored = i);
            Assert.That(item.State.Value, Is.True);

            click(item);

            Assert.Multiple(() =>
            {
                Assert.That(stored, Is.False);
                Assert.That(item.State.Value, Is.False);
                Assert.That(authoredBeatdrop, Is.EqualTo(45210));
                Assert.That(IntroBeatdropPool.IsCandidate(stored, authoredBeatdrop.HasValue), Is.False);
            });

            // reopening the context menu shows the stored exclusion.
            item = createMenuItemFor(stored, authoredBeatdrop, i => stored = i);
            Assert.That(item.State.Value, Is.False);

            click(item);

            Assert.Multiple(() =>
            {
                Assert.That(stored, Is.Null, "re-ticking should clear the override, not pin it");
                Assert.That(IntroBeatdropPool.IsCandidate(stored, authoredBeatdrop.HasValue), Is.True);
                Assert.That(IntroBeatdropPool.ResolveDropTime(authoredBeatdrop, -1), Is.EqualTo(45210));
            });
        }

        [Test]
        public void TickingAMapWithoutABeatdropOptsItIn()
        {
            double? authoredBeatdrop = null;
            bool? stored = null;

            var item = createMenuItemFor(stored, authoredBeatdrop, i => stored = i);
            Assert.That(item.State.Value, Is.False);

            click(item);

            Assert.Multiple(() =>
            {
                Assert.That(stored, Is.True);
                Assert.That(IntroBeatdropPool.IsCandidate(stored, authoredBeatdrop.HasValue), Is.True);
                // and it starts somewhere real rather than seeking nowhere.
                Assert.That(IntroBeatdropPool.ResolveDropTime(authoredBeatdrop, 30000), Is.EqualTo(30000));
                Assert.That(IntroBeatdropPool.ResolveDropTime(authoredBeatdrop, -1), Is.EqualTo(0));
            });

            item = createMenuItemFor(stored, authoredBeatdrop, i => stored = i);
            Assert.That(item.State.Value, Is.True);

            click(item);

            Assert.Multiple(() =>
            {
                Assert.That(stored, Is.Null);
                Assert.That(IntroBeatdropPool.IsCandidate(stored, authoredBeatdrop.HasValue), Is.False);
            });
        }

        private static ToggleMenuItem createMenuItemFor(bool? stored, double? beatdrop, Action<bool?> apply)
            => IntroBeatdropPool.CreateMenuItem(stored, beatdrop.HasValue, apply);

        private static void click(ToggleMenuItem item) => item.Action.Value?.Invoke();
    }
}
