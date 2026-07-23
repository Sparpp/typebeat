// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using typebeat.Game.Extensions;
using typebeat.Game.Rulesets;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Pins type!beat's online ruleset identity. Without <see cref="ILegacyRuleset"/> the ruleset's
    /// <c>OnlineID</c> resolves to -1, which makes score submission (<c>SoloPlayer</c>) and global
    /// leaderboard fetching (<c>LeaderboardManager</c>) silently no-op: no error, just nothing.
    /// </summary>
    [TestFixture]
    public class TypeBeatRulesetOnlineIdentityTest
    {
        [Test]
        public void RulesetClaimsOnlineId0()
        {
            var ruleset = new TypeBeatRuleset();

            Assert.That(ruleset, Is.InstanceOf<ILegacyRuleset>());
            Assert.That(((ILegacyRuleset)ruleset).LegacyID, Is.EqualTo(0));
            Assert.That(ruleset.RulesetInfo.OnlineID, Is.EqualTo(0));
            Assert.That(ruleset.RulesetInfo.IsLegacyRuleset(), Is.True);
        }

        [Test]
        public void NativeFormatStillPreferredForEditor()
        {
            // The editor picks its change handler by CanEncodeToNativeFormat first (see Editor.cs);
            // if this ever flips, undo/redo would route through the legacy text-diff patcher,
            // which cannot represent the [Lyrics] section.
            Assert.That(new TypeBeatRuleset().CanEncodeToNativeFormat, Is.True);
        }

        // The intro beatdrop is menu-only; a save that only adds/changes/removes it must be treated as
        // equivalent for online status (so BeatmapManager.save keeps a ranked map ranked). Anything else
        // is a real change and must NOT be equivalent (so it still demotes to LocallyModified).
        private const string no_drop =
            "type!beat file format v1\n\n[Lyrics]\n{\"version\":2,\"song_end_ms\":105620,\"granularity\":\"Word\"}\n{\"text\":\"hi\",\"start_ms\":1000}\n";

        private const string with_drop =
            "type!beat file format v1\n\n[Lyrics]\n{\"version\":2,\"song_end_ms\":105620,\"beatdrop_ms\":10968,\"granularity\":\"Word\"}\n{\"text\":\"hi\",\"start_ms\":1000}\n";

        private const string different_drop =
            "type!beat file format v1\n\n[Lyrics]\n{\"version\":2,\"song_end_ms\":105620,\"beatdrop_ms\":22222,\"granularity\":\"Word\"}\n{\"text\":\"hi\",\"start_ms\":1000}\n";

        private const string with_drop_and_content_change =
            "type!beat file format v1\n\n[Lyrics]\n{\"version\":2,\"song_end_ms\":105620,\"beatdrop_ms\":10968,\"granularity\":\"Word\"}\n{\"text\":\"bye\",\"start_ms\":1000}\n";

        [Test]
        public void BeatdropOnlyChangeIsStatusEquivalent()
        {
            var ruleset = new TypeBeatRuleset();

            Assert.Multiple(() =>
            {
                // Adding, removing, or retiming the beatdrop are all cosmetic for online status.
                Assert.That(ruleset.NativeEncodingsEquivalentForStatus(no_drop, with_drop), Is.True);
                Assert.That(ruleset.NativeEncodingsEquivalentForStatus(with_drop, no_drop), Is.True);
                Assert.That(ruleset.NativeEncodingsEquivalentForStatus(with_drop, different_drop), Is.True);
                Assert.That(ruleset.NativeEncodingsEquivalentForStatus(no_drop, no_drop), Is.True);
            });
        }

        [Test]
        public void RealContentChangeIsNotStatusEquivalent()
        {
            var ruleset = new TypeBeatRuleset();

            // A lyric edit alongside the beatdrop is a genuine change; must still demote.
            Assert.That(ruleset.NativeEncodingsEquivalentForStatus(with_drop, with_drop_and_content_change), Is.False);
            Assert.That(ruleset.NativeEncodingsEquivalentForStatus(no_drop, with_drop_and_content_change), Is.False);
        }
    }
}
