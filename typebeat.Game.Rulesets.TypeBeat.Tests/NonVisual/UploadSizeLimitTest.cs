// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using typebeat.Game.Screens.Edit.Submission;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>An oversized beatmap is refused before uploading, with its size and the limit in the message.</summary>
    [TestFixture]
    public class UploadSizeLimitTest
    {
        [Test]
        public void AnUploadAtTheLimitIsAllowed()
            => Assert.That(UploadSizeLimit.TooLargeMessage(UploadSizeLimit.MAX_BYTES), Is.Null);

        [Test]
        public void AnUploadOverTheLimitSaysHowLargeItIsAndWhatFits()
            => Assert.That(UploadSizeLimit.TooLargeMessage(UploadSizeLimit.MAX_BYTES + 5L * 1024 * 1024 / 2),
                Is.EqualTo("Beatmap is too large (102.5 MB). It must be 100 MB or smaller."));
    }
}
