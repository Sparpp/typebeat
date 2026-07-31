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

            return new TypingEngine(lyricBeatmap, literate);
        }
    }
}
