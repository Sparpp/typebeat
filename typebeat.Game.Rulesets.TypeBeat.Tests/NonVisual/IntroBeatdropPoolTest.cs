// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Graphics.Sprites;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Localisation;
using typebeat.Game.Screens.Menu;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Covers which beatmap SETS may soundtrack the game intro, which difficulty of a set plays and from
    /// where, and the song select "Use on game intro" set toggle that overrides membership. The important
    /// invariant is that the toggle only ever writes the user-side override: the authored "Intro beatdrop
    /// (ms)" timestamps (map content) are never touched, so an accidental untick cannot destroy a
    /// hand-found value.
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

        #region Set resolution

        [Test]
        public void AnyDifficultyWithABeatdropMakesTheSetACandidate()
        {
            // Only the EASIEST difficulty carries a beatdrop; the set is still in the pool by default.
            var set = new[] { diff("easy", 1.2, 41000), diff("hard", 5.5, null, 60000) };

            var result = pick(set, null, out _);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.Not.Null);
                Assert.That(result!.Value.Difficulty, Is.EqualTo("easy"));
                Assert.That(result.Value.DropTime, Is.EqualTo(41000));
            });
        }

        [Test]
        public void SetWithNoBeatdropIsNotACandidateByDefault()
        {
            var set = new[] { diff("easy", 1.2, null, 30000), diff("hard", 5.5, null, 60000) };

            Assert.That(pick(set, null, out _), Is.Null);
        }

        [Test]
        public void HighestStarBeatdropWinsWithMixedStars()
        {
            // cross-check: the 5.5 star difficulty is the hardest that declares one, so 2000 whatever the order.
            var drops = new (double, double?, int)[] { (2.0, 1000, 5000), (5.5, 2000, 6000), (3.1, 3000, 7000) };

            Assert.Multiple(() =>
            {
                Assert.That(IntroBeatdropPool.ResolveSetDropTime(drops), Is.EqualTo(2000));
                Assert.That(IntroBeatdropPool.ResolveSetDropTime(drops.AsEnumerable().Reverse()), Is.EqualTo(2000));
                Assert.That(IntroBeatdropPool.IndexOfDropDifficulty(drops), Is.EqualTo(1));
            });
        }

        [Test]
        public void HarderDifficultyWithoutABeatdropIsPassedOver()
        {
            var drops = new (double, double?, int)[] { (6.0, null, 9000), (2.0, 2000, 0), (4.0, 4000, 0) };

            Assert.That(IntroBeatdropPool.ResolveSetDropTime(drops), Is.EqualTo(4000));
        }

        [Test]
        public void EqualStarsKeepTheExistingDifficultyOrder()
        {
            var drops = new (double, double?, int)[] { (1.0, 500, 0), (4.0, 1111, 0), (4.0, 2222, 0) };
            var set = new[] { diff("a", 1.0, 500), diff("b", 4.0, 1111), diff("c", 4.0, 2222) };

            Assert.Multiple(() =>
            {
                Assert.That(IntroBeatdropPool.ResolveSetDropTime(drops), Is.EqualTo(1111));
                // the lazy walk agrees: the first of the tied pair is decoded first and wins.
                Assert.That(pick(set, null, out _)!.Value.Difficulty, Is.EqualTo("b"));
            });
        }

        [Test]
        public void UncalculatedRatingSortsLowest()
        {
            var set = new[] { diff("uncalculated", -1, 9999), diff("rated", 0.5, 500) };

            Assert.Multiple(() =>
            {
                // -1 loses to any real rating, even a low one ...
                Assert.That(IntroBeatdropPool.ResolveSetDropTime(new (double, double?, int)[] { (-1, 9999, 0), (0.5, 500, 0) }), Is.EqualTo(500));
                // ... but is still used when it is the only difficulty with a beatdrop ...
                Assert.That(IntroBeatdropPool.ResolveSetDropTime(new (double, double?, int)[] { (-1, 9999, 0), (0.5, null, 0) }), Is.EqualTo(9999));
                // ... and all-uncalculated falls back to the existing order.
                Assert.That(IntroBeatdropPool.ResolveSetDropTime(new (double, double?, int)[] { (-1, 1, 0), (-1, 2, 0) }), Is.EqualTo(1));
                Assert.That(pick(set, null, out _)!.Value.Difficulty, Is.EqualTo("rated"));
            });
        }

        [Test]
        public void OptedInSetWithoutABeatdropFallsBackToTheHighestStarPreview()
        {
            var set = new[] { diff("easy", 2.0, null, 30000), diff("hard", 5.0, null, 45000) };
            var opted = pick(set, true, out _);

            Assert.Multiple(() =>
            {
                Assert.That(IntroBeatdropPool.ResolveSetDropTime(new (double, double?, int)[] { (2.0, null, 30000), (5.0, null, 45000) }), Is.EqualTo(45000));
                // the HIGHEST star difficulty's preview, even when it is unset and a lower one has one: exactly
                // ResolveDropTime's fallback, applied to that difficulty.
                Assert.That(IntroBeatdropPool.ResolveSetDropTime(new (double, double?, int)[] { (2.0, null, 30000), (5.0, null, -1) }), Is.EqualTo(0));
                Assert.That(IntroBeatdropPool.ResolveSetDropTime(Array.Empty<(double, double?, int)>()), Is.EqualTo(0));

                Assert.That(opted!.Value.Difficulty, Is.EqualTo("hard"));
                Assert.That(opted.Value.DropTime, Is.EqualTo(45000));
            });
        }

        [Test]
        public void ExplicitExclusionCostsNoDecode()
        {
            var set = new[] { diff("easy", 1.2, 41000), diff("hard", 5.5, 60000) };

            var result = pick(set, false, out var decoded);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.Null);
                Assert.That(decoded, Is.Empty);
            });
        }

        [Test]
        public void WalkStopsAtTheHighestStarBeatdrop()
        {
            var set = new[] { diff("easy", 1.0, 1000), diff("insane", 5.0, null), diff("hard", 3.0, 3000), diff("normal", 2.0, 2000) };

            var result = pick(set, null, out var decoded);

            Assert.Multiple(() =>
            {
                Assert.That(result!.Value.Difficulty, Is.EqualTo("hard"));
                Assert.That(result.Value.DropTime, Is.EqualTo(3000));
                Assert.That(decoded, Is.EqualTo(new[] { "insane", "hard" }), "descending stars, stopping at the first beatdrop");
            });
        }

        [Test]
        public void UnreadableDifficultyIsSkipped()
        {
            var set = new[] { diff("easy", 1.0, 1000), diff("corrupt", 5.0, 5000, readable: false) };

            Assert.That(pick(set, null, out _)!.Value.Difficulty, Is.EqualTo("easy"));
        }

        [Test]
        public void MigrationDerivesSetInclusionFromDifficultyOverrides()
        {
            Assert.Multiple(() =>
            {
                Assert.That(IntroBeatdropPool.DeriveSetInclusion(new bool?[] { false, true, null }), Is.True, "any true wins");
                Assert.That(IntroBeatdropPool.DeriveSetInclusion(new bool?[] { true }), Is.True);
                Assert.That(IntroBeatdropPool.DeriveSetInclusion(new bool?[] { false, false }), Is.False, "all false stays out");
                Assert.That(IntroBeatdropPool.DeriveSetInclusion(new bool?[] { false, null }), Is.Null, "mixed follows the beatdrops");
                Assert.That(IntroBeatdropPool.DeriveSetInclusion(new bool?[] { null, null }), Is.Null);
                Assert.That(IntroBeatdropPool.DeriveSetInclusion(Array.Empty<bool?>()), Is.Null);
            });
        }

        private record Diff(string Name, double Stars, double? Beatdrop, int Preview, bool Readable);

        private static Diff diff(string name, double stars, double? beatdrop, int preview = -1, bool readable = true) => new Diff(name, stars, beatdrop, preview, readable);

        private static (string Difficulty, double DropTime)? pick(IEnumerable<Diff> set, bool? inclusion, out List<string> decoded)
        {
            var log = new List<string>();
            decoded = log;

            return IntroBeatdropPool.PickFromSet<Diff, string>(set, d => d.Stars, inclusion, d =>
            {
                log.Add(d.Name);
                return d.Readable ? (d.Name, d.Beatdrop, d.Preview) : null;
            });
        }

        #endregion

        private static ToggleMenuItem createMenuItemFor(bool? stored, double? beatdrop, Action<bool?> apply)
            => IntroBeatdropPool.CreateMenuItem(stored, beatdrop.HasValue, apply);

        private static void click(ToggleMenuItem item) => item.Action.Value?.Invoke();
    }
}
