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
    /// Backlog 394, the surface the player sees: the UPDATE button fades in for a set whose stored version
    /// is behind the online one even when every <c>.osu</c> MD5 matches (a stem-only version). The alpha is
    /// driven straight off <see cref="BeatmapSetInfo.AllBeatmapsUpToDate"/>, so the predicate is what the
    /// button shows.
    /// </summary>
    [TestFixture]
    public partial class TestSceneUpdateBeatmapButtonVersionSignal : OsuTestScene
    {
        private const string md5 = "0123456789abcdef0123456789abcdef";

        private static readonly DateTimeOffset v1 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
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
        public void TestAStemOnlyVersionShowsTheButton()
        {
            AddStep("behind the online version", () => button.BeatmapSet = setBehind());
            AddUntilStep("button shown", () => button.Alpha == 1);
        }

        [Test]
        public void TestAnUpToDateSetHidesTheButton()
        {
            AddStep("at the online version", () => button.BeatmapSet = upToDate());
            AddUntilStep("button hidden", () => button.Alpha == 0);
        }

        private static BeatmapSetInfo setBehind()
        {
            var set = upToDate();
            set.OnlineVersionLastUpdated = v1;
            set.Beatmaps[0].LastOnlineUpdate = v2;
            return set;
        }

        private static BeatmapSetInfo upToDate()
        {
            var set = new BeatmapSetInfo { OnlineID = 7 };
            set.Beatmaps.Add(new BeatmapInfo
            {
                MD5Hash = md5,
                OnlineMD5Hash = md5,
                OnlineID = 11,
                LastOnlineUpdate = v2,
                BeatmapSet = set,
            });
            set.OnlineVersionLastUpdated = v2;
            return set;
        }
    }
}
