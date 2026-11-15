// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.UI;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// KEYBOARD HITSOUNDS: the key-to-sample map (<see cref="KeyboardHitsounds.SampleFor"/>) and the
    /// engine's one announcement per gameplay key (<see cref="TypingEngine.KeyTyped"/>).
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
    }
}
