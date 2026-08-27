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
    /// later one). A typed-through wrong char resolves nothing.</item>
    /// <item><c>LineSealed</c> resolves every still-unresolved cell of the line, in ascending cell
    /// order, through <see cref="TypeBeatResultMapping.UnresolvedCellResult"/>: a Miss for a cell
    /// nobody typed, an unfixed typo (applied combo-neutral) for one left sitting wrong. Then it
    /// resolves the line itself scoring-inert (<c>ApplySealResults</c>).</item>
    /// <item><c>Mistyped</c> counts the mistype and, under <see cref="TypoRule.Deferred"/>, mirrors
    /// the combo break by hand (<c>onMistyped</c>).</item>
    /// <item>Fletcher's rush cap breaks combo on a judgement that is still a hit
    /// (<c>onCharJudged</c>), and under <see cref="TypoRule.ImmediateMiss"/> a REJECTED key breaks
    /// it (<c>onWrongKeyRejected</c>, which is where the break lived before backlog 109).</item>
    /// <item><c>ComboRestored</c> puts back the streak a CORRECTED typo's keypress broke, under
    /// <see cref="ComboRestoreRule.OnFix"/> only (<c>onComboRestored</c>, backlog 140), and WHICH
    /// break's streak that is follows <see cref="ComboClaimRule"/> (backlog 176).</item>
    /// <item><c>WordAbandoned</c> carries a word skip's one combo break by hand, and
    /// <c>AbandonSealed</c> marks the cells it gave up combo-neutral so the Misses they finally take
    /// cannot take that break a second time, under <see cref="WordSkipRule.Reclaimable"/> only
    /// (<c>onWordAbandoned</c> / <c>onAbandonSealed</c>, backlog 167). <c>AbandonReclaimed</c> is
    /// deliberately not wired: it carries health alone, and health is not simulated.</item>
    /// <item>An OFF-TIME press needs no seam at all under the live rule (backlog 199,
    /// <see cref="OffTimeRule"/>): it raises no <c>Mistyped</c> and no <c>ComboBroken</c>, and the
    /// <see cref="HitResult.Meh"/> its cell resolves with extends osu's combo by itself, exactly as
    /// the engine extends its own. Nothing is mirrored by hand, so nothing can double-count.</item>
    /// <item>A CORRECTED typo needs no seam either (backlog 210,
    /// <see cref="CorrectionCreditRule"/>): the cap is applied to the TIER inside the engine, so a
    /// corrected cell announces itself as an Ok and <see cref="TypeBeatResultMapping.CellResult"/>,
    /// which is the identity on the quality tiers, resolves it as one. Selecting the era is one
    /// assignment on the engine and nothing else.</item>
    /// <item>An UNCORRECTED typo needs no seam either (backlog 213,
    /// <see cref="UnfixedTypoWorthRule"/>), and it is the one axis the ENGINE knows nothing about:
    /// the seal path is unchanged, the key it writes is unchanged, and only what that key is WORTH
    /// moved. Selecting the era is one assignment on the score processor.</item>
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
        private const double frame_ms = ReplayEngineFeed.FRAME_MS;

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
        /// <param name="comboRule">The combo-restore rule to judge under. Stored scores predating
        /// backlog 140 were played under <see cref="ComboRestoreRule.Never"/>, so re-deriving one
        /// under the live rule would hand it a max_combo its fingers never earned. Independent of
        /// <paramref name="rule"/>: a score from between backlog 109 and 140 is
        /// <see cref="TypoRule.Deferred"/> with <see cref="ComboRestoreRule.Never"/>.</param>
        /// <param name="spaceRule">Whether the spacebar is inside the timing challenge. Stored scores
        /// predating backlog 148 were played under <see cref="SpaceTimingRule.Timed"/>. Defaults to
        /// the LIVE rule, like <paramref name="rateRule"/>, so a caller that has not thought about
        /// eras gets today's judgement rather than a silently different one.</param>
        /// <param name="rateRule">Whether a rate mod scales the judgement windows. Stored scores
        /// predating backlog 150 were played under <see cref="RateWindowRule.Unscaled"/>. Only
        /// reaches a run that carries a rate mod, so it is the one era axis most rows are indifferent
        /// to.</param>
        /// <param name="skipRule">Whether an abandoned word stays re-typeable. Stored scores
        /// predating backlog 167 were played under <see cref="WordSkipRule.ImmediateMiss"/>, where
        /// the skip spent each abandoned cell's one result on a Miss then and there. Narrower still
        /// than <paramref name="rateRule"/>: it only reaches a run played with the space-skip setting
        /// on that actually skipped a word.</param>
        /// <param name="claimRule">Which break owns the streak when a redeemable one lands on an
        /// outstanding claim. Stored scores predating backlog 176 were played under
        /// <see cref="ComboClaimRule.LatestBreakWins"/>, where a break that cost nothing took the
        /// claim anyway, so re-deriving one under the live rule would hand it combo its fingers
        /// never earned. Reaches only a run that fumbled twice before correcting, but that is a
        /// max_combo difference wherever it lands, which is one of the quantities the
        /// recalculation tool reproduces.</param>
        /// <param name="offTimeRule">What an off-time press (the right character, outside the
        /// outermost Meh window) costs. Stored scores predating backlog 199 were played under
        /// <see cref="OffTimeRule.BreaksCombo"/>, where such a press zeroed the run and resolved its
        /// cell as a Miss. As wide an axis as <paramref name="spaceRule"/> and wider than the rest:
        /// it reaches every row that ever fumbled a beat, and the two arms disagree on
        /// <c>statistics</c>, on <c>max_combo</c>, on accuracy, on completion and on rank.</param>
        /// <param name="creditRule">What a CORRECTED typo's cell is worth. Stored scores predating
        /// backlog 210 were played under <see cref="CorrectionCreditRule.Full"/>, where a fix struck
        /// inside the Great window was worth a full 300 and the typo cost the play no accuracy at
        /// all. It reaches every row that ever fixed a typo, so it is one of the wide axes, but it
        /// moves fewer quantities than <paramref name="offTimeRule"/> does: <c>statistics</c>,
        /// accuracy and <c>total_score</c> only, with <c>max_combo</c>, the miss count, completion
        /// and rank identical under both arms.</param>
        /// <param name="worthRule">What an UNCORRECTED typo's cell is worth in accuracy. Stored
        /// scores predating backlog 213 were played under
        /// <see cref="UnfixedTypoWorthRule.MehCredit"/>, where such a cell was re-weighted to Meh's
        /// 50 of 300 rather than to a miss's 0. It reaches every row that ever left a typo standing,
        /// and it moves the same two quantities <paramref name="creditRule"/> does and no others:
        /// accuracy and <c>total_score</c>. <c>statistics</c> is IDENTICAL under both arms here,
        /// unlike under every other axis, because the fold is a consumer-side reclassification and
        /// the seal's key never moved.</param>
        public static TypeBeatReplayAccount Score(
            IBeatmap playable,
            IReadOnlyList<Mod> mods,
            Replay replay,
            TypoRule rule,
            ComboRestoreRule comboRule,
            SpaceTimingRule spaceRule = SpaceTimingRule.Untimed,
            RateWindowRule rateRule = RateWindowRule.ScaledByRate,
            WordSkipRule skipRule = WordSkipRule.Reclaimable,
            ComboClaimRule claimRule = ComboClaimRule.StreakedBreakWins,
            OffTimeRule offTimeRule = OffTimeRule.MehHit,
            CorrectionCreditRule creditRule = CorrectionCreditRule.Capped,
            UnfixedTypoWorthRule worthRule = UnfixedTypoWorthRule.Nothing)
        {
            ArgumentNullException.ThrowIfNull(playable);
            ArgumentNullException.ThrowIfNull(replay);

            mods ??= Array.Empty<Mod>();

            var lineObjects = playable.HitObjects.OfType<TypeBeatHitObject>().OrderBy(h => h.LineIndex).ToList();

            // Normalize exactly as DrawableTypeBeatRuleset.createEngine does, so engine position ==
            // LineIndex == the key every CharJudgement is routed by.
            for (int i = 0; i < lineObjects.Count; i++)
                lineObjects[i].LineIndex = i;

            var engine = createEngine(playable, lineObjects, mods, rateRule);

            // The combo-restore rule is IMPLEMENTED in the engine (TypeBeatResultMapping
            // .FixRestoresTheComboBreak is read there and nowhere else), so setting it here is the
            // whole of the era gate: with it off no snapshot is ever taken, ComboRestored never
            // fires, and the handler below is dead. That is deliberately the same shape the mods
            // use, and it is why live play and this cannot restore differently.
            engine.ComboRestore = comboRule;

            // The fifth axis, and ComboRestore's companion (backlog 176): who owns the streak when a
            // second break lands on an outstanding claim. Implemented in the engine's one snapshot
            // site, so selecting it here is the whole of that era gate, and it is inert under
            // ComboRestoreRule.Never, where no snapshot is ever taken.
            engine.ComboClaim = claimRule;

            // Same shape again, and set before a single frame is fed: the space exemption is
            // implemented inside ProcessKey, so selecting it here is the whole of that era gate.
            // Backlog 148 is the widest of the four axes, because every map has spaces, and it is
            // the one that decides whether a pre-148 row can be reproduced at all.
            engine.SpaceTiming = spaceRule;

            // Same shape a third time (backlog 167): the reclaim is implemented inside
            // skipCurrentWord, so selecting it here is the whole of that era gate. With it off no
            // cell ever enters the phantom state, so the three handlers below are dead and the skip
            // misses its cells at the keypress exactly as a pre-167 client did.
            engine.WordSkip = skipRule;

            // Same shape a fourth time (backlog 199): what an off-time press costs the RUN is
            // implemented in the engine's one keypress arm, so selecting it here is half of that era
            // gate. The other half is the osu RESULT, which onCharJudged passes the same rule to,
            // and the two have to be the same value or the engine's combo and the score processor's
            // would part company. Passing one variable to both is what makes that structural.
            engine.OffTime = offTimeRule;

            // Same shape a fifth time (backlog 210), and the tidiest of them: the correction cap is
            // implemented in the engine's judging arms alone (TypeBeatResultMapping.AwardedTier),
            // so selecting it here is the WHOLE of that era gate. Unlike the off-time axis it needs
            // no companion at onCharJudged, because the cap moves the TIER the engine announces and
            // CellResult is the identity on the three quality tiers: a capped cell arrives here as
            // an Ok and resolves as an Ok without this file knowing the rule exists.
            engine.CorrectionCredit = creditRule;

            var ruleset = new TypeBeatRuleset();

            var scoreProcessor = new TypeBeatScoreProcessor(ruleset);

            // The sixth axis (backlog 213), and the first that lives in the SCORE PROCESSOR rather
            // than in the engine: what an uncorrected typo's cell is worth in accuracy is decided
            // by GetBaseScoreForResult, which no engine rule can reach. So selecting it here is the
            // whole of that era gate, on the same terms as the five above, and it is set before the
            // beatmap is applied so the simulated maximum run and the real one are priced by one
            // rule. That simulation cannot contain an unfixed typo anyway (it is a run of maximum
            // results), which is exactly why the accuracy DENOMINATOR does not move with the arm.
            scoreProcessor.UnfixedTypoWorth = worthRule;

            // Mods BEFORE the beatmap: ApplyBeatmap stores MaximumTotalScore, which is computed
            // through the score multiplier (ScoreProcessor.ApplyBeatmap's own note).
            scoreProcessor.Mods.Value = mods;
            scoreProcessor.ApplyBeatmap(playable);

            var cells = new CellRegistry(lineObjects);

            void onCharJudged(CharJudgement judgement)
            {
                if (TypeBeatResultMapping.CellResult(judgement.Type, rule, offTimeRule) is HitResult result)
                    cells.Resolve(scoreProcessor, judgement.LineIndex, judgement.CellIndex, result);

                if (engine.FletcherEnabled && judgement.ComboAfter == 0)
                    scoreProcessor.Combo.Value = 0;
            }

            // TypeBeatPlayfield.onLineSealed: the engine is the only thing that can tell a cell
            // nobody typed from one left holding a wrong character, so the decision is made here and
            // the registry only applies it.
            void onLineSealed(LineSealResult sealResult) => cells.Seal(scoreProcessor, sealResult.LineIndex, engine, rule);

            void onMistyped()
            {
                if (TypeBeatResultMapping.MistypeCarriesTheComboBreak(rule))
                    scoreProcessor.Combo.Value = 0;

                scoreProcessor.RecordMistype();
            }

            // TypeBeatPlayfield.onComboRestored: a corrected typo, or a reclaimed word skip, resumes
            // the streak its break cost, mirrored by hand because no result carries it (backlog 140,
            // widened by 167).
            void onComboRestored(int streak) => scoreProcessor.RestoreCombo(streak);

            // TypeBeatPlayfield.onWordAbandoned: the skip's one combo break, by hand, because under
            // the live rule the cells it gave up resolve nothing at the skip (backlog 167). Health is
            // the other half of that seam and is not simulated here.
            void onWordAbandoned(AbandonedCells abandoned) => scoreProcessor.Combo.Value = 0;

            // TypeBeatPlayfield.onAbandonSealed: the cells the player never came back for are about
            // to take their Misses, and each one's break was taken at the skip, so the result must
            // leave combo where it found it.
            void onAbandonSealed(AbandonedCells abandoned)
            {
                foreach (int cellIndex in abandoned.CellIndices)
                    scoreProcessor.MarkComboNeutral(abandoned.LineIndex, cellIndex);
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
            engine.ComboRestored += onComboRestored;
            engine.WordAbandoned += onWordAbandoned;
            engine.AbandonSealed += onAbandonSealed;

            int consumed = feed(engine, replay);

            engine.CharJudged -= onCharJudged;
            engine.LineSealed -= onLineSealed;
            engine.Mistyped -= onMistyped;
            engine.WrongKeyRejected -= onWrongKeyRejected;
            engine.ComboRestored -= onComboRestored;
            engine.WordAbandoned -= onWordAbandoned;
            engine.AbandonSealed -= onAbandonSealed;

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
        /// <c>AllowWrongInput</c>, <c>SpaceSkipsWord</c>, <c>SyllableTiming</c>,
        /// <c>WrongInputOnWordGaps</c>, <c>StrictSpaces</c>, <c>CharTimedStretch</c>,
        /// <c>FlexibleLineSnap</c> and <c>BoundedRush</c> are
        /// deliberately NOT set from the mods or from any config: the replay's CONFIG frame carries
        /// what the run was judged under and overwrites all seven, which is the only thing that judges
        /// a pre-Gatekeeper strict run right.
        /// </summary>
        private static TypingEngine createEngine(IBeatmap playable, IReadOnlyList<TypeBeatHitObject> lineObjects, IReadOnlyList<Mod> mods, RateWindowRule rateRule)
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

            // SyllableTiming is NOT selected here, and deliberately not: it is the one era axis
            // that travels in the replay itself (CONFIG frame, bit 2), so ReplayEngineFeed.Apply
            // sets it from the run's own header before a single keystroke is judged. The engine
            // default is false, which is exactly what a replay with no CONFIG frame, and every
            // replay written before backlog 179, must re-derive under: those runs were judged on
            // point targets. A replay recorded by the live client carries the bit set and
            // re-derives on syllable spans, which is what it was played on.
            //
            // A HARD ROCK replay is the one live run that carries the bit CLEAR (backlog 180: HR
            // reverts the judgement rule, because span judgement undercuts its halved windows). It
            // still needs no arm here, and deliberately: the frame already says so, and adding a
            // mod check would give the same run two answers that could disagree.
            //
            // WrongInputOnWordGaps (backlog 181) is not selected here either, for exactly the same
            // reasons: it travels in the CONFIG frame (bit 3), the engine default is the classic
            // strict word gap every pre-181 replay was played on, and HR carries the bit SET like
            // every other live stack, because it is an input-model axis and not a window one.
            //
            // StrictSpaces (backlog 184, CONFIG frame bit 4) is the third of those, on the same
            // terms: the default is the pre-184 space behaviour every stored replay was played on,
            // and the bit is set for every live stack because the spacebar's meaning is not a mod's
            // business.
            //
            // CharTimedStretch (backlog 209, CONFIG frame bit 6) is the fourth, and closest to the
            // first: it narrows SyllableTiming so a freestyle slot or a stretched identical-character
            // run is judged on its own character target. The default is the pure span rule every
            // stored replay was scored on, mashed sections included, and the bit is set for every
            // live stack (inert under Hard Rock, which has SyllableTiming clear anyway).

            // Every window-scaling mod MULTIPLIES its factor in, never assigns it (see
            // TypingEngine.WindowScale), so the three arms below compose in any order. A replay
            // carries KEYSTROKES and is re-judged from scratch, so missing any one of them would
            // re-grade a stored run on a ladder it was never played on.
            //
            // EASY AND HARD ROCK ARE NOT ERA-DEPENDENT, and deliberately have no switch. Both ship
            // for the first time in this release (backlog 149 and 150), so no stored row can carry
            // either acronym: there is no era in which their arms should be off, and adding a switch
            // would be a dead one that a later reader would have to prove dead all over again.
            if (mods.Any(m => m is TypeBeatModEasy))
                engine.WindowScale *= TypeBeatModEasy.WINDOW_SCALE;

            if (mods.Any(m => m is TypeBeatModHardRock))
                engine.WindowScale *= TypeBeatModHardRock.WINDOW_SCALE;

            // The rate mods scale the windows by the CLOCK RATE so the real-time tolerance is
            // constant (backlog 150). Matched on ModRateAdjust, the base the ruleset's three rate
            // mods share and the same set that carries the live ApplyToDrawableRuleset seam
            // (pinned by TypeBeatRateModTest); the rate is read off the mod's own user-adjustable
            // SpeedChange, never a hardcoded 1.50 / 0.75. Wind Up / Wind Down are deliberately not
            // here: a ramp's rate is a function of time, which one scale set before the first
            // keypress cannot express, and both are unranked at every configuration.
            //
            // Unlike the two above, this one DOES need an era switch: DT / NC / HT have been ranked
            // mods for the whole life of the score table, so every stored rate row was judged on
            // windows fixed in beatmap milliseconds. Under RateWindowRule.Unscaled the loop is
            // skipped entirely, which is exactly what a pre-150 client did.
            if (TypeBeatResultMapping.RateScalesTheWindows(rateRule))
            {
                foreach (var mod in mods)
                {
                    if (mod is ModRateAdjust rateAdjust)
                        engine.WindowScale *= rateAdjust.SpeedChange.Value;
                }
            }

            // BoundedRush (backlog 218, CONFIG frame bit 7) is the fifth, and it is a CARET era
            // rather than a judgement one: the default is the unbounded roll every stored replay was
            // played with (finish a line and you are on the next one however far off its cue it is),
            // and the bit is set for every live stack, inert wherever the caret is pinned.

            // FletcherEnabled and FlexibleLineSnap are NOT selected from TypeBeatModFletcher, and
            // that is the whole shape of backlog 208's flip: the mod named Fletcher now PINS the
            // caret, which is this engine's default, so it needs no arm at all. The unpinned caret
            // travels in the CONFIG frame (bit 5), which ReplayEngineFeed.Apply reads, exactly like
            // the four era bits above.
            //
            // The one arm that IS needed is the retired "FT" mod, because it is the only thing a
            // stored score can carry that means "unpinned caret, and NO line-start snap": the bit
            // did not exist when those runs were recorded, so their frames say clear, and clear on
            // its own means pinned. Apply ORs this in for the caret and still takes the snap
            // straight from the bit, so an FT run re-derives the way it was played.
            if (mods.Any(m => m is TypeBeatModLegacyFletcher))
            {
                engine.FletcherEnabled = true;
                engine.FlexibleCaretFromMod = true;
            }

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

        /// <summary>
        /// Shared with the live feeder so the claim above (that this reproduces the drawable layer's
        /// call sequence exactly) is structural rather than a comment two files apart.
        /// </summary>
        private static void apply(TypingEngine engine, TypeBeatReplayFrame frame) => ReplayEngineFeed.Apply(engine, frame);

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

            public void Seal(ScoreProcessor scoreProcessor, int lineIndex, TypingEngine engine, TypoRule rule)
            {
                if (!lines.TryGetValue(lineIndex, out var line))
                    return;

                foreach ((int cellIndex, var cell) in line.Cells)
                {
                    if (cell.Judged)
                        continue;

                    var result = TypeBeatResultMapping.UnresolvedCellResult(engine.CellLeftWrong(lineIndex, cellIndex), rule);

                    if (result == TypeBeatResultMapping.UNFIXED_TYPO && scoreProcessor is TypeBeatScoreProcessor typeBeatProcessor)
                        typeBeatProcessor.MarkComboNeutral(lineIndex, cellIndex);

                    cell.Apply(scoreProcessor, result);
                }

                line.Apply(scoreProcessor, TypeBeatResultMapping.LINE_RESULT);
            }
        }

        private sealed class LineCells(TypeBeatHitObject lineObject)
        {
            /// <summary>
            /// SORTED, because <see cref="CellRegistry.Seal"/> walks it and since backlog 124 the
            /// order reaches the score: a Miss breaks combo and an unfixed typo is weighted by the
            /// combo it finds. Ascending cell order is what
            /// <c>DrawableTypeBeatHitObject.ApplySealResults</c> walks, and the two have to agree.
            /// </summary>
            public readonly SortedDictionary<int, Cell> Cells = new SortedDictionary<int, Cell>();

            private readonly Slot slot = new Slot(lineObject);

            public void Apply(ScoreProcessor scoreProcessor, HitResult result) => slot.Apply(scoreProcessor, result);
        }

        private sealed class Cell(TypeBeatCharObject charObject)
        {
            private readonly Slot slot = new Slot(charObject);

            /// <summary><c>DrawableHitObject.Judged</c>: this cell has already taken its one result.</summary>
            public bool Judged => slot.Judged;

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

            public bool Judged => judged;

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
