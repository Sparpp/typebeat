// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Backlog 330, the EDITOR half: the line box takes any script and proposes the romanisation,
    /// the two-row word editor overwrites it word by word (and is how an unromanised word gets its
    /// text), the '|' and '&amp;' gestures keep working on the romanised row, the originals survive
    /// every other edit, and the 339 clipboard carries them.
    /// </summary>
    [TestFixture]
    public class OriginalTextEditorTest
    {
        [SetUp]
        public void SetUp() => LyricBeatmapDecoder.Register();

        private static EditorBeatmap createBeatmap(BeatmapLanguage language = BeatmapLanguage.Unspecified)
        {
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.Metadata.Artist = "Op";
            beatmap.Metadata.Title = "Test";
            beatmap.Metadata.AudioFile = "audio.mp3";
            beatmap.Metadata.Language = language;

            addLine(beatmap, 0, "alpha beta", 1000, 3000, 2800, (1000, 1800), (1900, 2800));
            addLine(beatmap, 1, "gamma delta", 3000, 6000, 5500, (3000, 4200), (4300, 5500));

            return new EditorBeatmap(beatmap);
        }

        private static void addLine(Beatmap beatmap, int index, string text, double start, double end, double singEnd, params (double s, double e)[] words)
        {
            string[] tokens = text.Split(' ');

            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = start,
                LineIndex = index,
                Line = new LyricLine
                {
                    RawText = text,
                    StartTime = start,
                    EndTime = end,
                    SingEndTime = singEnd,
                    Units = words.Select((w, i) => new TimedUnit { Text = tokens[i], StartTime = w.s, EndTime = w.e, Source = TimingSource.Explicit }).ToArray(),
                },
                Granularity = TimingGranularity.Word,
            });
        }

        private static TypeBeatHitObject lineAt(EditorBeatmap editorBeatmap, int index) => TypeBeatEditorOperations.OrderedLines(editorBeatmap)[index];

        private static LyricLine reloaded(EditorBeatmap editorBeatmap, int index)
            => OriginalTextFormatTest.Decode(OriginalTextFormatTest.Encode(editorBeatmap)).HitObjects.OfType<TypeBeatHitObject>().OrderBy(h => h.LineIndex).ElementAt(index).Line;

        [Test]
        public void TypingCyrillicIntoTheLineBoxProposesTheRomanisation()
        {
            var editorBeatmap = createBeatmap();
            var line = lineAt(editorBeatmap, 0);

            Assert.That(TypeBeatEditorOperations.SetLineText(editorBeatmap, line, "Привет мир"), Is.True);

            Assert.That(line.Line.RawText, Is.EqualTo("Privet mir"));
            Assert.That(line.Line.Original, Is.EqualTo("Привет мир"));
            Assert.That(line.Line.Units.Select(u => u.Original), Is.EqualTo(new[] { "Привет", "мир" }));

            // Two words over two: every word keeps its hand timing.
            Assert.That(line.Line.Units.Select(u => u.StartTime), Is.EqualTo(new[] { 1000.0, 1900.0 }));

            var saved = reloaded(editorBeatmap, 0);
            Assert.That(saved.Original, Is.EqualTo("Привет мир"));
            Assert.That(saved.Units.Select(u => u.Original), Is.EqualTo(new[] { "Привет", "мир" }));
        }

        /// <summary>The map's own language picks the table: the same letters read differently in Ukrainian.</summary>
        [Test]
        public void TheMapsLanguageSelectsTheTable()
        {
            var editorBeatmap = createBeatmap(BeatmapLanguage.Russian);
            TypeBeatEditorOperations.SetLineText(editorBeatmap, lineAt(editorBeatmap, 0), "гора");
            Assert.That(lineAt(editorBeatmap, 0).Line.RawText, Is.EqualTo("gora"));

            // No language on the map: the script decides, and a Ukrainian letter says Ukrainian.
            var unset = createBeatmap();
            TypeBeatEditorOperations.SetLineText(unset, lineAt(unset, 0), "гора їжак");
            Assert.That(lineAt(unset, 0).Line.RawText, Is.EqualTo("hora yizhak"));
        }

        /// <summary>
        /// The proposal is the mapper's to overwrite: typing Latin over it in the same box keeps every
        /// word's original (a same-count commit keeps originals by place), and the '|' gesture cuts
        /// the ROMANISED word.
        /// </summary>
        [Test]
        public void LatinTypedOverTheProposalKeepsTheOriginalsAndThePipesCutTheRomanisedRow()
        {
            var editorBeatmap = createBeatmap();
            var line = lineAt(editorBeatmap, 0);

            TypeBeatEditorOperations.SetLineText(editorBeatmap, line, "Привет мир");
            Assert.That(TypeBeatEditorOperations.SetLineText(editorBeatmap, line, "Pri|vyet mir"), Is.True);

            Assert.That(line.Line.RawText, Is.EqualTo("Privyet mir"));
            Assert.That(line.Line.Original, Is.EqualTo("Привет мир"));
            Assert.That(line.Line.Units.Select(u => u.Original), Is.EqualTo(new[] { "Привет", "мир" }));
            Assert.That(line.Line.Units[0].SyllableSplits, Is.EqualTo(new[] { 3 }));
            Assert.That(line.Line.Units[0].SyllableBoundaries.Count, Is.EqualTo(1));
        }

        [Test]
        public void TheFreestyleGestureWorksOnTheRomanisedRow()
        {
            var editorBeatmap = createBeatmap();
            var line = lineAt(editorBeatmap, 0);

            TypeBeatEditorOperations.SetLineText(editorBeatmap, line, "Привет мир");
            TypeBeatEditorOperations.SetLineText(editorBeatmap, line, "Privet &&&");

            Assert.That(line.Line.RawText, Is.EqualTo("Privet &&&"));
            Assert.That(line.Line.Units.Select(u => u.Original), Is.EqualTo(new[] { "Привет", "мир" }));
            Assert.That(OriginalTextFormatTest.Encode(editorBeatmap), Does.Contain("\"freestyle\":true"));
        }

        /// <summary>
        /// A kanji the romaniser cannot read is placed like any typed word and then set aside as an
        /// unromanised word with that span; the word editor's lower row gives it its text, which makes
        /// it an ordinary hand-timed word at its place, and the submission veto lifts.
        /// </summary>
        [Test]
        public void AnUnromanisedWordIsRomanisedInTheWordEditor()
        {
            var editorBeatmap = createBeatmap();
            var line = lineAt(editorBeatmap, 0);

            Assert.That(TypeBeatEditorOperations.SetLineText(editorBeatmap, line, "君 が すき"), Is.True);

            Assert.That(line.Line.RawText, Is.EqualTo("ga suki"));
            Assert.That(line.Line.Original, Is.EqualTo("君 が すき"));
            var pending = line.Line.UnromanisedWords.Single();
            Assert.That(pending.Original, Is.EqualTo("君"));
            Assert.That(pending.Position, Is.Zero);
            Assert.That(pending.EndTime, Is.LessThanOrEqualTo(line.Line.Units[0].StartTime));

            Assert.That(new TypeBeatRuleset().GetSubmissionRefusal(editorBeatmap), Does.Contain("君"));

            var slots = TypeBeatEditorOperations.WordSlots(line.Line);
            Assert.That(slots.Select(s => s.Unromanised), Is.EqualTo(new[] { true, false, false }));

            Assert.That(TypeBeatEditorOperations.SetWordText(editorBeatmap, line, 0, "kimi"), Is.True);

            Assert.That(line.Line.RawText, Is.EqualTo("kimi ga suki"));
            Assert.That(line.Line.UnromanisedWords, Is.Empty);
            Assert.That(line.Line.Units[0].Original, Is.EqualTo("君"));
            Assert.That(line.Line.Units[0].StartTime, Is.EqualTo(pending.StartTime));
            Assert.That(line.Line.Units[0].Source, Is.EqualTo(TimingSource.Explicit));
            Assert.That(new TypeBeatRuleset().GetSubmissionRefusal(editorBeatmap), Is.Null);

            Assert.That(reloaded(editorBeatmap, 0).RawText, Is.EqualTo("kimi ga suki"));
        }

        [Test]
        public void TheUpperRowSetsAnOriginalAndProposesItsRomanisation()
        {
            var editorBeatmap = createBeatmap();
            var line = lineAt(editorBeatmap, 1);

            // An English word given an original: the romaniser proposes the typed text too.
            Assert.That(TypeBeatEditorOperations.SetWordOriginal(editorBeatmap, line, 0, "гамма"), Is.True);
            Assert.That(line.Line.Units[0].Text, Is.EqualTo("gamma"));
            Assert.That(line.Line.Units[0].Original, Is.EqualTo("гамма"));
            Assert.That(TypeBeatEditorOperations.OriginalCaption(line.Line), Is.EqualTo("гамма delta"));

            // The lower row overwrites the proposal, and the original stays.
            Assert.That(TypeBeatEditorOperations.SetWordText(editorBeatmap, line, 0, "gammah"), Is.True);
            Assert.That(line.Line.RawText, Is.EqualTo("gammah delta"));
            Assert.That(line.Line.Units[0].Original, Is.EqualTo("гамма"));

            // An original the romaniser cannot spell leaves the typed text alone.
            Assert.That(TypeBeatEditorOperations.SetWordOriginal(editorBeatmap, line, 1, "德尔塔"), Is.True);
            Assert.That(line.Line.Units[1].Text, Is.EqualTo("delta"));
            Assert.That(line.Line.Units[1].Original, Is.EqualTo("德尔塔"));

            // Emptying the lower row of a word with an original makes it unromanised again.
            Assert.That(TypeBeatEditorOperations.SetWordText(editorBeatmap, line, 1, string.Empty), Is.True);
            Assert.That(line.Line.RawText, Is.EqualTo("gammah"));
            Assert.That(line.Line.UnromanisedWords.Single().Original, Is.EqualTo("德尔塔"));

            // The line's own original is a caption of its own.
            Assert.That(TypeBeatEditorOperations.SetLineOriginal(editorBeatmap, line, "гамма  德尔塔"), Is.True);
            Assert.That(line.Line.Original, Is.EqualTo("гамма 德尔塔"));
        }

        /// <summary>Every other edit carries the originals along: a retime, a shift, a split and a merge.</summary>
        [Test]
        public void TheOriginalsSurviveTheOtherEdits()
        {
            var editorBeatmap = createBeatmap();
            TypeBeatEditorOperations.SetLineText(editorBeatmap, lineAt(editorBeatmap, 0), "Привет мир");

            TypeBeatEditorOperations.SetUnitTiming(editorBeatmap, lineAt(editorBeatmap, 0), 0, 1100, 1700);
            Assert.That(lineAt(editorBeatmap, 0).Line.Units[0].Original, Is.EqualTo("Привет"));
            Assert.That(lineAt(editorBeatmap, 0).Line.Original, Is.EqualTo("Привет мир"));

            TypeBeatEditorOperations.ShiftAllTimes(editorBeatmap, 50);
            Assert.That(lineAt(editorBeatmap, 0).Line.Units.Select(u => u.Original), Is.EqualTo(new[] { "Привет", "мир" }));

            TypeBeatEditorOperations.MergeWithNext(editorBeatmap, lineAt(editorBeatmap, 0));
            var merged = lineAt(editorBeatmap, 0).Line;
            Assert.That(merged.RawText, Is.EqualTo("Privet mir gamma delta"));
            Assert.That(merged.Original, Is.EqualTo("Привет мир gamma delta"));
            Assert.That(merged.Units.Select(u => u.Original), Is.EqualTo(new[] { "Привет", "мир", null, null }));

            TypeBeatEditorOperations.SplitLine(editorBeatmap, lineAt(editorBeatmap, 0), 2);
            Assert.That(lineAt(editorBeatmap, 0).Line.Original, Is.EqualTo("Привет мир"));
            Assert.That(lineAt(editorBeatmap, 1).Line.Original, Is.Null, "the English half has no original left");
        }

        /// <summary>The 339 clipboard carries the originals beside the text, and the text paste puts them down.</summary>
        [Test]
        public void TheLineClipboardCarriesTheOriginals()
        {
            var editorBeatmap = createBeatmap();
            TypeBeatEditorOperations.SetLineText(editorBeatmap, lineAt(editorBeatmap, 0), "Привет мир");

            var payload = TypeBeatEditorOperations.CopyLineTimings(new[] { lineAt(editorBeatmap, 0) });
            string serialized = LyricTimingClipboard.Serialize(payload);
            Assert.That(serialized, Does.Contain("\"original\""));

            var (parsed, _) = LyricTimingClipboard.TryParse(serialized);
            Assert.That(parsed!.Lines[0].Original, Is.EqualTo("Привет мир"));
            Assert.That(parsed.Lines[0].Units.Select(u => u.Original), Is.EqualTo(new[] { "Привет", "мир" }));

            TypeBeatEditorOperations.PasteLine(editorBeatmap, new[] { lineAt(editorBeatmap, 1) }, parsed);

            var pasted = lineAt(editorBeatmap, 1).Line;
            Assert.That(pasted.RawText, Is.EqualTo("Privet mir"));
            Assert.That(pasted.Original, Is.EqualTo("Привет мир"));
            Assert.That(pasted.Units.Select(u => u.Original), Is.EqualTo(new[] { "Привет", "мир" }));

            // A timing-only paste keeps the target's own text and originals.
            TypeBeatEditorOperations.PasteLineTimings(editorBeatmap, new[] { lineAt(editorBeatmap, 0) }, parsed);
            Assert.That(lineAt(editorBeatmap, 0).Line.Units.Select(u => u.Original), Is.EqualTo(new[] { "Привет", "мир" }));
        }

        /// <summary>A plain Latin commit on a line without originals stores none (nothing moved).</summary>
        [Test]
        public void ALatinEditStoresNoOriginal()
        {
            var editorBeatmap = createBeatmap();
            TypeBeatEditorOperations.SetLineText(editorBeatmap, lineAt(editorBeatmap, 0), "alpha beta gamma");

            Assert.That(lineAt(editorBeatmap, 0).Line.Original, Is.Null);
            Assert.That(lineAt(editorBeatmap, 0).Line.Units.Select(u => u.Original), Is.All.Null);
            Assert.That(OriginalTextFormatTest.Encode(editorBeatmap), Does.Not.Contain("original"));
        }
    }
}
