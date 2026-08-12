// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Backlog 72: wrong keypresses are a SEPARATE, PERSISTED stat.
//
// Before this, a wrong key in the default (strict) mode raised no judgement at all, so nothing
// past the engine ever saw it: the submitted statistics carried great/ok/meh/miss only and the
// server recomputed a spotless accuracy for a play full of stumbles. The only trace was a broken
// max_combo. These pins cover both halves of the fix: the engine announces every wrong KEYPRESS
// in both input modes, and the score processor persists the count under HitResult.ComboBreak
// WITHOUT touching accuracy, completion, rank or maximum_statistics.

using System.Collections.Generic;
using NUnit.Framework;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class MistypeStatTest
    {
        #region Fixture builders (the TypingEngineTest workhorse line)

        private static TimedUnit unit(string text, double start, double end)
            => new TimedUnit { Text = text, StartTime = start, EndTime = end };

        private static LyricBeatmap map() => new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata
            {
                Artist = "Test",
                Title = "Song",
                FolderPath = @"X:\nowhere",
                AudioFileName = "a.mp3",
            },
            Lines = new List<LyricLine>
            {
                // "ab cd", active [1000, 4000), SingEnd 3000. Cell targets:
                // 'a' 1000, 'b' 1500, ' ' 2000, 'c' 2000, 'd' 2500.
                new LyricLine
                {
                    RawText = "ab cd",
                    StartTime = 1000,
                    EndTime = 4000,
                    SingEndTime = 3000,
                    Units = new[] { unit("ab", 1000, 2000), unit("cd", 2000, 3000) },
                },
            },
            Granularity = TimingGranularity.Line,
        };

        #endregion

        #region Engine: the wrong KEYPRESS is announced in both input modes

        [Test]
        public void StrictModeAnnouncesEveryRejectedKeyAsAMistype()
        {
            // Strict is the GATEKEEPER model since backlog 107; the default one is covered by
            // AllowWrongInputAnnouncesTheSameMistypeAndStillResolvesTheCell below.
            var engine = new TypingEngine(map()) { AllowWrongInput = false };

            int mistypes = 0, judgements = 0;
            engine.Mistyped += () => mistypes++;
            engine.CharJudged += _ => judgements++;

            engine.Update(1000);

            Assert.That(engine.ProcessKey('z', 1000), Is.True);
            Assert.That(engine.ProcessKey('q', 1010), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(mistypes, Is.EqualTo(2), "both rejected keys are mistypes");
                Assert.That(engine.Mistypes, Is.EqualTo(2));
                Assert.That(judgements, Is.Zero, "a rejected key still raises NO cell judgement");

                // Gameplay feel is untouched: caret unmoved, cell still typeable, streak growing.
                Assert.That(engine.CaretIndex, Is.Zero);
                Assert.That(engine.Lines[0].Cells[0].State, Is.EqualTo(CellState.Untyped));
                Assert.That(engine.ConsecutiveWrongKeys, Is.EqualTo(2));
                Assert.That(engine.Combo, Is.Zero);
            });

            // The count is the engine's own WrongChar tally, exposed under the name it now carries
            // outside the engine.
            Assert.That(engine.BuildResults().Counts[JudgementType.WrongChar], Is.EqualTo(engine.Mistypes));
        }

        [Test]
        public void AllowWrongInputAnnouncesTheSameMistypeAndStillResolvesTheCell()
        {
            var engine = new TypingEngine(map()) { AllowWrongInput = true };

            int mistypes = 0;
            var judged = new List<CharJudgement>();
            engine.Mistyped += () => mistypes++;
            engine.CharJudged += judged.Add;

            engine.Update(1000);
            Assert.That(engine.ProcessKey('z', 1000), Is.True);

            Assert.Multiple(() =>
            {
                // The KEYPRESS accounts identically to strict mode...
                Assert.That(mistypes, Is.EqualTo(1));
                Assert.That(engine.Mistypes, Is.EqualTo(1));

                // ...while the CELL keeps doing exactly what it did before: the wrong char is typed
                // through, marked Wrong, the caret advances, and the cell's own WrongChar judgement
                // still travels on CharJudged. Since backlog 109 the drawable applies no osu result
                // for it (the cell's result is deferred until it is corrected or sealed on), which is
                // a matter for the drawable and does not touch this keypress count.
                Assert.That(judged, Has.Count.EqualTo(1));
                Assert.That(judged[0].Type, Is.EqualTo(JudgementType.WrongChar));
                Assert.That(engine.CaretIndex, Is.EqualTo(1));
                Assert.That(engine.Lines[0].Cells[0].State, Is.EqualTo(CellState.Wrong));
                Assert.That(engine.Lines[0].Cells[0].TypedChar, Is.EqualTo('z'));

                // This mode deliberately never feeds the mash-fail streak; that is unchanged.
                Assert.That(engine.ConsecutiveWrongKeys, Is.Zero);
            });
        }

        [Test]
        public void ARightCharAtAWrongTimeIsNotAMistype()
        {
            // TypingEngine's other no-miss combo break: the correct char struck outside the widest
            // window is ACCEPTED (0 points, combo broken) and judged Premature/Lagging, which the
            // drawable already maps to an osu Miss, so it has always reached the score processor.
            // Out of scope here, and it must stay out of the mistype count.
            var engine = new TypingEngine(map());

            int mistypes = 0;
            engine.Mistyped += () => mistypes++;

            engine.Update(3200);
            // The line's five characters span 1000..2500, i.e. 375 ms apart, so the playhead at 7100
            // sits at 4 + 4600/375 = 16.27 characters: one notch past the Line MehLate of 16. A press
            // is judged on the time it is handed, so no further Update is needed to reach it.
            Assert.That(engine.ProcessKey('a', 7100), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(mistypes, Is.Zero);
                Assert.That(engine.Mistypes, Is.Zero);
                Assert.That(engine.BuildResults().Counts[JudgementType.Lagging], Is.EqualTo(1));
                Assert.That(engine.Combo, Is.Zero, "it is still a combo break, just not a mistype");
            });
        }

        [Test]
        public void ACleanRunHasNoMistypes()
        {
            var engine = new TypingEngine(map());

            int mistypes = 0;
            engine.Mistyped += () => mistypes++;

            engine.Update(1000);

            foreach ((char c, double t) in new[] { ('a', 1000.0), ('b', 1500.0), (' ', 2000.0), ('c', 2000.0), ('d', 2500.0) })
                Assert.That(engine.ProcessKey(c, t), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(mistypes, Is.Zero);
                Assert.That(engine.Mistypes, Is.Zero);
            });
        }

        #endregion

        #region Score processor: persisted, and inert everywhere else

        private static TypeBeatScoreProcessor processor() => new TypeBeatScoreProcessor(new TypeBeatRuleset());

        [Test]
        public void RecordMistypeCountsUnderComboBreakAndTouchesNothingElse()
        {
            var scoreProcessor = processor();
            scoreProcessor.Combo.Value = 37;

            scoreProcessor.RecordMistype();
            scoreProcessor.RecordMistype();

            Assert.Multiple(() =>
            {
                Assert.That(scoreProcessor.Mistypes, Is.EqualTo(2));
                Assert.That(scoreProcessor.Statistics.GetValueOrDefault(HitResult.ComboBreak), Is.EqualTo(2));

                // Counting is all it does. Each input mode already breaks combo its own way (the
                // playfield's hand reset for a rejected key, the cell's Miss judgement for a
                // typed-through wrong char); doing it here as well would double up on the second
                // and destroy the ComboAtJudgement a rewind restores from.
                Assert.That(scoreProcessor.Combo.Value, Is.EqualTo(37));

                // The wire key the server reads. Changing it silently would strand the stat.
                Assert.That(TypeBeatScoreProcessor.MISTYPE_RESULT, Is.EqualTo(HitResult.ComboBreak));
            });
        }

        [Test]
        public void TheMistypeResultIsInertForAccuracyCompletionAndCombo()
        {
            // Everything the recompute (client AND server) keys off must ignore this result, or an
            // old score and a new one would stop meaning the same thing.
            Assert.Multiple(() =>
            {
                Assert.That(HitResult.ComboBreak.AffectsAccuracy(), Is.False);
                Assert.That(HitResult.ComboBreak.IsBasic(), Is.False);
                Assert.That(HitResult.ComboBreak.IsHit(), Is.False);
                Assert.That(HitResult.ComboBreak.IncreasesCombo(), Is.False);
                Assert.That(HitResult.ComboBreak.IsBonus(), Is.False);
                Assert.That(HitResult.ComboBreak.BreaksCombo(), Is.True);
            });
        }

        [Test]
        public void MistypesDoNotMoveCompletionOrRank()
        {
            // The headline guarantee: a heavily mistyped but fully typed play is still an SS.
            var clean = new Dictionary<HitResult, int> { [HitResult.Great] = 100 };
            var sloppy = new Dictionary<HitResult, int> { [HitResult.Great] = 100, [HitResult.ComboBreak] = 250 };

            var scoreProcessor = processor();

            Assert.Multiple(() =>
            {
                Assert.That(TypeBeatScoreProcessor.ComputeCompletion(sloppy),
                    Is.EqualTo(TypeBeatScoreProcessor.ComputeCompletion(clean)));
                Assert.That(scoreProcessor.RankFromScore(0, sloppy), Is.EqualTo(ScoreRank.X));
            });
        }

        [Test]
        public void WholeMapCompletionIgnoresMistypesOnBothSidesOfTheFraction()
        {
            var score = new ScoreInfo
            {
                Statistics = new Dictionary<HitResult, int>
                {
                    [HitResult.Great] = 38,
                    [HitResult.Miss] = 2,
                    [HitResult.ComboBreak] = 400,
                },
                // maximum_statistics stays one great per cell: mashing can never inflate the
                // denominator, which is what makes the completion/pp maths safe.
                MaximumStatistics = new Dictionary<HitResult, int> { [HitResult.Great] = 100 },
            };

            Assert.That(TypeBeatScoreProcessor.ComputeCompletion(score), Is.EqualTo(0.38).Within(1e-9));
        }

        [Test]
        public void AScoreWithoutTheStatCarriesNullNotZero()
        {
            // History: plays from before the stat existed simply LACK the key, and the display must
            // show nothing for them rather than claim a flawless run.
            var old = new ScoreInfo { Statistics = new Dictionary<HitResult, int> { [HitResult.Great] = 100 } };
            var fresh = new ScoreInfo { Statistics = new Dictionary<HitResult, int> { [HitResult.Great] = 100, [HitResult.ComboBreak] = 0 } };
            var messy = new ScoreInfo { Statistics = new Dictionary<HitResult, int> { [HitResult.Great] = 100, [HitResult.ComboBreak] = 9 } };

            Assert.Multiple(() =>
            {
                Assert.That(TypeBeatScoreProcessor.MistypesOf(old), Is.Null);
                Assert.That(TypeBeatScoreProcessor.MistypesOf(fresh), Is.Zero);
                Assert.That(TypeBeatScoreProcessor.MistypesOf(messy), Is.EqualTo(9));
            });
        }

        [Test]
        public void TheResultsScreenShowsMistypesOnlyForAScoreThatCarriesThem()
        {
            static string[] rowsFor(ScoreInfo score)
            {
                var names = new List<string>();

                foreach (var item in TypeBeatRuleset.CreateCompletionStatistics(score))
                    names.Add(item.Name);

                return names.ToArray();
            }

            var old = new ScoreInfo
            {
                Statistics = new Dictionary<HitResult, int> { [HitResult.Great] = 100 },
                MaximumStatistics = new Dictionary<HitResult, int> { [HitResult.Great] = 100 },
            };
            var messy = new ScoreInfo
            {
                Statistics = new Dictionary<HitResult, int> { [HitResult.Great] = 100, [HitResult.ComboBreak] = 9 },
                MaximumStatistics = new Dictionary<HitResult, int> { [HitResult.Great] = 100 },
            };

            // The trailing "pp" row (backlog 75) is unconditional, unlike this one: an absent
            // typo count is unknowable and shows no row, whereas a pp reading always exists,
            // either as a number or as "could never have earned any".
            //
            // The row is named TYPOS since backlog 140, and it is the only typo figure the results
            // screen carries: the uncorrected-typo CELLS are no longer surfaced as a count of their
            // own (TypeBeatRuleset.GetValidHitResults), because each of them took a wrong keypress
            // that this one number already counts.
            Assert.Multiple(() =>
            {
                Assert.That(rowsFor(old), Is.EqualTo(new[] { "Completion", "Missed characters", "pp" }));
                Assert.That(rowsFor(messy), Is.EqualTo(new[] { "Completion", "Missed characters", "Typos", "pp" }));

                Assert.That(new TypeBeatRuleset().GetValidHitResults(), Does.Not.Contain(TypeBeatResultMapping.UNFIXED_TYPO));
            });
        }

        #endregion
    }
}
