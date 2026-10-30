// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Screens.Select;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Backlog 449, the surface the player sees: the UPDATE button is driven straight off
    /// <see cref="BeatmapSetInfo.AllBeatmapsUpToDate"/>, so it stays hidden for a set the server only re-stamped
    /// (a rank change: newer online timestamp, every <c>.osu</c> MD5 unchanged) and shows for a changed MD5 or an
    /// online vocals stem the local copy lacks.
    /// </summary>
    [TestFixture]
    public partial class TestSceneUpdateBeatmapButtonOffer : OsuTestScene
    {
        private const string md5 = "0123456789abcdef0123456789abcdef";
        private const string other_md5 = "fedcba9876543210fedcba9876543210";

        private static readonly DateTimeOffset v2 = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        private PanelUpdateBeatmapButton button = null!;

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("create button", () => Child = button = new PanelUpdateBeatmapButton
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Scale = new osuTK.Vector2(3),
            });

            AddUntilStep("button loaded", () => button.IsLoaded);
        }

        [Test]
        public void TestARankChangeHidesTheButton()
        {
            AddStep("online MD5 differs", () => button.BeatmapSet = set(other_md5));
            AddUntilStep("button shown", () => button.Alpha == 1);

            AddStep("re-stamped online, same files", () => button.BeatmapSet = set(md5));
            AddUntilStep("button hidden", () => button.Alpha == 0);
        }

        [Test]
        public void TestAChangedMd5ShowsTheButton()
        {
            AddStep("online MD5 differs", () => button.BeatmapSet = set(other_md5));
            AddUntilStep("button shown", () => button.Alpha == 1);
        }

        [Test]
        public void TestAMissingStemShowsTheButton()
        {
            AddStep("online stem, none locally", () =>
            {
                var s = set(md5);
                s.RecordOnlineFileManifest(SetFileManifest.VOCALS_STEM);
                button.BeatmapSet = s;
            });
            AddUntilStep("button shown", () => button.Alpha == 1);
        }

        private static BeatmapSetInfo set(string onlineMd5)
        {
            var s = new BeatmapSetInfo { OnlineID = 7 };
            s.Beatmaps.Add(new BeatmapInfo
            {
                MD5Hash = md5,
                OnlineMD5Hash = onlineMd5,
                OnlineID = 11,
                LastOnlineUpdate = v2,
                BeatmapSet = s,
            });
            return s;
        }
    }
}
