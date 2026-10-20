// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Newtonsoft.Json;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Resources.Localisation.Web;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// A set with no uploaded package (the server's <c>has_package</c> false) is presented as having no download, in
    /// this game's own words, by the set overlay's notice and every download button; osu's generic availability flag
    /// keeps osu's message.
    /// </summary>
    [TestFixture]
    public class OnlineSetDownloadTest
    {
        [Test]
        public void ASetWithNoPackage_HasNoDownload_AndSaysWhy()
        {
            // The server's wire for such a set: has_package false, and the availability flag it derives from it.
            var set = JsonConvert.DeserializeObject<APIBeatmapSet>(@"{""id"":1,""has_package"":false,""availability"":{""download_disabled"":true}}")!;

            Assert.Multiple(() =>
            {
                Assert.That(set.HasPackage, Is.False);
                Assert.That(OnlineSetDownload.IsDownloadable(set), Is.False);
                Assert.That(OnlineSetDownload.UnavailableNotice(set).ToString(), Is.EqualTo(OnlineSetDownload.NO_PACKAGE_NOTICE));
                Assert.That(OnlineSetDownload.UnavailableTooltip(set).ToString(), Is.EqualTo(OnlineSetDownload.NO_PACKAGE_TOOLTIP));
            });
        }

        [Test]
        public void TheMissingPackage_IsReadOnItsOwn_EvenWithoutTheAvailabilityFlag()
        {
            var set = JsonConvert.DeserializeObject<APIBeatmapSet>(@"{""id"":1,""has_package"":false}")!;

            Assert.Multiple(() =>
            {
                Assert.That(set.Availability.DownloadDisabled, Is.False);
                Assert.That(OnlineSetDownload.IsDownloadable(set), Is.False);
                Assert.That(OnlineSetDownload.UnavailableNotice(set).ToString(), Is.EqualTo(OnlineSetDownload.NO_PACKAGE_NOTICE));
            });
        }

        [Test]
        public void ADisabledSetWithAPackage_KeepsOsusMessage()
        {
            var set = JsonConvert.DeserializeObject<APIBeatmapSet>(@"{""id"":1,""has_package"":true,""availability"":{""download_disabled"":true}}")!;

            Assert.Multiple(() =>
            {
                Assert.That(OnlineSetDownload.IsDownloadable(set), Is.False);
                Assert.That(OnlineSetDownload.UnavailableNotice(set), Is.EqualTo(BeatmapsetsStrings.AvailabilityDisabled));
                Assert.That(OnlineSetDownload.UnavailableTooltip(set), Is.EqualTo(BeatmapsetsStrings.AvailabilityDisabled));
            });
        }

        [Test]
        public void AnOrdinarySet_IsDownloadable_AndAnOlderServerThatSendsNoFlagCountsAsHavingAPackage()
        {
            var withFlag = JsonConvert.DeserializeObject<APIBeatmapSet>(@"{""id"":1,""has_package"":true}")!;
            var withoutFlag = JsonConvert.DeserializeObject<APIBeatmapSet>(@"{""id"":1}")!;

            Assert.Multiple(() =>
            {
                Assert.That(OnlineSetDownload.IsDownloadable(withFlag), Is.True);
                Assert.That(withoutFlag.HasPackage, Is.True);
                Assert.That(OnlineSetDownload.IsDownloadable(withoutFlag), Is.True);
            });
        }
    }
}
