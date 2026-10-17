// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// The era bit table (ReplayEraBits) is the one list every era site reads: the .osr codec, the
// recorder's headers, ReplayEngineFeed.Apply and ClearExtendedEras, the Puppeteer transform's clone
// and the watch path's capture and restore. These tests walk the frame's fields, the engine's flags
// and the public header factories by REFLECTION, so a new era added to any of them without a table
// entry fails here rather than re-deriving stored runs under the wrong rules. ReplayEraBitsGoldenTest
// pins the bytes.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class ReplayEraBitsTest
    {
        private static readonly Beatmap dummy_beatmap = new Beatmap();

        /// <summary>
        /// The engine's settable flags that are NOT eras, each with the reason no replay header
        /// carries it. Anything settable on the engine and absent from both this list and the table
        /// fails <see cref="EveryEngineFlagIsAnEraBitOrDeclaredNotToBe"/>.
        /// </summary>
        private static readonly Dictionary<string, string> engine_flags_that_are_not_eras = new Dictionary<string, string>
        {
            [nameof(TypingEngine.WordShelter)] = "Easy's arm, a mod no stored row predates",
            [nameof(TypingEngine.MashingEnabled)] = "a mod, travels on the score",
            [nameof(TypingEngine.AnyOrderWithinWord)] = "a mod (Dyslexia), travels on the score",
            [nameof(TypingEngine.RushCapExempt)] = "a mod flag (Puppeteer), travels on the score",
            [nameof(TypingEngine.FlexibleCaretFromMod)] = "the retired FT mod's half of bit 5, travels on the score",
            [nameof(TypingEngine.HardRockFromMod)] = "Hard Rock's half of bit 13, travels on the score",
            [nameof(TypingEngine.CaseSensitive)] = "decided by the cell list (Literate), not by a header",
        };

        [Test]
        public void EachWordHoldsContiguousSingleBitsWithinTheCarrierCeiling()
        {
            foreach (var word in new[] { EraWord.Config, EraWord.ConfigExtended })
            {
                var masks = ReplayEraBits.Of(word).Select(b => b.Mask).ToList();

                Assert.That(masks, Is.EqualTo(Enumerable.Range(0, masks.Count).Select(i => 1 << i)), $"{word} bits must be 0, 1, 2... in order");
                Assert.That(masks.Max(), Is.LessThanOrEqualTo(1 << 16), "the .osr decoder throws above 131072");
            }

            Assert.That(ReplayEraBits.ALL.Select(b => b.Name), Is.Unique);
            Assert.That(ReplayEraBits.ALL.Count(b => !b.IsEngineFlag), Is.EqualTo(1), "only the wall-clock axis bit has no engine side");
            Assert.That(ReplayEraBits.ALL.Single(b => !b.IsEngineFlag).Name, Is.EqualTo(nameof(TypeBeatReplayFrame.WallClockFrames)));
        }

        /// <summary>
        /// Every public bool the frame carries is one table entry, and the entry reads and writes
        /// exactly that member and nothing else.
        /// </summary>
        [Test]
        public void EveryFrameFlagHasExactlyOneEntry()
        {
            var members = frameFlags().ToList();

            Assert.That(members.Select(m => m.Name), Is.EquivalentTo(ReplayEraBits.ALL.Select(b => b.Name)));

            foreach (var member in members)
            {
                var frame = new TypeBeatReplayFrame();
                member.Set(frame, true);

                Assert.That(ReplayEraBits.ALL.Where(b => b.Get(frame)).Select(b => b.Name), Is.EqualTo(new[] { member.Name }));
            }

            foreach (var bit in ReplayEraBits.ALL)
            {
                var frame = new TypeBeatReplayFrame();
                bit.Set(frame, true);

                Assert.That(members.Where(m => m.Get(frame)).Select(m => m.Name), Is.EqualTo(new[] { bit.Name }));
            }
        }

        /// <summary>
        /// The two public header factories (what WireCompat, autoplay and the tests build headers
        /// with) take exactly one named parameter per entry of their word, and each sets its own
        /// entry alone.
        /// </summary>
        [TestCase(nameof(TypeBeatReplayFrame.CreateConfigFrame), EraWord.Config)]
        [TestCase(nameof(TypeBeatReplayFrame.CreateExtendedConfigFrame), EraWord.ConfigExtended)]
        public void EveryHeaderFactoryParameterIsOneEntryOfItsWord(string factoryName, EraWord word)
        {
            var factory = typeof(TypeBeatReplayFrame).GetMethod(factoryName, BindingFlags.Public | BindingFlags.Static)!;
            var flagParameters = factory.GetParameters().Where(p => p.ParameterType == typeof(bool)).ToList();
            var bits = ReplayEraBits.Of(word);

            Assert.That(flagParameters.Select(p => p.Name), Is.EquivalentTo(bits.Select(b => camel(b.Name))));

            foreach (var parameter in flagParameters)
            {
                object?[] arguments = factory.GetParameters().Select(p => p.ParameterType == typeof(bool) ? (object)(p == parameter) : 1000.0).ToArray();
                var frame = (TypeBeatReplayFrame)factory.Invoke(null, arguments)!;

                Assert.That(ReplayEraBits.ALL.Where(b => b.Get(frame)).Select(b => camel(b.Name)), Is.EqualTo(new[] { parameter.Name }));
                Assert.That(frame.Time, Is.EqualTo(1000));
            }
        }

        /// <summary>Each bit alone, and all of a word at once, survives ToLegacy then FromLegacy on its own header kind.</summary>
        [Test]
        public void EveryBitSurvivesTheLegacyRoundTrip()
        {
            foreach (var bit in ReplayEraBits.ALL)
            {
                var frame = header(bit.Word);
                bit.Set(frame, true);

                var legacy = frame.ToLegacy(dummy_beatmap);

                Assert.That(legacy.MouseY, Is.EqualTo(bit.Mask), bit.Name);
                Assert.That(set(roundTrip(frame)), Is.EqualTo(new[] { bit.Name }));
            }

            foreach (var word in new[] { EraWord.Config, EraWord.ConfigExtended })
            {
                var frame = header(word);

                foreach (var bit in ReplayEraBits.Of(word))
                    bit.Set(frame, true);

                Assert.That(set(roundTrip(frame)), Is.EquivalentTo(ReplayEraBits.Of(word).Select(b => b.Name)));
            }
        }

        /// <summary>The Puppeteer transform's clone carries every field of both words.</summary>
        [Test]
        public void EveryBitSurvivesTheClone()
        {
            foreach (var bit in ReplayEraBits.ALL)
            {
                var frame = header(bit.Word);
                bit.Set(frame, true);

                var clone = PuppeteerReplayTransform.Clone(frame, 777);

                Assert.That(set(clone), Is.EqualTo(new[] { bit.Name }));
                Assert.That(clone.Time, Is.EqualTo(777));
                Assert.That(clone.Character, Is.EqualTo(frame.Character));
            }

            var full = new TypeBeatReplayFrame(5, 'x');

            foreach (var bit in ReplayEraBits.ALL)
                bit.Set(full, true);

            Assert.That(set(PuppeteerReplayTransform.Clone(full, 5)), Is.EquivalentTo(ReplayEraBits.ALL.Select(b => b.Name)));
        }

        /// <summary>
        /// Playback: a header with one bit set turns exactly that engine flag on, and the same header
        /// with every OTHER bit set turns it off, so no flag is left to whatever the engine held.
        /// </summary>
        [Test]
        public void EveryEngineBitSurvivesApply()
        {
            foreach (var bit in ReplayEraBits.ALL.Where(b => b.IsEngineFlag))
            {
                var on = header(bit.Word);
                bit.Set(on, true);

                var off = header(bit.Word);

                foreach (var other in ReplayEraBits.Of(bit.Word).Where(b => b != bit))
                    other.Set(off, true);

                var engine = freshEngine();

                applyAsPlayback(engine, on);
                Assert.That(bit.Read(engine), Is.True, $"{bit.Name} on");

                applyAsPlayback(engine, off);
                Assert.That(bit.Read(engine), Is.False, $"{bit.Name} off");
            }
        }

        /// <summary>A CONFIG frame clears every second-word era, which is what a replay with no extended header must mean.</summary>
        [Test]
        public void AConfigFrameClearsEverySecondWordEra()
        {
            var engine = freshEngine();

            ReplayEraBits.WriteAll(engine, EraWord.ConfigExtended, true);
            ReplayEngineFeed.Apply(engine, header(EraWord.Config));

            foreach (var bit in ReplayEraBits.Of(EraWord.ConfigExtended))
                Assert.That(bit.Read(engine), Is.False, bit.Name);

            ReplayEraBits.WriteAll(engine, EraWord.ConfigExtended, true);
            ReplayEngineFeed.ClearExtendedEras(engine);

            foreach (var bit in ReplayEraBits.Of(EraWord.ConfigExtended))
                Assert.That(bit.Read(engine), Is.False, bit.Name);
        }

        /// <summary>
        /// Recording: each engine flag alone lands on its own field of the right header, and the
        /// wall-clock axis comes from the caller.
        /// </summary>
        [Test]
        public void EveryEngineBitSurvivesTheRecorderHeaders()
        {
            foreach (var bit in ReplayEraBits.ALL.Where(b => b.IsEngineFlag))
            {
                var engine = freshEngine();

                ReplayEraBits.WriteAll(engine, EraWord.Config, false);
                ReplayEraBits.WriteAll(engine, EraWord.ConfigExtended, false);
                bit.Write(engine, true);

                var (config, extended) = TypeBeatReplayRecorder.HeaderFramesFor(engine, 250, wallClockFrames: false);

                Assert.That(config.IsConfig && extended.IsConfigExtended, Is.True);
                Assert.That(set(roundTrip(bit.Word == EraWord.Config ? config : extended)), Is.EqualTo(new[] { bit.Name }));
                Assert.That(set(roundTrip(bit.Word == EraWord.Config ? extended : config)), Is.Empty);
            }

            var (stamped, _) = TypeBeatReplayRecorder.HeaderFramesFor(freshEngine(), 250, wallClockFrames: true);

            Assert.That(stamped.WallClockFrames, Is.True);
            Assert.That(stamped.Time, Is.EqualTo(250));
        }

        /// <summary>The watch path's capture and restore of the live second-word eras puts every one back.</summary>
        [Test]
        public void TheLiveSecondWordErasSurviveCaptureAndRestore()
        {
            var bits = ReplayEraBits.Of(EraWord.ConfigExtended);

            for (int pattern = 0; pattern < 2; pattern++)
            {
                var engine = freshEngine();

                for (int i = 0; i < bits.Count; i++)
                    bits[i].Write(engine, i % 2 == pattern);

                bool[] live = ReplayEraBits.Capture(engine, EraWord.ConfigExtended);

                ReplayEngineFeed.ClearExtendedEras(engine);
                ReplayEraBits.Restore(engine, EraWord.ConfigExtended, live);

                for (int i = 0; i < bits.Count; i++)
                    Assert.That(bits[i].Read(engine), Is.EqualTo(i % 2 == pattern), bits[i].Name);
            }
        }

        /// <summary>
        /// Every settable flag on the engine is either written by some entry's playback arm or
        /// declared not to be an era, so a new era flag on the engine cannot ship without a bit.
        /// </summary>
        [Test]
        public void EveryEngineFlagIsAnEraBitOrDeclaredNotToBe()
        {
            var flags = typeof(TypingEngine).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                            .Where(p => p.PropertyType == typeof(bool) && p.CanRead && p.SetMethod?.IsPublic == true)
                                            .ToList();

            var written = new HashSet<string>();

            foreach (var bit in ReplayEraBits.ALL.Where(b => b.IsEngineFlag))
            {
                var engine = freshEngine();

                bit.Write(engine, false);
                var before = flags.ToDictionary(p => p.Name, p => (bool)p.GetValue(engine)!);

                bit.Write(engine, true);

                foreach (var flag in flags.Where(p => (bool)p.GetValue(engine)! != before[p.Name]))
                    written.Add(flag.Name);
            }

            var unaccounted = flags.Select(p => p.Name).Where(n => !written.Contains(n) && !engine_flags_that_are_not_eras.ContainsKey(n)).ToList();

            Assert.That(unaccounted, Is.Empty, "an engine flag with no era bit and no reason it needs none");
            Assert.That(engine_flags_that_are_not_eras.Keys.Where(written.Contains), Is.Empty, "declared not an era, yet written by a header");
            Assert.That(engine_flags_that_are_not_eras.Keys, Is.SubsetOf(flags.Select(p => p.Name)), "a stale name in the not-an-era list");
        }

        /// <summary>
        /// Each entry's engine side is the property of its own name, so two entries with swapped
        /// engine delegates cannot pass: writing the bit flips that property and nothing else, and
        /// reading it reads that property. Bit 5 is the documented exception: it writes the snap
        /// and the caret (the caret OR-ed with the FT mod's half) and records the snap.
        /// </summary>
        [Test]
        public void EveryEntryWritesAndReadsTheEnginePropertyOfItsOwnName()
        {
            var flags = typeof(TypingEngine).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                            .Where(p => p.PropertyType == typeof(bool) && p.CanRead && p.SetMethod?.IsPublic == true)
                                            .ToDictionary(p => p.Name);

            foreach (var bit in ReplayEraBits.ALL.Where(b => b.IsEngineFlag))
            {
                bool flexibleLines = bit.Name == nameof(TypeBeatReplayFrame.FlexibleLines);
                string recorded = flexibleLines ? nameof(TypingEngine.FlexibleLineSnap) : bit.Name;
                string[] written = flexibleLines
                    ? new[] { nameof(TypingEngine.FletcherEnabled), nameof(TypingEngine.FlexibleLineSnap) }
                    : new[] { bit.Name };

                Assert.That(flags.ContainsKey(recorded), $"no engine flag named {recorded}");

                foreach (bool value in new[] { true, false })
                {
                    var engine = freshEngine();

                    bit.Write(engine, !value);
                    var before = flags.Values.ToDictionary(p => p.Name, p => (bool)p.GetValue(engine)!);

                    bit.Write(engine, value);

                    Assert.That(flags.Values.Where(p => (bool)p.GetValue(engine)! != before[p.Name]).Select(p => p.Name).OrderBy(n => n),
                        Is.EqualTo(written), $"{bit.Name} written {value}");

                    var reading = freshEngine();
                    flags[recorded].SetValue(reading, value);

                    Assert.That(bit.Read(reading), Is.EqualTo(value), $"{bit.Name} read off {recorded}");
                }
            }
        }

        #region Helpers

        /// <summary>A public bool field or settable property of the frame, read and written by reflection.</summary>
        private sealed class FrameFlag
        {
            public readonly string Name;
            private readonly Func<TypeBeatReplayFrame, bool> get;
            private readonly Action<TypeBeatReplayFrame, bool> set;

            public FrameFlag(string name, Func<TypeBeatReplayFrame, bool> get, Action<TypeBeatReplayFrame, bool> set)
            {
                Name = name;
                this.get = get;
                this.set = set;
            }

            public bool Get(TypeBeatReplayFrame frame) => get(frame);

            public void Set(TypeBeatReplayFrame frame, bool value) => set(frame, value);
        }

        private static IEnumerable<FrameFlag> frameFlags()
        {
            foreach (var field in typeof(TypeBeatReplayFrame).GetFields(BindingFlags.Public | BindingFlags.Instance).Where(f => f.FieldType == typeof(bool)))
                yield return new FrameFlag(field.Name, f => (bool)field.GetValue(f)!, (f, v) => field.SetValue(f, v));

            foreach (var property in typeof(TypeBeatReplayFrame).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                                                 .Where(p => p.PropertyType == typeof(bool) && p.SetMethod?.IsPublic == true))
                yield return new FrameFlag(property.Name, f => (bool)property.GetValue(f)!, (f, v) => property.SetValue(f, v));
        }

        private static TypeBeatReplayFrame header(EraWord word)
            => new TypeBeatReplayFrame(100, word == EraWord.Config ? TypeBeatReplayFrame.CONFIG : TypeBeatReplayFrame.CONFIG_EXTENDED);

        /// <summary>The names of every table entry set on <paramref name="frame"/>.</summary>
        private static List<string> set(TypeBeatReplayFrame frame) => ReplayEraBits.ALL.Where(b => b.Get(frame)).Select(b => b.Name).ToList();

        private static TypeBeatReplayFrame roundTrip(TypeBeatReplayFrame frame)
        {
            var decoded = new TypeBeatReplayFrame();
            decoded.FromLegacy(frame.ToLegacy(dummy_beatmap), dummy_beatmap);
            return decoded;
        }

        /// <summary>A second-word header is applied after the CONFIG frame that clears it, as playback feeds them.</summary>
        private static void applyAsPlayback(TypingEngine engine, TypeBeatReplayFrame frame)
        {
            if (frame.IsConfigExtended)
                ReplayEngineFeed.Apply(engine, header(EraWord.Config));

            ReplayEngineFeed.Apply(engine, frame);
        }

        private static TypingEngine freshEngine() => new TypingEngine(new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata { Artist = "Test", Title = "Song", FolderPath = @"X:\nowhere", AudioFileName = "a.mp3" },
            Lines = new[]
            {
                new LyricLine
                {
                    RawText = "cake",
                    StartTime = 0,
                    EndTime = 60000,
                    SingEndTime = 3000,
                    Units = new[] { new TimedUnit { Text = "cake", StartTime = 1000, EndTime = 3000 } },
                },
            },
            Granularity = TimingGranularity.Line,
        });

        private static string camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];

        #endregion
    }
}
