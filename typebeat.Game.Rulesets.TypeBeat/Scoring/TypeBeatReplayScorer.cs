// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using typebeat.Game.Beatmaps;
using typebeat.Game.Replays;
using typebeat.Game.Rulesets.Judgements;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Objects;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Scoring
{
    /// <summary>
    /// The submitted account a run produces: exactly the quantities a client puts on the wire
    /// (SoloScoreInfo.ForSubmission) and the server stores, plus the derived values the results
    /// screen shows. Every one of them comes out of a real <see cref="TypeBeatScoreProcessor"/>.
    /// </summary>
    public sealed class TypeBeatReplayAccount
    {
        public required IReadOnlyDictionary<HitResult, int> Statistics { get; init; }
        public required IReadOnlyDictionary<HitResult, int> MaximumStatistics { get; init; }
        public required int MaxCombo { get; init; }
        public required long TotalScore { get; init; }
        public required long TotalScoreWithoutMods { get; init; }
        public required double Accuracy { get; init; }
        public required double Completion { get; init; }
        public required ScoreRank Rank { get; init; }

        /// <summary>Wrong keypresses (<see cref="TypeBeatScoreProcessor.MISTYPE_RESULT"/>).</summary>
        public int Mistypes => Statistics.GetValueOrDefault(TypeBeatScoreProcessor.MISTYPE_RESULT);

        /// <summary>Frames the replay held that were never fed, i.e. the engine was inert for them.
        /// Always 0 for a replay of a completed run; anything else means the harness and the run
        /// disagree about the map, and the account must not be trusted.</summary>
        public required int UnconsumedFrames { get; init; }
    }

    /// <summary>
    /// Re-derives the SUBMITTED account of a stored score from its replay, headlessly.
    ///
    /// <para><b>Why this is not just "run the TypingEngine".</b> The engine's own counters are not
    /// what a client submits. The submitted <c>statistics</c> / <c>max_combo</c> / <c>total_score</c>
    /// come out of osu's <see cref="ScoreProcessor"/>, fed by the DRAWABLE layer
    /// (<see cref="Objects.Drawables.DrawableTypeBeatHitObject"/> and <c>TypeBeatPlayfield</c>),
    /// and backlog 109's change lives entirely in that layer. So this drives the REAL
    /// <see cref="TypeBeatScoreProcessor"/> with the REAL <see cref="TypingEngine"/> and reproduces
    /// the drawable layer's four seams exactly:</para>
    ///
    /// <list type="number">
    /// <item><c>CharJudged</c> resolves the cell with
    /// <see cref="TypeBeatResultMapping.CellResult"/>, the same call the drawable makes, and the
    /// FIRST result on a cell wins (<c>DrawableTypeBeatCharObject.ApplyEngineResult</c> drops every
    /// later one). A typed-through wrong char resolves nothing and instead PREPAYS its cell's combo
    /// break (<see cref="TypeBeatResultMapping.PrepaysCellComboBreak"/>, backlog 122), the same call
    /// <c>TypeBeatPlayfield.onCharJudged</c> makes.</item>
    /// <item><c>LineSealed</c> misses every still-unresolved cell of the line, then resolves the
    /// line itself scoring-inert (<c>ApplySealResults</c>).</item>
    /// <item><c>Mistyped</c> counts the mistype and, under <see cref="TypoRule.Deferred"/>, mirrors
    /// the combo break by hand (<c>onMistyped</c>).</item>
    /// <item>Fletcher's rush cap breaks combo on a judgement that is still a hit
    /// (<c>onCharJudged</c>), and under <see cref="TypoRule.ImmediateMiss"/> a REJECTED key breaks
    /// it (<c>onWrongKeyRejected</c>, which is where the break lived before backlog 109).</item>
    /// </list>
    ///
    /// <para><b>What it does not do.</b> No health, so PASS/FAIL is not re-derived: a replay ends
    /// where the run ended, and a run that would have survived longer under a different rule left no
    /// record of what it would have typed. Callers must recalculate PASSED runs only.</para>
    ///
    /// <para>The engine is fed exactly as <c>TypeBeatPlayfield.EngineTicker</c> feeds it: a
    /// display-cadence <c>Update</c> loop with each due frame applied as
    /// <c>Update(frameTime)</c> + the keystroke. <see cref="TypingEngine.Update"/> is monotonic and
    /// idempotent, so the cadence cannot change any judgement; it only decides when a seal lands
    /// relative to the next keystroke, which the recorded times already fix.</para>
    /// </summary>
    public static class TypeBeatReplayScorer
    {
        /// <summary>The display-frame cadence the engine ticker runs at; keystrokes interleave.</summary>
        private const double frame_ms = 1000.0 / 60;

        /// <summary>
        /// How far past the last line's end the engine is ticked so every line seals. The engine
        /// seals a line once the clock passes its end plus its grace, and a play's final line has
        /// to seal for the account to be complete.
        /// </summary>
        private const double tail_ms = 10000;

        /// <summary>
        /// Re-derive the account for one run.
        /// </summary>
        /// <param name="playable">The playable beatmap the run was played on: the ruleset's own
        /// converted beatmap, with mods applied and nested per-cell objects built. This is what the
        /// score processor's <c>maximum_statistics</c> comes from, so it must be the same beatmap
        /// the client had.</param>
        /// <param name="mods">The run's mods. They reach three places: the engine (Gatekeeper,
        /// Fletcher, Mashing, Literate), the score multiplier, and the rank adjustment.</param>
        /// <param name="replay">The recorded frames (see <see cref="TypeBeatReplayFrame"/>).</param>
        /// <param name="rule">The typo rule to judge under. Stored scores predating backlog 109
        /// were judged under <see cref="TypoRule.ImmediateMiss"/>.</param>
        public static TypeBeatReplayAccount Score(IBeatmap playable, IReadOnlyList<Mod> mods, Replay replay, TypoRule rule)
        {
            ArgumentNullException.ThrowIfNull(playable);
            ArgumentNullException.ThrowIfNull(replay);

            mods ??= Array.Empty<Mod>();

            var lineObjects = playable.HitObjects.OfType<TypeBeatHitObject>().OrderBy(h => h.LineIndex).ToList();

            // Normalize exactly as DrawableTypeBeatRuleset.createEngine does, so engine position ==
            // LineIndex == the key every CharJudgement is routed by.
            for (int i = 0; i < lineObjects.Count; i++)
                lineObjects[i].LineIndex = i;

            var engine = createEngine(playable, lineObjects, mods);
            var ruleset = new TypeBeatRuleset();

            var scoreProcessor = new TypeBeatScoreProcessor(ruleset);

            // Mods BEFORE the beatmap: ApplyBeatmap stores MaximumTotalScore, which is computed
            // through the score multiplier (ScoreProcessor.ApplyBeatmap's own note).
            scoreProcessor.Mods.Value = mods;
            scoreProcessor.ApplyBeatmap(playable);

            var cells = new CellRegistry(lineObjects);

            void onCharJudged(CharJudgement judgement)
            {
                // The drawable layer's prepayment seam (TypeBeatPlayfield.onCharJudged): a
                // typed-through wrong char has just paid its combo break at the keypress, so the
                // deferred Miss its cell resolves with must not pay it again (backlog 122).
                if (TypeBeatResultMapping.PrepaysCellComboBreak(judgement.Type, rule))
                    scoreProcessor.PrepayComboBreak(judgement.LineIndex, judgement.CellIndex);

                if (TypeBeatResultMapping.CellResult(judgement.Type, rule) is HitResult result)
                    cells.Resolve(scoreProcessor, judgement.LineIndex, judgement.CellIndex, result);

                if (engine.FletcherEnabled && judgement.ComboAfter == 0)
                    scoreProcessor.Combo.Value = 0;
            }

            void onLineSealed(LineSealResult sealResult) => cells.Seal(scoreProcessor, sealResult.LineIndex);

            void onMistyped()
            {
                if (TypeBeatResultMapping.MistypeCarriesTheComboBreak(rule))
                    scoreProcessor.Combo.Value = 0;

                scoreProcessor.RecordMistype();
            }

            // Pre-109 the break for a REJECTED key lived here, and a typed-through wrong char broke
            // combo through the Miss its cell took. The mash-guard half of this seam is health only,
            // which is not simulated.
            void onWrongKeyRejected(char c)
            {
                if (!TypeBeatResultMapping.MistypeCarriesTheComboBreak(rule))
                    scoreProcessor.Combo.Value = 0;
            }

            engine.CharJudged += onCharJudged;
            engine.LineSealed += onLineSealed;
            engine.Mistyped += onMistyped;
            engine.WrongKeyRejected += onWrongKeyRejected;

            int consumed = feed(engine, replay);

            engine.CharJudged -= onCharJudged;
            engine.LineSealed -= onLineSealed;
            engine.Mistyped -= onMistyped;
            engine.WrongKeyRejected -= onWrongKeyRejected;

            var populated = new ScoreInfo { Ruleset = ruleset.RulesetInfo };
            scoreProcessor.PopulateScore(populated);

            var statistics = populated.Statistics.Where(kvp => kvp.Value != 0).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            var maximumStatistics = populated.MaximumStatistics.Where(kvp => kvp.Value != 0).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            return new TypeBeatReplayAccount
            {
                Statistics = statistics,
                MaximumStatistics = maximumStatistics,
                MaxCombo = populated.MaxCombo,
                TotalScore = populated.TotalScore,
                TotalScoreWithoutMods = populated.TotalScoreWithoutMods,
                Accuracy = populated.Accuracy,
                Completion = TypeBeatScoreProcessor.ComputeCompletion(populated),
                Rank = populated.Rank,
                UnconsumedFrames = replay.Frames.Count - consumed,
            };
        }

        /// <summary>
        /// The engine <c>DrawableTypeBeatRuleset.createEngine</c> would build for this beatmap,
        /// plus the engine flags the mods set through <c>ApplyToDrawableRuleset</c>.
        /// <c>AllowWrongInput</c> and <c>SpaceSkipsWord</c> are deliberately NOT set from the mods
        /// or from any config: the replay's CONFIG frame carries what the run was judged under and
        /// overwrites both, which is the only thing that judges a pre-Gatekeeper strict run right.
        /// </summary>
        private static TypingEngine createEngine(IBeatmap playable, IReadOnlyList<TypeBeatHitObject> lineObjects, IReadOnlyList<Mod> mods)
        {
            TimingGranularity granularity = lineObjects.Count > 0 ? lineObjects[0].Granularity : TimingGranularity.Line;

            var lyricBeatmap = new LyricBeatmap
            {
                Metadata = new LyricBeatmapMetadata
                {
                    Artist = playable.BeatmapInfo.Metadata.Artist,
                    Title = playable.BeatmapInfo.Metadata.Title,
                    FolderPath = string.Empty,
                    AudioFileName = playable.BeatmapInfo.Metadata.AudioFile,
                    HasWordTiming = granularity != TimingGranularity.Line,
                },
                Lines = lineObjects.Select(h => h.Line).ToList(),
                Granularity = granularity,
            };

            bool literate = mods.Any(m => m is TypeBeatModLiterate);

            var engine = new TypingEngine(lyricBeatmap, literate);

            if (mods.Any(m => m is TypeBeatModFletcher))
                engine.FletcherEnabled = true;

            if (mods.Any(m => m is TypeBeatModMashing))
                engine.MashingEnabled = true;

            return engine;
        }

        /// <summary>
        /// <c>TypeBeatPlayfield.EngineTicker</c>, headless. Returns how many frames were applied.
        /// </summary>
        private static int feed(TypingEngine engine, Replay replay)
        {
            var frames = replay.Frames.OfType<TypeBeatReplayFrame>().ToList();

            double end = engine.Lines.Count > 0
                ? engine.Lines[^1].EndTime + engine.Lines[^1].SealGraceMs + tail_ms
                : tail_ms;

            if (frames.Count > 0)
                end = Math.Max(end, frames[^1].Time + tail_ms);

            int next = 0;

            for (double now = 0; now <= end; now += frame_ms)
            {
                while (next < frames.Count && frames[next].Time <= now)
                {
                    apply(engine, frames[next]);
                    next++;
                }

                engine.Update(now);
            }

            while (next < frames.Count)
            {
                apply(engine, frames[next]);
                next++;
            }

            engine.Update(end);

            return next;
        }

        private static void apply(TypingEngine engine, TypeBeatReplayFrame frame)
        {
            if (frame.IsConfig)
            {
                engine.AllowWrongInput = frame.AllowWrongInput;
                engine.SpaceSkipsWord = frame.SpaceSkipsWord;
                return;
            }

            engine.Update(frame.Time);

            if (frame.IsBackspace)
                engine.ProcessBackspace();
            else
                engine.ProcessKey(frame.Character, frame.Time);
        }

        /// <summary>
        /// The nested per-cell scoring objects, indexed the way the playfield indexes their
        /// drawables, plus the one thing the drawables own: a cell takes exactly ONE osu result,
        /// the first offered.
        /// </summary>
        private sealed class CellRegistry
        {
            private readonly Dictionary<int, LineCells> lines = new Dictionary<int, LineCells>();

            public CellRegistry(IReadOnlyList<TypeBeatHitObject> lineObjects)
            {
                foreach (var lineObject in lineObjects)
                {
                    var line = new LineCells(lineObject);

                    foreach (var nested in lineObject.NestedHitObjects.OfType<TypeBeatCharObject>())
                        line.Cells[nested.CellIndex] = new Cell(nested);

                    lines[lineObject.LineIndex] = line;
                }
            }

            public void Resolve(ScoreProcessor scoreProcessor, int lineIndex, int cellIndex, HitResult result)
            {
                if (!lines.TryGetValue(lineIndex, out var line) || !line.Cells.TryGetValue(cellIndex, out var cell))
                    return;

                cell.Apply(scoreProcessor, result);
            }

            public void Seal(ScoreProcessor scoreProcessor, int lineIndex)
            {
                if (!lines.TryGetValue(lineIndex, out var line))
                    return;

                foreach (var cell in line.Cells.Values)
                    cell.Apply(scoreProcessor, TypeBeatResultMapping.SEAL_MISS);

                line.Apply(scoreProcessor, TypeBeatResultMapping.LINE_RESULT);
            }
        }

        private sealed class LineCells(TypeBeatHitObject lineObject)
        {
            public readonly Dictionary<int, Cell> Cells = new Dictionary<int, Cell>();

            private readonly Slot slot = new Slot(lineObject);

            public void Apply(ScoreProcessor scoreProcessor, HitResult result) => slot.Apply(scoreProcessor, result);
        }

        private sealed class Cell(TypeBeatCharObject charObject)
        {
            private readonly Slot slot = new Slot(charObject);

            public void Apply(ScoreProcessor scoreProcessor, HitResult result) => slot.Apply(scoreProcessor, result);
        }

        /// <summary>
        /// One judged object. <c>DrawableHitObject</c> holds exactly one
        /// <see cref="JudgementResult"/> per object and <c>ApplyResult</c> refuses to run twice
        /// (<c>Judged</c>); this is that, without the drawable.
        /// </summary>
        private sealed class Slot(HitObject hitObject)
        {
            private readonly JudgementResult result = new JudgementResult(hitObject, hitObject.Judgement);

            private bool judged;

            public void Apply(ScoreProcessor scoreProcessor, HitResult hitResult)
            {
                if (judged)
                    return;

                judged = true;
                result.Type = hitResult;
                scoreProcessor.ApplyResult(result);
            }
        }
    }
}
