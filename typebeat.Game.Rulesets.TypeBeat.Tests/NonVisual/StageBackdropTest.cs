// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Models;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Screens.Edit.Setup;
using typebeat.Game.Storyboards;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The lyric stage either reveals the map's image/video behind a readability scrim or paints its
    /// own opaque panel, and the choice is made once at load. Pinned here because the video half of
    /// it cannot be asked of the [Events] line alone: <see cref="StoryboardVideo"/> is drawable
    /// unconditionally, so a map whose video FILE is missing (an audio-only download of a video map,
    /// a set whose video was deleted) used to get the translucent treatment over plain black.
    /// </summary>
    [TestFixture]
    public class StageBackdropTest
    {
        [Test]
        public void AVideoWhoseFileIsMissingIsNotABackdrop()
        {
            var storyboard = storyboardWithVideo("song.mp4", videoInSet: false);

            Assert.Multiple(() =>
            {
                Assert.That(StageBackdrop.HasRenderableContent(storyboard), Is.False, "there is no file to draw");
                Assert.That(StageBackdrop.HasBackdrop(null, storyboard, showStoryboard: true), Is.False, "so the stage keeps its opaque panel");

                // The event itself is untouched: the map still declares a video (scores keep their
                // beatmap identity), it just cannot be shown.
                Assert.That(storyboard.PrimaryVideo, Is.Not.Null);
                Assert.That(storyboard.HasDrawable, Is.True, "the pin only means something while this still says yes");
            });
        }

        [Test]
        public void AVideoWhoseFileIsPresentIsABackdrop()
        {
            var storyboard = storyboardWithVideo("song.mp4", videoInSet: true);

            Assert.Multiple(() =>
            {
                Assert.That(StageBackdrop.HasRenderableContent(storyboard), Is.True);
                Assert.That(StageBackdrop.HasBackdrop(null, storyboard, showStoryboard: true), Is.True);

                // "beatmap storyboard/video" off hides the video, and nothing else is left to show.
                Assert.That(StageBackdrop.HasBackdrop(null, storyboard, showStoryboard: false), Is.False);
            });
        }

        [Test]
        public void ABackgroundImageIsABackdropWhateverTheVideoDoes()
        {
            var storyboard = storyboardWithVideo("song.mp4", videoInSet: false);

            Assert.Multiple(() =>
            {
                // The image is drawn by the player regardless of the storyboard setting.
                Assert.That(StageBackdrop.HasBackdrop("bg.jpg", storyboard, showStoryboard: true), Is.True);
                Assert.That(StageBackdrop.HasBackdrop("bg.jpg", storyboard, showStoryboard: false), Is.True);
            });
        }

        [Test]
        public void NoStoryboardAndNoImageIsTheOpaquePanel()
        {
            Assert.Multiple(() =>
            {
                Assert.That(StageBackdrop.HasRenderableContent(null), Is.False);
                Assert.That(StageBackdrop.HasRenderableContent(new Storyboard()), Is.False);
                Assert.That(StageBackdrop.HasBackdrop(null, null, showStoryboard: true), Is.False);
                Assert.That(StageBackdrop.HasBackdrop(string.Empty, new Storyboard(), showStoryboard: true), Is.False);
            });
        }

        /// <summary>
        /// A storyboard carrying one video event, attached to a beatmap set that either does or does
        /// not hold the file, which is exactly the difference between a full and an audio-only
        /// download of the same map.
        /// </summary>
        private static Storyboard storyboardWithVideo(string filename, bool videoInSet)
        {
            var beatmapInfo = new BeatmapInfo(new TypeBeatRuleset().RulesetInfo);
            var setInfo = new BeatmapSetInfo(new[] { beatmapInfo });
            beatmapInfo.BeatmapSet = setInfo;

            setInfo.Files.Add(new RealmNamedFileUsage(new RealmFile { Hash = "0123456789abcdef" }, "map.osu"));
            setInfo.Files.Add(new RealmNamedFileUsage(new RealmFile { Hash = "fedcba9876543210" }, "audio.mp3"));

            if (videoInSet)
                setInfo.Files.Add(new RealmNamedFileUsage(new RealmFile { Hash = "abcdef0123456789" }, filename));

            var storyboard = new Storyboard { BeatmapInfo = beatmapInfo };
            ResourcesSection.ApplyVideoChange(storyboard, filename);

            return storyboard;
        }
    }
}
