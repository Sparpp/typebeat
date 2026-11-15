// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Configuration.Presets;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Backlog 484: a share code is a reversible, collision-free encoding of a Gameplay configuration,
    /// prefixed with the schema version it was written in and closed by a check character.
    /// </summary>
    [TestFixture]
    public class GameplayShareCodeTest
    {
        private static PresetSchema latest => GameplayPresetSchemas.LATEST;

        [Test]
        public void RandomConfigurationsRoundTrip()
        {
            var random = new Random(484);

            for (int i = 0; i < 2000; i++)
            {
                int[] digits = randomDigits(random);
                assertRoundTrip(digits);
            }
        }

        [Test]
        public void DomainEdgesRoundTrip()
        {
            int[] zero = new int[latest.Entries.Count];
            int[] top = latest.Entries.Select(e => e.Domain.Radix - 1).ToArray();

            assertRoundTrip(zero);
            assertRoundTrip(top);

            // Each setting alone at its top, and alone at zero under everything else at the top.
            for (int i = 0; i < zero.Length; i++)
            {
                int[] oneHigh = (int[])zero.Clone();
                oneHigh[i] = top[i];
                assertRoundTrip(oneHigh);

                int[] oneLow = (int[])top.Clone();
                oneLow[i] = 0;
                assertRoundTrip(oneLow);
            }
        }

        [Test]
        public void DistinctConfigurationsGetDistinctCodes()
        {
            var random = new Random(4840);
            var codes = new Dictionary<string, string>();

            void add(int[] digits)
            {
                string code = GameplayShareCode.Encode(latest, digits);
                string key = string.Join(',', digits);

                if (codes.TryGetValue(code, out string? existing))
                    Assert.That(existing, Is.EqualTo(key), $"{code} is the code of two configurations");

                codes[code] = key;
            }

            for (int i = 0; i < 2000; i++)
            {
                int[] digits = randomDigits(random);
                add(digits);

                // Every neighbour one step away in a single setting, where the packing is tightest.
                for (int s = 0; s < digits.Length; s++)
                {
                    int[] neighbour = (int[])digits.Clone();
                    neighbour[s] = (neighbour[s] + 1) % latest.Entries[s].Domain.Radix;
                    add(neighbour);
                }
            }

            Assert.That(codes.Values.Distinct().Count(), Is.EqualTo(codes.Count));
        }

        [Test]
        public void CodesAreOneWordOfFixedLength()
        {
            var random = new Random(48400);

            for (int i = 0; i < 200; i++)
            {
                string code = GameplayShareCode.Encode(latest, randomDigits(random));

                Assert.That(code, Has.Length.EqualTo(latest.PayloadLength + 2));
                Assert.That(code.All(c => GameplayShareCode.ALPHABET.Contains(c)), code);
            }
        }

        [Test]
        public void EverySingleMistypedCharacterIsRejected()
        {
            string code = GameplayShareCode.Encode(latest, randomDigits(new Random(7)));

            for (int position = 0; position < code.Length; position++)
            {
                foreach (char replacement in GameplayShareCode.ALPHABET.Where(c => c != code[position]))
                {
                    string typo = code[..position] + replacement + code[(position + 1)..];
                    Assert.That(decode(typo), Is.EqualTo(ShareCodeError.Invalid), typo);
                }
            }
        }

        [Test]
        public void TruncatedOrExtendedCodesAreRejected()
        {
            string code = GameplayShareCode.Encode(latest, randomDigits(new Random(8)));

            Assert.Multiple(() =>
            {
                Assert.That(decode(code[..^1]), Is.EqualTo(ShareCodeError.Invalid));
                Assert.That(decode(code[1..]), Is.Not.EqualTo(ShareCodeError.None));
                Assert.That(decode(code + "0"), Is.EqualTo(ShareCodeError.Invalid));
                Assert.That(decode("1"), Is.EqualTo(ShareCodeError.Invalid));
            });
        }

        [Test]
        public void ANumberBeyondTheSchemaIsRejected()
        {
            // The highest payload the length allows, with a valid check character: well formed, but
            // larger than any configuration the schema describes.
            string body = GameplayShareCode.ALPHABET[latest.Version] + new string('z', latest.PayloadLength);
            Assume.That(GameplayShareCode.DigitsFor(latest.Capacity) == latest.PayloadLength, "the payload has spare room above the capacity");

            Assert.That(decode(body + GameplayShareCode.CheckCharacter(body)), Is.EqualTo(ShareCodeError.Invalid));
        }

        [Test]
        public void ACodeFromANewerSchemaSaysSo()
        {
            var newer = new PresetSchema(latest.Version + 1, latest.Entries.Append(new PresetSchemaEntry("game:SomethingAddedLater", PresetDomain.Bool())));
            string code = GameplayShareCode.Encode(newer, new int[newer.Entries.Count]);

            Assert.That(decode(code), Is.EqualTo(ShareCodeError.NewerVersion));

            // '0' is the reserved escape for a longer version field.
            string escaped = "0" + code[1..^1];
            Assert.That(decode(escaped + GameplayShareCode.CheckCharacter(escaped)), Is.EqualTo(ShareCodeError.NewerVersion));
        }

        [Test]
        public void PastingNoiseAroundACodeIsIgnored()
        {
            int[] digits = randomDigits(new Random(9));
            string code = GameplayShareCode.Encode(latest, digits);

            foreach (string pasted in new[] { $"  {code}\n", $"`{code}`", $"\"{code}\"", code[..10] + "\r\n" + code[10..] })
            {
                Assert.That(GameplayShareCode.TryDecode(pasted, GameplayPresetSchemas.ALL, out _, out int[] decoded), Is.EqualTo(ShareCodeError.None), pasted);
                Assert.That(decoded, Is.EqualTo(digits));
            }
        }

        [Test]
        public void NotACodeAtAll()
        {
            Assert.Multiple(() =>
            {
                Assert.That(decode(""), Is.EqualTo(ShareCodeError.Empty));
                Assert.That(decode("   "), Is.EqualTo(ShareCodeError.Empty));
                Assert.That(decode(null), Is.EqualTo(ShareCodeError.Empty));
                Assert.That(decode("1abc-def"), Is.EqualTo(ShareCodeError.InvalidCharacters));
                Assert.That(decode("hello world this is not a code"), Is.Not.EqualTo(ShareCodeError.None));
            });
        }

        private static void assertRoundTrip(int[] digits)
        {
            string code = GameplayShareCode.Encode(latest, digits);

            var error = GameplayShareCode.TryDecode(code, GameplayPresetSchemas.ALL, out var schema, out int[] decoded);

            Assert.That(error, Is.EqualTo(ShareCodeError.None), code);
            Assert.That(schema, Is.SameAs(latest));
            Assert.That(decoded, Is.EqualTo(digits), code);
        }

        private static ShareCodeError decode(string? text) => GameplayShareCode.TryDecode(text, GameplayPresetSchemas.ALL, out _, out _);

        private static int[] randomDigits(Random random) => latest.Entries.Select(e => random.Next(e.Domain.Radix)).ToArray();
    }
}
