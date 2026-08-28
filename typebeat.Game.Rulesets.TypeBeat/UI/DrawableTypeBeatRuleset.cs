// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Input;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Input.Handlers;
using typebeat.Game.Replays;
using typebeat.Game.Rulesets.Objects.Drawables;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Objects.Drawables;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.UI;
using typebeat.Game.Scoring;
using typebeat.Game.Screens.Play;

namespace typebeat.Game.Rulesets.TypeBeat.UI
{
    [Cached]
    public partial class DrawableTypeBeatRuleset : DrawableRuleset<TypeBeatHitObject>
    {
        private TypingEngine? engine;

        /// <summary>
        /// The gameplay/judgement authority, built once from the converted beatmap's lyric
        /// lines. The engine's line order (and thus every CharJudgement.LineIndex) is the
        /// LineIndex order of the hit objects, re-normalized to 0..n-1 for consistency.
        /// </summary>
        public TypingEngine Engine => engine ??= createEngine();

        /// <summary>
        /// Flashlight mod: when &gt; 0, only this many countable characters (typeable, non-space)
        /// either side of the typing caret are lit on the active line, and the rest of the 3-line
        /// stack is hidden. 0 = mod off. Set by <see cref="Mods.TypeBeatModFlashlight"/> during
        /// <c>ApplyToDrawableRuleset</c> (before load); the <see cref="LyricStage"/> reads it each
        /// frame. Purely visual, so judgement, scoring and replays are unaffected.
        /// </summary>
        public int FlashlightVisibleRadius { get; set; }

        /// <summary>
        /// Recite mod: when true, every cell the player has not typed yet is hidden (alpha 0), so
        /// the lyric is only ever written out one keypress behind the player. Set by
        /// <see cref="Mods.TypeBeatModRecite"/> during <c>ApplyToDrawableRuleset</c> (before load);
        /// the <see cref="LyricStage"/> reads it each frame and pushes it to every line display.
        ///
        /// <para>Purely visual, exactly like <see cref="FlashlightVisibleRadius"/> and for the same
        /// reason it lives here rather than on the engine: it changes no judgement, so it is not an
        /// ERA flag, needs no CONFIG-frame bit and no <c>ReplayEngineFeed</c> arm. A stored replay
        /// re-derives the hiding from its mod list, which is why this can be applied late.</para>
        /// </summary>
        public bool HideUpcomingText { get; set; }

        private IReadOnlyList<InstrumentalSkipSection>? instrumentalSkipSections;

        /// <summary>
        /// Long purely-instrumental stretches between lyric lines (see <see cref="InstrumentalGaps"/>),
        /// mapped to the shared skip-section shape <see cref="Player"/> consumes to offer a mid-song
        /// skip. The seek target keeps the same run-up before the next line that the intro skip leaves
        /// before the first line.
        /// </summary>
        public override IReadOnlyList<InstrumentalSkipSection> InstrumentalSkipSections =>
            instrumentalSkipSections ??= InstrumentalGaps.Compute(Engine.Lines)
                                                         .Select(g => new InstrumentalSkipSection(g.GapStartTime, g.SkipTarget))
                                                         .ToArray();

        public DrawableTypeBeatRuleset(TypeBeatRuleset ruleset, IBeatmap beatmap, IReadOnlyList<Mod>? mods = null)
            : base(ruleset, beatmap, mods)
        {
        }

        protected override Playfield CreatePlayfield() => new TypeBeatPlayfield(Engine);

        public override DrawableHitObject<TypeBeatHitObject> CreateDrawableRepresentation(TypeBeatHitObject h) => new DrawableTypeBeatHitObject(h);

        protected override PassThroughInputManager CreateInputManager() => new TypeBeatInputManager(Ruleset.RulesetInfo);

        protected override ReplayInputHandler CreateReplayInputHandler(Replay replay) => new TypeBeatFramedReplayInputHandler(replay);

        protected override ReplayRecorder CreateReplayRecorder(Score score) => new TypeBeatReplayRecorder(score, Engine);

        /// <summary>
        /// Recording seam for the playfield's key handler: forwards one EFFECTIVE typing input
        /// (accepted char, rejected-wrong char, or erasing backspace) to the active recorder, if
        /// any. No-op while not recording (e.g. watching a replay, where the feeder drives the
        /// engine directly and must not be re-recorded).
        /// </summary>
        internal void RecordTypingInput(char character, double time) =>
            ((KeyBindingInputManager as IHasRecordingHandler)?.Recorder as TypeBeatReplayRecorder)?.RecordInput(character, time);

        private TypingEngine createEngine()
        {
            var lineObjects = Beatmap.HitObjects.OrderBy(h => h.LineIndex).ToList();

            // Normalize indices so engine position == LineIndex == playfield registry key.
            for (int i = 0; i < lineObjects.Count; i++)
                lineObjects[i].LineIndex = i;

            TimingGranularity granularity = lineObjects.Count > 0 ? lineObjects[0].Granularity : TimingGranularity.Line;

            var lyricBeatmap = new LyricBeatmap
            {
                Metadata = new LyricBeatmapMetadata
                {
                    Artist = Beatmap.BeatmapInfo.Metadata.Artist,
                    Title = Beatmap.BeatmapInfo.Metadata.Title,
                    FolderPath = string.Empty,
                    AudioFileName = Beatmap.BeatmapInfo.Metadata.AudioFile,
                    HasWordTiming = granularity != TimingGranularity.Line,
                },
                Lines = lineObjects.Select(h => h.Line).ToList(),
                Granularity = granularity,
            };

            // Literate changes the cell list itself, so it must be known BEFORE the engine is built
            // (see TypingLine.FromLyricLine). Read off the mod list rather than a drawable-ruleset
            // flag, because ApplyToDrawableRuleset has no guaranteed order against the first Engine
            // read; TypeBeatModLiterate stamps the same value onto the hit objects, so the engine's
            // cells and the nested scoring objects are flattened identically by construction.
            bool literate = Mods?.Any(m => m is TypeBeatModLiterate) == true;

            // HARD ROCK REVERTS THE JUDGEMENT RULE (backlog 180): under HR every window is halved,
            // and a rule that hands out delta 0 anywhere inside a syllable's whole sung span
            // undercuts that halving, because most of the presses the tightened ladder is meant to
            // price never reach it. So HR alone judges on the classic per-character point targets.
            // Read off the mod list here rather than written by TypeBeatModHardRock.ApplyToDrawableRuleset
            // (which is where the window scale is applied) for the same reason Literate is: mod
            // application has no guaranteed order against the first Engine read, and deciding it at
            // construction means the flag is never momentarily wrong. The window scale can be
            // applied late because it is re-read per judgement; an era flag read by the replay
            // recorder's CONFIG frame cannot.
            bool hardRock = Mods?.Any(m => m is TypeBeatModHardRock) == true;

            // FLEXIBLE LINES (backlog 208), read here for exactly the reason Hard Rock's judgement
            // rule is: it is an ERA pair the replay recorder's CONFIG frame stamps at the first
            // keystroke, and an era flag cannot be applied late from ApplyToDrawableRuleset, which
            // has no guaranteed order against the first Engine read. TypeBeatModFletcher re-asserts
            // the same two values from that seam and cannot disagree, both being derived from this
            // same "is the mod in the list" question.
            bool pinnedLines = Mods?.Any(m => m is TypeBeatModFletcher) == true;

            // The retired "FT" mod, which can only ever arrive on a REPLAY being watched (it is a
            // System mod, unselectable). It says the run was played with an unpinned caret and no
            // line-start snap, and its frames carry bit 5 CLEAR, so the CONFIG frame alone would
            // re-pin a caret that was played free. ReplayEngineFeed.Apply ORs this back in.
            bool legacyFletcher = Mods?.Any(m => m is TypeBeatModLegacyFletcher) == true;

            return new TypingEngine(lyricBeatmap, literate)
            {
                // THE live judgement rule since backlog 179, for every player and (since backlog
                // 180) every mod stack but Hard Rock: a character typed while its syllable is being
                // sung is perfectly timed. Backlog 174 shipped this as a debug-only experiment and
                // this seam was its gate; there is no build gate any more, and the flag doubles as
                // the ERA arm every stored replay needs (TypeBeatReplayScorer re-derives on the
                // CONFIG frame's bit 2, and a replay written before backlog 179, or any HR replay,
                // re-derives on point targets forever, with no mod inspection in the scorer).
                // JUDGEMENT ONLY, since backlog 175: the lit-syllable look is unconditional
                // rendering off TypingLine.Syllables (backlog 177) and this flag does not gate it,
                // so an HR run still SEES the sung syllable light up while being graded per char.
                SyllableTiming = !hardRock,

                // THE live input model since backlog 181, for every player and EVERY mod stack,
                // Hard Rock included: a wrong letter pressed on a word gap is typed through exactly
                // as one pressed on a lyric character is. Unconditional on purpose, and the
                // contrast with the line above is the argument: HR reverts a judgement rule because
                // it halves the judgement WINDOWS, and this is not a window, it is which cells the
                // wrong-input model reaches. HR runs already type wrong letters through everywhere
                // else, so gating this on it would leave the word gap as the one cell in the map
                // behaving differently under one mod, for no reason the mod is about.
                //
                // It is still an ERA flag, because every replay recorded before this shipped holds
                // rejected wrong-key-on-gap frames that must stay rejected forever
                // (TypeBeatReplayScorer re-derives on the CONFIG frame's bit 3, with no mod
                // inspection anywhere in the scorer).
                WrongInputOnWordGaps = true,

                // THE live space rule since backlog 184, and unconditional for the same reason the
                // line above is: it is the input model, not a window, so no mod has an opinion about
                // it. A gap typo parks the caret on the gap it spoiled (the space is still owed, and
                // without the park the next space fed the skip gate a spoiled gap and gave up a whole
                // word), and with word skipping off a mid-word space is a typo rather than a
                // rejection. Deliberately NOT a user setting: it is what the spacebar MEANS here.
                //
                // An ERA flag as well, on CONFIG frame bit 4, because every replay recorded before it
                // holds spaces whose caret consequence was the other one, and re-deriving those under
                // this rule would shift every keystroke after the first of them.
                StrictSpaces = true,

                // THE narrowing of the judgement rule since backlog 209, unconditional for every
                // mod stack: a freestyle slot and a run of three or more identical characters
                // inside one syllable are judged on their own character targets, because the span
                // rule paid a whole mashed run a delta of zero (a field report had accuracy going UP
                // for spamming a freestyle section ahead of the vocal).
                //
                // Set even under Hard Rock, where it is INERT: HR turns SyllableTiming off, so its
                // cells are already point-timed and this flag changes nothing it does. Recording it
                // regardless is what keeps re-derivation uniform, the same convention bits 3 and 4
                // follow, and it means no reader ever has to work out which stacks stamped it.
                //
                // An ERA flag on CONFIG frame bit 6, because every replay recorded before it holds
                // presses that WERE paid across the whole span, and re-deriving those on character
                // targets would invent a worse score than the one the player was shown.
                CharTimedStretch = true,

                // THE LIVE CARET SINCE BACKLOG 208, for every stack except the pinning mod's. The
                // three freedoms that shipped as the "FT" mod (open the next line the moment you
                // finish one, keep a line the song has left, character distance instead of a timing
                // lock) are what the game does now, and TypeBeatModFletcher is what takes them back.
                //
                // The two flags are separate because the SNAP is new with 208 and no older run has
                // it: a caret sitting past the end of its line is handed to the next line when that
                // line starts, which is what keeps the unpinned default feeling like the pinned game
                // it replaced. An "FT" replay must re-derive WITHOUT it, so it is its own era bit
                // (CONFIG frame bit 5) rather than something FletcherEnabled implies.
                FletcherEnabled = !pinnedLines,
                FlexibleLineSnap = !pinnedLines,

                // THE RUSH BOUND SINCE BACKLOG 218, and the symmetry the unpinned caret shipped
                // without: drag borrows 1500 ms past a line's end, so rush may enter a line 1500 ms
                // before its cue and not a beat earlier. Unbounded, the roll was transitive and a
                // fast player could type the whole map at the top of the song.
                //
                // Set UNCONDITIONALLY, the pinning mod included, where it is INERT (FletcherEnabled
                // gates every roll it bounds, and that flag is false there): the same convention
                // bits 3, 4 and 6 follow, and it means no reader has to work out which stacks
                // stamped it. Not folded into FlexibleLineSnap, because the two answer different
                // questions about the same era and every "FT" and pre-218 run on disk answers them
                // differently: a stored run's rushed keystrokes LANDED, and re-deriving them under
                // the bound would refuse them outright, so this is CONFIG frame bit 7 of its own.
                BoundedRush = true,

                FlexibleCaretFromMod = legacyFletcher,
            };
        }
    }
}
