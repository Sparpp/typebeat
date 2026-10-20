// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Backlog 415: the Japanese Polyglot SPACE era. Since extended bit 8 an authored original-script
// space between Japanese words is joined away (PolyglotLine.joinJapaneseWords); a replay recorded
// without the bit was played on cells that kept it. The join changes the CELL LIST, so the era is
// known before the playable beatmap is built: TypeBeatModPolyglot.LegacyJapaneseSpaces, stamped on the
// line objects and read by both engine factories, set from the stored replay by the player and
// re-aligned by the headless scorer. These pins restore the cells main had before the join (6a487bad)
// under the legacy era, and pin every seam that carries it.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.ControlPoints;
using typebeat.Game.Replays;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class JapaneseSpaceEraTest
    {
        private static TimedUnit unit(string text, double start, double end, string? original = null, double[]? boundaries = null, int[]? splits = null)
            => new TimedUnit
            {
                Text = text,
                StartTime = start,
                EndTime = end,
                Original = original,
                SyllableBoundaries = boundaries ?? Array.Empty<double>(),
                SyllableSplits = splits ?? Array.Empty<int>(),
            };

        private static TypingLine polyglot(LyricLine l, bool legacy, bool japaneseWordTiming = true)
            => TypingLine.ForMods(l, false, true, "japanese", japaneseWordTiming: japaneseWordTiming, legacyJapaneseSpaces: legacy);

        /// <summary>"tsuki to" over 月 and と with an authored space in the line original.</summary>
        private static LyricLine tsukiTo() => new LyricLine
        {
            RawText = "tsuki to", Original = "月 と", StartTime = 0, EndTime = 6000, SingEndTime = 3000,
            Units = new[] { unit("tsuki", 1000, 2000, "月"), unit("to", 2500, 3000, "と") },
        };

        private static TypeBeatBeatmap beatmapOf(TypeBeatModPolyglot? mod, params LyricLine[] lines)
        {
            var map = new TypeBeatBeatmap();

            for (int i = 0; i < lines.Length; i++)
                map.HitObjects.Add(new TypeBeatHitObject { StartTime = lines[i].StartTime, LineIndex = i, Line = lines[i], Granularity = TimingGranularity.Word });

            mod?.ApplyToBeatmap(map);

            foreach (var hitObject in map.HitObjects)
                hitObject.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty(), CancellationToken.None);

            return map;
        }

        private static int[] nestedCells(IBeatmap map)
            => map.HitObjects.OfType<TypeBeatHitObject>().Single().NestedHitObjects.OfType<TypeBeatCharObject>().Where(c => c.Phase == 0).Select(c => c.CellIndex).ToArray();

        /// <summary>A header, then every typeable cell of <paramref name="cells"/> pressed at its target as committed text.</summary>
        private static Replay run(TypingLine cells, bool era3)
        {
            var r = new Replay();
            r.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true));
            r.Frames.Add(TypeBeatReplayFrame.CreateExtendedConfigFrame(0, japaneseInputEra3: era3));

            foreach (var cell in cells.Cells.Where(c => c.IsTypeable))
                r.Frames.Add(new TypeBeatReplayFrame(Math.Round(cell.TargetTime), cell.Expected));

            return r;
        }

        #region The legacy cells (main 6a487bad expectations)

        [TestCase("月 と", "月", "と")]
        [TestCase("月　と", "月", "と")]
        [TestCase(null, "月 ", "と")]
        [TestCase(null, "月", " と")]
        public void ExplicitJapaneseOriginalSpacesStayTypeable(string? original, string first, string second)
        {
            var units = new[] { unit("tsuki", 1000, 2000, first), unit("to", 2500, 3000, second) };
            var l = new LyricLine { RawText = "tsuki to", Original = original, StartTime = 0, EndTime = 6000, SingEndTime = 3000, Units = units };
            var cells = polyglot(l, legacy: true);

            Assert.Multiple(() =>
            {
                Assert.That(cells.DisplayText, Is.EqualTo("月 と"));
                Assert.That(cells.Words.Count, Is.EqualTo(2));
                Assert.That(cells.Cells.Select(c => c.TargetTime), Is.EqualTo(new[] { 1000.0, 2000.0, 2500.0 }));
                Assert.That(polyglot(l, legacy: false).DisplayText, Is.EqualTo("月と"), "the live era still joins");
            });
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void IntentionalSpacesInsideAJapaneseWordOriginalStayTypeable(bool subdivided, bool fromLine)
        {
            var l = new LyricLine
            {
                RawText = "tsukitenshi", Original = fromLine ? "つき テンシ" : null,
                StartTime = 0, EndTime = 6000, SingEndTime = 3000,
                Units = new[] { unit("tsukitenshi", 1000, 3000, fromLine ? "つきテンシ" : "つき テンシ",
                    subdivided ? new[] { 2000.0 } : null, subdivided ? new[] { 5 } : null) },
            };
            var cells = polyglot(l, legacy: true);
            double seam = 1800;

            Assert.Multiple(() =>
            {
                Assert.That(cells.DisplayText, Is.EqualTo("つき テンシ"));
                Assert.That(cells.Words.Select(w => (w.StartTime, w.EndTime)), Is.EqualTo(new[] { (1000.0, seam), (seam, 3000.0) }));
                Assert.That(cells.Cells.Select(c => c.TargetTime), Is.EqualTo(new[] { 1000.0, (1000 + seam) / 2, seam, seam, seam + (3000 - seam) / 3, seam + 2 * (3000 - seam) / 3 }).Within(1e-9));
            });
        }

        [Test]
        public void LegacyJapaneseReplaySpacesInsideAUnitPreserveSubdivisionsOnBothSides()
        {
            var l = new LyricLine
            {
                RawText = "tsukitenshi", Original = "つき テンシ", StartTime = 0, EndTime = 6000, SingEndTime = 3000,
                Units = new[] { unit("tsukitenshi", 1000, 3000, "つきテンシ", new[] { 1400.0, 2000.0, 2600.0 }, new[] { 3, 5, 8 }) },
            };
            var cells = polyglot(l, legacy: true, japaneseWordTiming: false);

            Assert.Multiple(() =>
            {
                Assert.That(cells.DisplayText, Is.EqualTo("つき テンシ"));
                Assert.That(cells.Source.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 1400.0 }));
                Assert.That(cells.Source.Units[0].SyllableSplits, Is.EqualTo(new[] { 1 }));
                Assert.That(cells.Source.Units[1].SyllableBoundaries, Is.EqualTo(new[] { 2600.0 }));
                Assert.That(cells.Source.Units[1].SyllableSplits, Is.EqualTo(new[] { 2 }));
                Assert.That(cells.Cells.Select(c => c.TargetTime), Is.EqualTo(new[] { 1000.0, 1400.0, 2000.0, 2000.0, 2300.0, 2600.0 }));
            });
        }

        [Test]
        public void AJapaneseRunWithNoAuthoredSpaceJoinsUnderBothEras()
        {
            var l = new LyricLine
            {
                RawText = "tsuki to", StartTime = 0, EndTime = 6000, SingEndTime = 3000,
                Units = new[] { unit("tsuki", 1000, 2000, "月"), unit("to", 2500, 3000, "と") },
            };

            Assert.Multiple(() =>
            {
                Assert.That(polyglot(l, legacy: true).DisplayText, Is.EqualTo("月と"));
                Assert.That(polyglot(l, legacy: false).DisplayText, Is.EqualTo("月と"));
            });
        }

        [Test]
        public void TheEraSwapsKeepTheLegacyCells()
        {
            // Both timing swaps rebuild the line through its factory; the space era is fixed at
            // construction and must survive them, or the cell-by-index copy would read a shorter line.
            var cells = TypingLine.ForMods(tsukiTo(), false, true, "japanese", japaneseCharacterTiming: true, legacyJapaneseSpaces: true);
            var engine = new TypingEngine(new LyricBeatmap
            {
                Metadata = new LyricBeatmapMetadata { Artist = "A", Title = "T", FolderPath = string.Empty, AudioFileName = "a.mp3" },
                Lines = new[] { tsukiTo() },
                Granularity = TimingGranularity.Word,
            }, polyglot: true, polyglotLanguage: "japanese", legacyJapaneseSpaces: true);

            engine.JapaneseCharacterTiming = false;
            engine.JapaneseWordTiming = false;

            Assert.Multiple(() =>
            {
                Assert.That(cells.DisplayText, Is.EqualTo("月 と"));
                Assert.That(engine.Lines[0].DisplayText, Is.EqualTo("月 と"));
                Assert.That(engine.Lines[0].Cells.Select(c => c.TargetTime), Is.EqualTo(new[] { 1000.0, 2000.0, 2500.0 }));
            });
        }

        #endregion

        #region Carrying the era

        [Test]
        public void TheEraIsReadOffExtendedBit8()
        {
            var noHeader = new Replay();
            var noExtended = new Replay { Frames = { TypeBeatReplayFrame.CreateConfigFrame(0, true) } };
            var clear = new Replay { Frames = { TypeBeatReplayFrame.CreateConfigFrame(0, true), TypeBeatReplayFrame.CreateExtendedConfigFrame(0, japaneseInputEra2: true) } };
            var set = new Replay { Frames = { TypeBeatReplayFrame.CreateConfigFrame(0, true), TypeBeatReplayFrame.CreateExtendedConfigFrame(0, japaneseInputEra3: true) } };

            Assert.Multiple(() =>
            {
                Assert.That(TypeBeatModPolyglot.LegacyJapaneseSpacesFor(noHeader), Is.True);
                Assert.That(TypeBeatModPolyglot.LegacyJapaneseSpacesFor(noExtended), Is.True);
                Assert.That(TypeBeatModPolyglot.LegacyJapaneseSpacesFor(clear), Is.True);
                Assert.That(TypeBeatModPolyglot.LegacyJapaneseSpacesFor(set), Is.False);

                var mod = new TypeBeatModPolyglot();
                mod.ApplyToStoredReplay(clear);
                Assert.That(mod.LegacyJapaneseSpaces, Is.True);
                mod.ApplyToStoredReplay(set);
                Assert.That(mod.LegacyJapaneseSpaces, Is.False);
                Assert.That(mod.DeepClone(), Is.TypeOf<TypeBeatModPolyglot>().And.Property(nameof(TypeBeatModPolyglot.LegacyJapaneseSpaces)).False,
                    "not a setting: a clone starts live");
            });
        }

        [TestCase(true)]
        [TestCase(false)]
        public void TheModStampsTheNestedObjectsAndTheLiveFactoryReadsTheStamp(bool legacy)
        {
            var mod = new TypeBeatModPolyglot { LegacyJapaneseSpaces = legacy };
            var map = beatmapOf(mod, tsukiTo());

            using var drawable = new DrawableTypeBeatRuleset(new TypeBeatRuleset(), map, new Mod[] { mod });
            var line = drawable.Engine.Lines[0];

            Assert.Multiple(() =>
            {
                Assert.That(TypeBeatModPolyglot.LanguageFor(map), Is.EqualTo("japanese"));
                Assert.That(map.HitObjects.OfType<TypeBeatHitObject>().Single().LegacyJapaneseSpaces, Is.EqualTo(legacy));
                Assert.That(line.DisplayText, Is.EqualTo(legacy ? "月 と" : "月と"));
                Assert.That(nestedCells(map), Is.EqualTo(Enumerable.Range(0, line.Cells.Count).Where(i => line.Cells[i].IsTypeable).ToArray()),
                    "the engine and the nested scoring objects cut the same cells");
            });
        }

        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        [TestCase(false, false)]
        public void TheScorerRealignsAPlayableBuiltForTheOtherEra(bool builtLegacy, bool era3)
        {
            var map = beatmapOf(new TypeBeatModPolyglot { LegacyJapaneseSpaces = builtLegacy }, tsukiTo());
            var mods = new Mod[] { new TypeBeatModPolyglot() };
            var replay = run(polyglot(tsukiTo(), legacy: !era3), era3);

            var account = TypeBeatReplayScorer.Score(map, mods, replay, TypoRule.Deferred, ComboRestoreRule.OnFix);
            int cells = era3 ? 2 : 3;

            Assert.Multiple(() =>
            {
                Assert.That(map.HitObjects.OfType<TypeBeatHitObject>().Single().LegacyJapaneseSpaces, Is.EqualTo(!era3), "restamped to the replay's era");
                Assert.That(nestedCells(map).Length, Is.EqualTo(cells), era3 ? "月と" : "月, the authored space, と");
                Assert.That(account.MaximumStatistics.GetValueOrDefault(HitResult.Great), Is.EqualTo(cells));
                Assert.That(account.Statistics.GetValueOrDefault(HitResult.Great), Is.EqualTo(cells), "every press lands on the cell it was recorded on");
                Assert.That(account.Statistics.GetValueOrDefault(HitResult.Miss), Is.Zero);
                Assert.That(account.Rank, Is.EqualTo(ScoreRank.X));
            });
        }

        [Test]
        public void ANonJapanesePlayIsNeverRebuilt()
        {
            var cyrillic = new LyricLine
            {
                RawText = "Privet mir", StartTime = 0, EndTime = 6000, SingEndTime = 3000,
                Units = new[] { unit("Privet", 1000, 2000, "Привет"), unit("mir", 2500, 3000, "мир") },
            };
            var map = beatmapOf(new TypeBeatModPolyglot(), cyrillic);
            var nested = map.HitObjects.OfType<TypeBeatHitObject>().Single().NestedHitObjects.ToArray();

            var replay = new Replay { Frames = { TypeBeatReplayFrame.CreateConfigFrame(0, true) } };
            TypeBeatReplayScorer.Score(map, new Mod[] { new TypeBeatModPolyglot() }, replay, TypoRule.Deferred, ComboRestoreRule.OnFix);

            Assert.That(map.HitObjects.OfType<TypeBeatHitObject>().Single().NestedHitObjects, Is.EqualTo(nested).AsCollection, "same objects, not rebuilt");
        }

        #endregion
    }
}
