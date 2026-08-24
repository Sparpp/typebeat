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
            };
        }
    }
}
