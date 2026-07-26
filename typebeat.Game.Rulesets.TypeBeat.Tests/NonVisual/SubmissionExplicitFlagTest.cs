// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using typebeat.Game.Online.API.Requests;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Screens.Edit.Submission;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Pins the explicit content flag's wire shape. The submission request carries it as a single
    /// optional boolean under the key <c>explicit</c>; the server treats absent as false, so old
    /// clients stay compatible, and unknown members are ignored, so old servers do too.
    /// </summary>
    [TestFixture]
    public class SubmissionExplicitFlagTest
    {
        /// <remarks>
        /// "Response" and "CompletionState" are public members of the request base class that have
        /// always ridden along in the serialised body; the server ignores them. They are listed here
        /// so this pin describes what actually goes over the wire.
        /// </remarks>
        private static readonly string[] expected_request_keys =
        {
            "beatmapset_id",
            "beatmaps_to_create",
            "beatmaps_to_keep",
            "target",
            "notify_on_discussion_replies",
            "explicit",
            "Response",
            "CompletionState",
        };

        [TestCase(true)]
        [TestCase(false)]
        public void NewSetSendsExplicitFlag(bool isExplicit)
        {
            var settings = new BeatmapSubmissionSettings();
            settings.ExplicitContent.Value = isExplicit;

            var json = serialise(PutBeatmapSetRequest.CreateNew(1, settings));

            Assert.That(json["explicit"]!.Value<bool>(), Is.EqualTo(isExplicit));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ExistingSetSendsExplicitFlag(bool isExplicit)
        {
            var settings = new BeatmapSubmissionSettings();
            settings.ExplicitContent.Value = isExplicit;

            var json = serialise(PutBeatmapSetRequest.UpdateExisting(777, new uint[] { 1, 2 }, 0, settings));

            Assert.That(json["explicit"]!.Value<bool>(), Is.EqualTo(isExplicit));
        }

        [Test]
        public void UntouchedSettingsSendFalse()
        {
            var json = serialise(PutBeatmapSetRequest.CreateNew(1, new BeatmapSubmissionSettings()));

            Assert.That(json["explicit"]!.Value<bool>(), Is.False);
        }

        [Test]
        public void RequestCarriesNoOtherNewFields()
        {
            var json = serialise(PutBeatmapSetRequest.CreateNew(1, new BeatmapSubmissionSettings()));

            Assert.That(json.Properties().Select(p => p.Name), Is.EquivalentTo(expected_request_keys));
        }

        [Test]
        public void ExplicitChoiceDoesNotDisturbOtherChoices()
        {
            var settings = new BeatmapSubmissionSettings();
            settings.Target.Value = BeatmapSubmissionTarget.Pending;
            settings.NotifyOnDiscussionReplies.Value = true;
            settings.ExplicitContent.Value = true;

            var json = serialise(PutBeatmapSetRequest.CreateNew(3, settings));

            Assert.Multiple(() =>
            {
                Assert.That(json["target"]!.Value<string>(), Is.EqualTo("Pending"));
                Assert.That(json["notify_on_discussion_replies"]!.Value<bool>(), Is.True);
                Assert.That(json["beatmaps_to_create"]!.Value<uint>(), Is.EqualTo(3u));
                Assert.That(json["explicit"]!.Value<bool>(), Is.True);
            });
        }

        /// <summary>
        /// The set responses the game reads back are not required to report the flag at all; when they
        /// do, either the submission contract's key or the legacy osu-web one is accepted, and unknown
        /// members must never make deserialisation throw.
        /// </summary>
        [TestCase(@"{""id"":1}", false)]
        [TestCase(@"{""id"":1,""explicit"":true}", true)]
        [TestCase(@"{""id"":1,""explicit"":false}", false)]
        [TestCase(@"{""id"":1,""nsfw"":true}", true)]
        [TestCase(@"{""id"":1,""explicit"":true,""nsfw"":true}", true)]
        [TestCase(@"{""id"":1,""explicit"":true,""some_future_field"":""hello""}", true)]
        public void BeatmapSetResponseReadsExplicitFlag(string body, bool expected)
        {
            var set = JsonConvert.DeserializeObject<APIBeatmapSet>(body);

            Assert.That(set, Is.Not.Null);
            Assert.That(set!.HasExplicitContent, Is.EqualTo(expected));
        }

        [Test]
        public void BeatmapSetResponseDoesNotEchoAliasOnSerialisation()
        {
            string json = JsonConvert.SerializeObject(new APIBeatmapSet { HasExplicitContent = true });

            Assert.Multiple(() =>
            {
                Assert.That(json, Does.Contain(@"""nsfw"":true"));
                Assert.That(json, Does.Not.Contain(@"""explicit"""));
            });
        }

        private static JObject serialise(PutBeatmapSetRequest request) => JObject.Parse(JsonConvert.SerializeObject(request));
    }
}
