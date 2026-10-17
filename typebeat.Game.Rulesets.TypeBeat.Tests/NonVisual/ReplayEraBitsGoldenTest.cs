// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Replays.Legacy;
using typebeat.Game.Rulesets.TypeBeat.Replays;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The era bits as they travel on disk. <see cref="golden_body"/> is the .osr replay body the
    /// frame set below encodes to, captured from the encoder as it stood before the bit table
    /// replaced the hand-written flag lists, so any change to which bit a flag rides on, to which
    /// header kind carries which word, or to what a non-header frame writes, fails here.
    /// </summary>
    [TestFixture]
    public class ReplayEraBitsGoldenTest
    {
        private static readonly Beatmap dummy_beatmap = new Beatmap();

        private const string golden_body =
            "1000|0|0|0,38|0|1|0,38|0|2|0,35|0|4|0,38|0|8|0,38|0|16|0,35|0|32|0,38|0|64|0," +
            "38|0|128|0,35|0|256|0,38|0|512|0,38|0|1024|0,35|0|2048|0,38|0|4096|0," +
            "38|0|8192|0,35|0|16384|0,38|0|32768|0,38|0|65536|0,35|0|131071|0,38|0|87381|0," +
            "38|0|43690|0,35|1|0|0,38|1|1|0,38|1|2|0,35|1|4|0,38|1|8|0,38|1|16|0,35|1|32|0," +
            "38|1|64|0,38|1|128|0,35|1|256|0,38|1|512|0,38|1|1023|0,35|1|341|0,38|1|682|0," +
            "38|0|129|0,35|1|2|0,38|97|0|0,38|90|0|0,35|32|0|0,38|8|0|0,38|10|0|0,35|228|0|0," +
            "38|12354|0|0,38|65519|0|0,";

        [Test]
        public void TheFrameSetEncodesToTheCapturedBytes()
        {
            string body = encode(goldenFrames());

            Assert.That(body, Is.EqualTo(golden_body), body);
        }

        [Test]
        public void TheCapturedBytesDecodeToFramesThatEncodeBackToThem()
        {
            var decoded = decode(golden_body);

            Assert.That(decoded.Count, Is.EqualTo(goldenFrames().Count));
            Assert.That(encode(decoded), Is.EqualTo(golden_body));
        }

        /// <summary>
        /// Every first-word bit alone and every second-word bit alone, through the public factories
        /// the recorder, autoplay and the wire tests build headers with; all set, none set, two
        /// interleaved patterns per word; fields of the OTHER word set on each header kind (which
        /// must not leak into its word); and ordinary keystrokes with header fields set (which must
        /// encode no flags at all).
        /// </summary>
        private static List<TypeBeatReplayFrame> goldenFrames()
        {
            const bool t = true;

            var frames = new List<TypeBeatReplayFrame>
            {
                TypeBeatReplayFrame.CreateConfigFrame(0, false),
                TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: t),
                TypeBeatReplayFrame.CreateConfigFrame(0, false, spaceSkipsWord: t),
                TypeBeatReplayFrame.CreateConfigFrame(0, false, syllableTiming: t),
                TypeBeatReplayFrame.CreateConfigFrame(0, false, wrongInputOnWordGaps: t),
                TypeBeatReplayFrame.CreateConfigFrame(0, false, strictSpaces: t),
                TypeBeatReplayFrame.CreateConfigFrame(0, false, flexibleLines: t),
                TypeBeatReplayFrame.CreateConfigFrame(0, false, charTimedStretch: t),
                TypeBeatReplayFrame.CreateConfigFrame(0, false, boundedRush: t),
                TypeBeatReplayFrame.CreateConfigFrame(0, false, firstCharTiming: t),
                TypeBeatReplayFrame.CreateConfigFrame(0, false, wallClockFrames: t),
                TypeBeatReplayFrame.CreateConfigFrame(0, false, backDatedSealBreak: t),
                TypeBeatReplayFrame.CreateConfigFrame(0, false, losslessSkipReclaim: t),
                TypeBeatReplayFrame.CreateConfigFrame(0, false, foldsDisplacedClaim: t),
                TypeBeatReplayFrame.CreateConfigFrame(0, false, unhalvedHardRockWindows: t),
                TypeBeatReplayFrame.CreateConfigFrame(0, false, manualNewlines: t),
                TypeBeatReplayFrame.CreateConfigFrame(0, false, newlineOnTypedLetter: t),
                TypeBeatReplayFrame.CreateConfigFrame(0, false, firstLineLeadIn: t),
                TypeBeatReplayFrame.CreateConfigFrame(0, t, t, t, t, t, t, t, t, t, t, t, t, t, t, t, t, t),
                TypeBeatReplayFrame.CreateConfigFrame(0, t, syllableTiming: t, strictSpaces: t, charTimedStretch: t, firstCharTiming: t, backDatedSealBreak: t, foldsDisplacedClaim: t, manualNewlines: t, firstLineLeadIn: t),
                TypeBeatReplayFrame.CreateConfigFrame(0, false, spaceSkipsWord: t, wrongInputOnWordGaps: t, flexibleLines: t, boundedRush: t, wallClockFrames: t, losslessSkipReclaim: t, unhalvedHardRockWindows: t, newlineOnTypedLetter: t),
                TypeBeatReplayFrame.CreateExtendedConfigFrame(0),
                TypeBeatReplayFrame.CreateExtendedConfigFrame(0, rushCapCostsAccuracy: t),
                TypeBeatReplayFrame.CreateExtendedConfigFrame(0, inputEra2: t),
                TypeBeatReplayFrame.CreateExtendedConfigFrame(0, authoredSyllablesOnly: t),
                TypeBeatReplayFrame.CreateExtendedConfigFrame(0, alignSubdivisionTargets: t),
                TypeBeatReplayFrame.CreateExtendedConfigFrame(0, earlyFinish: t),
                TypeBeatReplayFrame.CreateExtendedConfigFrame(0, japaneseRomajiInput: t),
                TypeBeatReplayFrame.CreateExtendedConfigFrame(0, japaneseWordTiming: t),
                TypeBeatReplayFrame.CreateExtendedConfigFrame(0, japaneseInputEra2: t),
                TypeBeatReplayFrame.CreateExtendedConfigFrame(0, japaneseInputEra3: t),
                TypeBeatReplayFrame.CreateExtendedConfigFrame(0, keyTimeJudgement: t),
                TypeBeatReplayFrame.CreateExtendedConfigFrame(0, t, t, t, t, t, t, t, t, t, t),
                TypeBeatReplayFrame.CreateExtendedConfigFrame(0, rushCapCostsAccuracy: t, authoredSyllablesOnly: t, earlyFinish: t, japaneseWordTiming: t, japaneseInputEra3: t),
                TypeBeatReplayFrame.CreateExtendedConfigFrame(0, inputEra2: t, alignSubdivisionTargets: t, japaneseRomajiInput: t, japaneseInputEra2: t, keyTimeJudgement: t),
            };

            var configWithSecondWordFields = TypeBeatReplayFrame.CreateConfigFrame(0, t, boundedRush: t);
            configWithSecondWordFields.RushCapCostsAccuracy = t;
            configWithSecondWordFields.KeyTimeJudgement = t;
            configWithSecondWordFields.JapaneseInputEra3 = t;
            frames.Add(configWithSecondWordFields);

            var extendedWithFirstWordFields = TypeBeatReplayFrame.CreateExtendedConfigFrame(0, inputEra2: t);
            extendedWithFirstWordFields.AllowWrongInput = t;
            extendedWithFirstWordFields.FirstLineLeadIn = t;
            extendedWithFirstWordFields.WallClockFrames = t;
            frames.Add(extendedWithFirstWordFields);

            var keystrokeWithHeaderFields = new TypeBeatReplayFrame(0, 'a') { AllowWrongInput = t, EarlyFinish = t, FirstLineLeadIn = t };
            frames.Add(keystrokeWithHeaderFields);

            frames.Add(new TypeBeatReplayFrame(0, 'Z'));
            frames.Add(new TypeBeatReplayFrame(0, ' '));
            frames.Add(new TypeBeatReplayFrame(0, TypeBeatReplayFrame.BACKSPACE));
            frames.Add(new TypeBeatReplayFrame(0, TypeBeatReplayFrame.ENTER));
            frames.Add(new TypeBeatReplayFrame(0, '\u00e4'));
            frames.Add(new TypeBeatReplayFrame(0, '\u3042'));
            frames.Add(new TypeBeatReplayFrame(0, '\uffef'));

            // Strictly increasing integral times with uneven gaps, so the deltas are pinned too.
            for (int i = 0; i < frames.Count; i++)
                frames[i].Time = 1000 + i * 37 + i % 3;

            return frames;
        }

        /// <summary>The replay body exactly as <c>LegacyScoreEncoder</c> writes it, before compression.</summary>
        private static string encode(IEnumerable<TypeBeatReplayFrame> frames)
        {
            var body = new StringBuilder();
            int lastTime = 0;

            foreach (var frame in frames)
            {
                var legacy = frame.ToLegacy(dummy_beatmap);
                int time = (int)Math.Round(legacy.Time);

                body.Append(FormattableString.Invariant($"{time - lastTime}|{legacy.MouseX ?? 0}|{legacy.MouseY ?? 0}|{(int)legacy.ButtonState},"));
                lastTime = time;
            }

            return body.ToString();
        }

        /// <summary>The body read back the way <c>LegacyScoreDecoder</c> reads it.</summary>
        private static List<TypeBeatReplayFrame> decode(string body)
        {
            var frames = new List<TypeBeatReplayFrame>();
            double time = 0;

            foreach (string entry in body.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = entry.Split('|');

                time += double.Parse(parts[0], CultureInfo.InvariantCulture);

                var legacy = new LegacyReplayFrame(time, float.Parse(parts[1], CultureInfo.InvariantCulture), float.Parse(parts[2], CultureInfo.InvariantCulture),
                    (ReplayButtonState)int.Parse(parts[3], CultureInfo.InvariantCulture));

                var frame = new TypeBeatReplayFrame();
                frame.FromLegacy(legacy, dummy_beatmap);
                frame.Time = time;
                frames.Add(frame);
            }

            return frames;
        }
    }
}
