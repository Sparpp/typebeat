// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using typebeat.Game.Online;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Debug builds play against the production server but never submit scores to it; any other server (the
    /// TYPEBEAT_API_URL dev workflow) takes submissions from any build.
    /// </summary>
    [TestFixture]
    public class ScoreSubmissionGateTest
    {
        [TestCase(TypebeatEndpointConfiguration.PRODUCTION_ROOT)]
        [TestCase(TypebeatEndpointConfiguration.PRODUCTION_BSS_ROOT)] // where the API fails over to mid-session
        [TestCase("https://TYPEBEAT.SH")]
        [TestCase("https://typebeat.mingda.sh")] // the pre-move host, which the server still answers on
        [TestCase("https://bss.typebeat.mingda.sh")]
        [TestCase("not a url")] // fails closed
        [TestCase(null)]
        public void ADevelopmentBuildCannotSubmitToProduction(string? apiRoot)
        {
            Assert.Multiple(() =>
            {
                Assert.That(ScoreSubmissionGate.AllowsSubmission(true, apiRoot), Is.False);
                Assert.That(ScoreSubmissionGate.AllowsSubmission(false, apiRoot), Is.True, "release builds are never gated");
            });
        }

        [TestCase("http://localhost:5089")]
        [TestCase("http://127.0.0.1:5089")]
        [TestCase("https://staging.example.com")]
        [TestCase("https://typebeat.sh.example.com")] // the production name as a label, not as the host
        [TestCase("https://nottypebeat.sh")]
        public void AnyOtherServerTakesSubmissionsFromAnyBuild(string apiRoot)
        {
            Assert.Multiple(() =>
            {
                Assert.That(ScoreSubmissionGate.AllowsSubmission(true, apiRoot), Is.True);
                Assert.That(ScoreSubmissionGate.AllowsSubmission(false, apiRoot), Is.True);
            });
        }

        /// <summary>
        /// The headless test API (<see cref="Game.Online.API.DummyAPIAccess"/>) targets loopback, so the debug
        /// test runs keep exercising the token and submission paths.
        /// </summary>
        [Test]
        public void TheTestApiIsNotProduction()
            => Assert.That(ScoreSubmissionGate.IsProductionRoot(new Game.Online.API.DummyAPIAccess().Endpoints.APIUrl), Is.False);

        /// <summary>
        /// The test assembly runs against a debug build of the game (the gate script builds Debug), which is the
        /// configuration this gate exists for.
        /// </summary>
        [Test]
        public void ThisBuildIsADevelopmentBuild()
        {
#if DEBUG
            Assert.That(ScoreSubmissionGate.IsDevelopmentBuild, Is.True);
#else
            Assert.That(ScoreSubmissionGate.IsDevelopmentBuild, Is.False);
#endif
        }
    }
}
