// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
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

        // The background video's OFFSET is the same kind of field as the beatdrop: it syncs a
        // decorative clip to the song and touches neither gameplay nor scoring (the server leaves the
        // video out of its gameplay fingerprint), so re-syncing a video must not cost a map its rank.
        private static string videoAt(string offset) =>
            "type!beat file format v1\n\n[Events]\n//Background and Video events\n0,0,\"bg.jpg\",0,0\nVideo," + offset + ",\"song.mp4\"\n\n[Lyrics]\n{\"version\":2,\"granularity\":\"Word\"}\n{\"text\":\"hi\",\"start_ms\":1000}\n";

        [Test]
        public void VideoOffsetOnlyChangeIsStatusEquivalent()
        {
            var ruleset = new TypeBeatRuleset();

            Assert.Multiple(() =>
            {
                // Setting an offset, either sign, and moving between two offsets.
                Assert.That(ruleset.NativeEncodingsEquivalentForStatus(videoAt("0"), videoAt("1500")), Is.True);
                Assert.That(ruleset.NativeEncodingsEquivalentForStatus(videoAt("0"), videoAt("-1500")), Is.True);
                Assert.That(ruleset.NativeEncodingsEquivalentForStatus(videoAt("-1500"), videoAt("0")), Is.True);
                Assert.That(ruleset.NativeEncodingsEquivalentForStatus(videoAt("1500"), videoAt("-250")), Is.True);
                Assert.That(ruleset.NativeEncodingsEquivalentForStatus(videoAt("0"), videoAt("0")), Is.True);
            });
        }

        [Test]
        public void VideoFileChangeIsStillNotStatusEquivalent()
        {
            var ruleset = new TypeBeatRuleset();

            // The non-vacuity arm: normalising the offset must not normalise the video line away.
            string otherFile = videoAt("1500").Replace("song.mp4", "trailer.mp4", StringComparison.Ordinal);
            string noVideo = videoAt("1500").Replace("Video,1500,\"song.mp4\"\n", string.Empty, StringComparison.Ordinal);

            Assert.Multiple(() =>
            {
                Assert.That(ruleset.NativeEncodingsEquivalentForStatus(videoAt("1500"), otherFile), Is.False, "a different video FILE is a real change");
                Assert.That(ruleset.NativeEncodingsEquivalentForStatus(videoAt("0"), otherFile), Is.False);
                Assert.That(ruleset.NativeEncodingsEquivalentForStatus(videoAt("1500"), noVideo), Is.False, "removing the video is a real change");
                Assert.That(ruleset.NativeEncodingsEquivalentForStatus(videoAt("0"), noVideo), Is.False);
            });
        }
    }
}
