// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.UI;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// KEYBOARD HITSOUNDS: the key-to-sample map (<see cref="KeyboardHitsounds.SampleFor"/>) and the
    /// engine's one announcement per gameplay key (<see cref="TypingEngine.KeyTyped"/>), and which keys sound under each
    /// <see cref="KeyboardHitsoundMode"/> (<see cref="KeyboardHitsoundCues"/>).
    /// </summary>
    public class KeyboardHitsoundsTest
    {
        private static readonly string[] the_set =
        {
            "Keyboard/key-press-1", "Keyboard/key-press-2", "Keyboard/key-press-3", "Keyboard/key-press-4",
            "Keyboard/key-caps", "Keyboard/key-confirm", "Keyboard/key-delete", "Keyboard/key-movement",
        };

        private static string sound(char c) => KeyboardHitsounds.SampleFor(new TypedKey(TypedKeyKind.Character, c));

        [Test]
        public void EveryTypeableKeyHasOneSampleOfTheSet()
        {
            // The typeable surface is printable ASCII (TypeBeatReplayFrame).
            for (char c = ' '; c <= '~'; c++)
                Assert.That(the_set, Does.Contain(sound(c)), $"'{c}'");
        }

        [Test]
        public void TheSampleFollowsWhereTheKeySits()
        {
            Assert.Multiple(() =>
            {
                Assert.That("qwertyuiop".Select(sound), Is.All.EqualTo("Keyboard/key-press-1"), "top letter row");
                Assert.That("asdfghjkl".Select(sound), Is.All.EqualTo("Keyboard/key-press-2"), "home row");
                Assert.That("zxcvbnm".Select(sound), Is.All.EqualTo("Keyboard/key-press-3"), "bottom row");
                Assert.That("0123456789".Select(sound), Is.All.EqualTo("Keyboard/key-press-4"), "number row");
                Assert.That(".,'\"!?-:;()".Select(sound), Is.All.EqualTo("Keyboard/key-caps"), "punctuation");
                Assert.That(sound(' '), Is.EqualTo("Keyboard/key-confirm"), "space bar");
                Assert.That(KeyboardHitsounds.SampleFor(new TypedKey(TypedKeyKind.Backspace)), Is.EqualTo("Keyboard/key-delete"));
                Assert.That(KeyboardHitsounds.SampleFor(new TypedKey(TypedKeyKind.Enter)), Is.EqualTo("Keyboard/key-movement"));
            });
        }

        [Test]
        public void ACapitalSoundsLikeItsLetter()
        {
            for (char c = 'a'; c <= 'z'; c++)
                Assert.That(sound(char.ToUpperInvariant(c)), Is.EqualTo(sound(c)), $"'{c}'");
        }

        private static LyricBeatmap map() => new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata { Artist = "Test", Title = "Song", FolderPath = @"X:\nowhere", AudioFileName = "a.mp3" },
            Lines = new List<LyricLine>
            {
                new LyricLine
                {
                    RawText = "ab cd",
                    StartTime = 1000,
                    EndTime = 60000,
                    SingEndTime = 5000,
                    Units = new[]
                    {
                        new TimedUnit { Text = "ab", StartTime = 1000, EndTime = 2000 },
                        new TimedUnit { Text = "cd", StartTime = 3000, EndTime = 4000 },
                    },
                },
            },
            Granularity = TimingGranularity.Word,
        };

        [Test]
        public void EveryKeyIsAnnouncedOnceWhateverItDoes()
        {
            var engine = new TypingEngine(map());
            var heard = new List<TypedKey>();
            engine.KeyTyped += heard.Add;
            engine.Update(1000);

            engine.ProcessKey('a', 1000);
            engine.ProcessKey('x', 1200); // a wrong key is still a key
            engine.ProcessBackspace();
            engine.ProcessEnter(1500);

            Assert.That(heard, Is.EqualTo(new[]
            {
                new TypedKey(TypedKeyKind.Character, 'a'),
                new TypedKey(TypedKeyKind.Character, 'x'),
                new TypedKey(TypedKeyKind.Backspace),
                new TypedKey(TypedKeyKind.Enter),
            }));
        }

        [Test]
        public void ARebuildIsSilent()
        {
            var engine = new TypingEngine(map());
            int heard = 0;
            engine.KeyTyped += _ => heard++;

            engine.Rebuild(e =>
            {
                e.Update(1000);
                e.ProcessKey('a', 1000);
                e.ProcessKey('b', 1500);
                e.ProcessBackspace();
            });

            Assert.That(heard, Is.Zero);

            engine.ProcessKey('b', 1600);
            Assert.That(heard, Is.EqualTo(1), "announcing resumes once the rebuild is over");
        }

        private static LyricBeatmap syllableMap(params TimedUnit[] units) => new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata { Artist = "Test", Title = "Song", FolderPath = @"X:
owhere", AudioFileName = "a.mp3" },
            Lines = new List<LyricLine>
            {
                new LyricLine
                {
                    RawText = string.Join(' ', units.Select(u => u.Text)),
                    StartTime = 1000,
                    EndTime = 60000,
                    SingEndTime = 9000,
                    Units = units,
                },
            },
            Granularity = TimingGranularity.Word,
        };

        /// <summary>"ap|ple" subdivided by the mapper, then the undivided "cd": three syllables, eight cells.</summary>
        private static TypingEngine appleCd() => new TypingEngine(syllableMap(
            new TimedUnit
            {
                Text = "apple", StartTime = 1000, EndTime = 3000, Source = TimingSource.Explicit,
                SyllableBoundaries = new[] { 2000.0 }, SyllableSplits = new[] { 2 },
            },
            new TimedUnit { Text = "cd", StartTime = 4000, EndTime = 5000, Source = TimingSource.Explicit }));

        /// <summary>Types "apple cd" in time, then a key past the end of the line, Backspace and Enter.</summary>
        private static List<string> typeAppleCd(KeyboardHitsoundMode mode)
        {
            var engine = appleCd();

            using var cues = new KeyboardHitsoundCues(engine, mode);
            var heard = new List<string>();
            cues.Cued += heard.Add;

            double t = 1000;

            foreach (char c in "apple cd")
            {
                engine.Update(t);
                engine.ProcessKey(c, t);
                t += c == ' ' ? 1000 : 400;
            }

            engine.ProcessKey('q', t); // a key past the end of the line
            engine.ProcessBackspace();
            engine.ProcessEnter(t);

            return heard;
        }

        [Test]
        public void OnlyTheFirstCharacterOfEachSyllableSounds()
        {
            Assert.That(typeAppleCd(KeyboardHitsoundMode.PerSyllable), Is.EqualTo(new[] { sound('a'), sound('p'), sound('c') }));
        }

        /// <summary>
        /// Backlog 499: per character, every key that types a lyric cell sounds, the space between the
        /// words included (it types the gap cell, with the space bar's own sample). A key past the end of
        /// the line, Backspace and Enter type no cell and stay silent.
        /// </summary>
        [Test]
        public void EveryCharacterSoundsPerCharacter()
        {
            var heard = typeAppleCd(KeyboardHitsoundMode.PerCharacter);

            Assert.That(heard, Is.EqualTo("apple cd".Select(sound).ToArray()));
            Assert.That(heard.Count(s => s == "Keyboard/key-confirm"), Is.EqualTo(1), "the space bar between the words clicks once");
        }

        [Test]
        public void OffIsSilent()
        {
            Assert.That(typeAppleCd(KeyboardHitsoundMode.Off), Is.Empty);
        }

        /// <summary>Per character still only counts a press's own judgement: a wrong character typed into a cell is silent.</summary>
        [Test]
        public void AWrongCharacterIsSilentPerCharacter()
        {
            var engine = appleCd();

            using var cues = new KeyboardHitsoundCues(engine, KeyboardHitsoundMode.PerCharacter);
            var heard = new List<string>();
            cues.Cued += heard.Add;

            engine.Update(1000);
            engine.ProcessKey('a', 1000);
            engine.Update(1400);
            engine.ProcessKey('x', 1400); // 'p' expected

            Assert.That(heard, Is.EqualTo(new[] { sound('a') }));
        }

        /// <summary>The mode is read on every event, so a change made mid-play applies from the next key.</summary>
        [Test]
        public void TheModeAppliesFromTheNextKey()
        {
            var engine = appleCd();

            using var cues = new KeyboardHitsoundCues(engine, KeyboardHitsoundMode.PerSyllable);
            var heard = new List<string>();
            cues.Cued += heard.Add;

            engine.Update(1000);
            engine.ProcessKey('a', 1000);
            engine.Update(1400);
            engine.ProcessKey('p', 1400);
            engine.Update(1800);
            engine.ProcessKey('p', 1800); // not a syllable start: silent per syllable

            cues.Mode = KeyboardHitsoundMode.PerCharacter;
            engine.Update(2200);
            engine.ProcessKey('l', 2200);

            cues.Mode = KeyboardHitsoundMode.Off;
            engine.Update(2600);
            engine.ProcessKey('e', 2600);

            Assert.That(heard, Is.EqualTo(new[] { sound('a'), sound('p'), sound('l') }));
        }

        [TestCase(KeyboardHitsoundMode.PerSyllable, "so")]
        [TestCase(KeyboardHitsoundMode.PerCharacter, "soo")]
        [TestCase(KeyboardHitsoundMode.Off, "")]
        public void EachHoldTickHeldThroughSounds(KeyboardHitsoundMode mode, string expected)
        {
            // "so" with the 'o' held over [2000, 3500], ticks at 2300 and 2600.
            var hold = new WordHold(1, 2000, 3500) { Ticks = new[] { 2300.0, 2600.0 } };
            var engine = new TypingEngine(syllableMap(new TimedUnit
            {
                Text = "so", StartTime = 1000, EndTime = 3500, Source = TimingSource.Explicit,
                Objects = new WordObjects { Holds = new[] { hold } },
            }));

            using var cues = new KeyboardHitsoundCues(engine, mode);
            var heard = new List<string>();
            cues.Cued += heard.Add;

            engine.Update(1000);
            engine.ProcessKey('s', 1000);
            engine.Update(2000);
            engine.ProcessKey('o', 2000);
            engine.Update(2400);
            engine.ProcessRelease(2500); // let go before the second tick

            engine.Update(4000);

            // Per syllable: the word's first key, then the one tick held through. Per character the
            // held 'o' key sounds too, before its tick.
            Assert.That(heard, Is.EqualTo(expected.Select(sound).ToArray()));
        }
    }
}