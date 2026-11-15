// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Configuration.Presets;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Backlog 484: frozen share code schemas. A code made by a shipped build must decode to the same
    /// configuration forever, whatever Settings &gt; Gameplay turns into.
    /// </summary>
    [TestFixture]
    public class GameplayPresetSchemaTest
    {
        /// <summary>
        /// A schema version 1 code, frozen when version 1 shipped, and what it holds. If this fails, a
        /// frozen schema was edited: put it back and append a new version instead.
        /// </summary>
        private const string version_1_code = "10y79hBcqqSpMMst0y0Dlpttm3YMwDbNA";

        private static readonly (string Key, object Value)[] version_1_values =
        {
            ("typebeat:SpaceSkipsWord", true),
            ("typebeat:ManualNewlines", false),
            ("typebeat:UseMapFonts", false),
            ("typebeat:UseTypingFontInEditor", false),
            ("typebeat:LyricFontSize", 48m),
            ("typebeat:LineSpacing", 120m),
            ("typebeat:CaretStyle", "Block"),
            ("typebeat:SungCaretStyle", "Outline"),
            ("typebeat:CaretSmoothing", 35m),
            ("typebeat:UseSpaceErrorDot", false),
            ("typebeat:ApproachBars", true),
            ("typebeat:ApproachBarAlignment", 40m),
            ("typebeat:JudgementIndicator", "FadeIn"),
            ("typebeat:IndicatorAlignment", 75m),
            ("typebeat:SyllableFadeInDuration", 1234m),
            ("typebeat:SyllableBrightness", 80m),
            ("typebeat:TextPopIn", false),
            ("typebeat:TextPopInAmount", 12m),
            ("typebeat:ShowSyllableMarkers", false),
            ("typebeat:ShowBackingVocals", false),
            ("typebeat:ShowPaceColours", true),
            ("typebeat:PaceColourMode", "AccelerationBased"),
            ("typebeat:PaceColourMaxChange", 60m),
            ("typebeat:PaceColourOpacityCurve", 30m),
            ("typebeat:PaceColourGradient", 0m),
            ("typebeat:ShowSyncMetric", true),
            ("typebeat:PaceBarReveal", "ColourIn"),
            ("typebeat:JudgementColouredBars", true),
            ("typebeat:LinePushWarningEdges", true),
            ("typebeat:ShowTimingTint", true),
            ("typebeat:TimingTintMinStrength", 10m),
            ("typebeat:TimingTintMaxStrength", 90m),
            ("typebeat:TimingTintEarlyColour", 0x123456),
            ("typebeat:TimingTintLateColour", 0xabcdef),
            ("game:DimLevel", 0.65m),
            ("game:BlurLevel", 0.2m),
            ("game:LightenDuringBreaks", false),
            ("game:ShowStoryboard", false),
            ("game:HUDVisibilityMode", "HideDuringGameplay"),
            ("game:ReplaySettingsOverlay", false),
            ("game:GameplayLeaderboard", false),
            ("game:AlwaysRequireHoldingForPause", true),
            ("game:AlwaysShowHoldForMenuButton", true),
            ("game:ShowHealthDisplayWhenCantFail", false),
        };

        [Test]
        public void TheVersion1FixtureStillDecodes()
        {
            var version1 = GameplayPresetSchemas.Find(1)!;

            Assert.That(version1.Entries.Select(e => e.Key), Is.EqualTo(version_1_values.Select(v => v.Key)), "schema version 1 was edited");

            int[] expected = version1.Entries.Select((e, i) => e.Domain.DigitOf(version_1_values[i].Value)!.Value).ToArray();
            Assert.That(GameplayShareCode.Encode(version1, expected), Is.EqualTo(version_1_code), "schema version 1 encodes differently than when it shipped");

            Assert.That(GameplayShareCode.TryDecode(version_1_code, GameplayPresetSchemas.ALL, out var schema, out int[] digits), Is.EqualTo(ShareCodeError.None));
            Assert.That(schema!.Version, Is.EqualTo(1));

            var values = schema.Entries.Select((e, i) => (e.Key, Value: e.Domain.ValueOf(digits[i]))).ToArray();
            Assert.That(values, Is.EqualTo(version_1_values));
        }

        [Test]
        public void VersionsCountUpFromOne()
        {
            Assert.That(GameplayPresetSchemas.ALL.Select(s => s.Version), Is.EqualTo(Enumerable.Range(1, GameplayPresetSchemas.ALL.Count)));
            Assert.That(GameplayPresetSchemas.LATEST.Version, Is.LessThanOrEqualTo(GameplayShareCode.MAX_VERSION));
        }

        [Test]
        public void ExcludedSettingsAreNeverCovered()
        {
            foreach (var schema in GameplayPresetSchemas.ALL)
                Assert.That(schema.Entries.Select(e => e.Key).Intersect(GameplayPresetSchemas.EXCLUDED.Keys), Is.Empty, $"version {schema.Version}");
        }

        [Test]
        public void ThePrintedSchemaReadsBackTheSame()
        {
            // The coverage test prints a schema to append in this form; it must mean what it says.
            var latest = GameplayPresetSchemas.LATEST;
            string[] lines = latest.ToSource().Split('\n').Skip(1).Select(l => l.Trim().TrimEnd(',', ')').Trim('"')).ToArray();
            var reread = new PresetSchema(latest.Version, lines);

            Assert.That(reread.Entries.Select(e => e.Line), Is.EqualTo(latest.Entries.Select(e => e.Line)));
        }

        [Test]
        public void DomainsSnapAndClamp()
        {
            var range = PresetDomain.Range(0, 1, 0.01m);

            Assert.Multiple(() =>
            {
                Assert.That(range.Radix, Is.EqualTo(101));
                Assert.That(range.DigitOf(0.404m), Is.EqualTo(40), "between steps snaps to the nearest");
                Assert.That(range.DigitOf(0.405m), Is.EqualTo(41), "a midpoint rounds away from zero");
                Assert.That(range.DigitOf(-3m), Is.EqualTo(0));
                Assert.That(range.DigitOf(7m), Is.EqualTo(100));
                Assert.That(range.ValueOf(40), Is.EqualTo(0.4m));
                Assert.That(range.DigitOf(true), Is.Null, "another kind's value is not a value of this one");

                var names = PresetDomain.Enum(new[] { "A", "B" });
                Assert.That(names.DigitOf("B"), Is.EqualTo(1));
                Assert.That(names.DigitOf("C"), Is.Null);

                Assert.That(PresetDomain.Parse("range 24 72 1").Radix, Is.EqualTo(49));
            });
        }

        [Test]
        public void DomainsWithoutAFiniteSizeAreRefused()
        {
            Assert.Multiple(() =>
            {
                Assert.That(() => PresetDomain.Range(0, 1, 0), Throws.ArgumentException);
                Assert.That(() => PresetDomain.Range(0, 1, 1e-20m), Throws.ArgumentException);
                Assert.That(() => PresetDomain.Enum(new List<string>()), Throws.ArgumentException);
            });
        }
    }
}
