// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Globalization;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Input.Bindings;
using osu.Framework.Localisation;
using typebeat.Game.Beatmaps;
using typebeat.Game.Configuration;
using typebeat.Game.Overlays.Settings;
using typebeat.Game.Rulesets.Configuration;
using typebeat.Game.Rulesets.Difficulty;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Replays.Types;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.Scoring.Legacy;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Edit;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Scoring;
using typebeat.Game.Screens.Edit.Setup;
using typebeat.Game.Screens.Ranking.Statistics;
using typebeat.Game.Rulesets.UI;
using typebeat.Game.Storyboards;
using osuTK;
using osuTK.Graphics;

namespace typebeat.Game.Rulesets.TypeBeat
{
    public partial class TypeBeatRuleset : Ruleset, ILegacyRuleset
    {
        static TypeBeatRuleset()
        {
            // Runs when RulesetStore instantiates the ruleset at game startup — before any
            // beatmap import or load can request a decoder, covering both paths. Tests call
            // Register() directly.
            LyricBeatmapDecoder.Register();
        }

        public override string Description => "type!beat";

        public override string PlayingVerb => "Typing lyrics";

        public override DrawableRuleset CreateDrawableRulesetWith(IBeatmap beatmap, IReadOnlyList<Mod>? mods = null) =>
            new DrawableTypeBeatRuleset(this, beatmap, mods);

        public override IBeatmapConverter CreateBeatmapConverter(IBeatmap beatmap) =>
            new TypeBeatBeatmapConverter(beatmap, this);

        public override DifficultyCalculator CreateDifficultyCalculator(IWorkingBeatmap beatmap) =>
            new TypeBeatDifficultyCalculator(RulesetInfo, beatmap);

        public override IEnumerable<Mod> GetModsFor(ModType type) => type switch
        {
            ModType.DifficultyReduction => new Mod[]
            {
                new TypeBeatModNoFail(),
                new TypeBeatModHalfTime(),
            },
            ModType.DifficultyIncrease => new Mod[]
            {
                new TypeBeatModSuddenDeath(),
                new TypeBeatModDoubleTime(),
                new TypeBeatModNightcore(),
                new TypeBeatModFlashlight(),
                new TypeBeatModLiterate(),
            },
            ModType.Automation => new Mod[]
            {
                new TypeBeatModAutoplay(),
                new TypeBeatModMashing(),
            },
            ModType.Fun => new Mod[]
            {
                new ModWindUp(),
                new ModWindDown(),
            },
            _ => Array.Empty<Mod>(),
        };

        public override ScoreMultiplierCalculator CreateScoreMultiplierCalculator(ScoreMultiplierContext context) =>
            new TypeBeatScoreMultiplierCalculator(context);

        /// <summary>
        /// type!beat maps are stored in the "type!beat file format v1" .osu variant; the legacy
        /// encoder cannot represent the [Lyrics] section, so the ruleset serialises itself. This
        /// also makes the editor treat the ruleset as save-capable despite not being legacy.
        /// </summary>
        public override bool CanEncodeToNativeFormat => true;

        public override void EncodeToNativeFormat(IBeatmap beatmap, Storyboard? storyboard, System.IO.TextWriter writer) =>
            TypeBeatBeatmapEncoder.Encode(beatmap, storyboard, writer);

        /// <summary>
        /// The intro beatdrop (<c>beatdrop_ms</c>) only soundtracks the main-menu intro — it has no
        /// bearing on gameplay or scoring. So a save that changes only the beatdrop must not demote a
        /// ranked map to LocallyModified: compare with the beatdrop field normalised out.
        /// </summary>
        public override bool NativeEncodingsEquivalentForStatus(string encodedA, string encodedB) =>
            LyricOsuFormat.StripBeatdrop(encodedA) == LyricOsuFormat.StripBeatdrop(encodedB);

        /// <summary>Compose mode is type!beat's own lyric surface, not a circle composer.</summary>
        public override typebeat.Game.Screens.Edit.EditorScreen CreateEditorComposeScreen() => new LyricComposeScreen();

        /// <summary>
        /// The editor setup screen for type!beat: song metadata, audio/background resources, and a
        /// type!beat section (global offset + in-editor auto-timing). The circle-game sections
        /// (difficulty, combo colours, design) are dropped — they are meaningless for lyrics.
        /// </summary>
        public override IEnumerable<Drawable> CreateEditorSetupSections() => new Drawable[]
        {
            new MetadataSection(),
            new ResourcesSection { RelativeSizeAxes = Axes.X },
            new TypeBeatSetupSection(),
        };

        /// <summary>
        /// Health is a genuine HP pool (<see cref="TypeBeatHealthProcessor"/>): untyped cells drain
        /// it and typing recovers it, so sustained AFK fails the play, while mashing
        /// (<see cref="TypeBeatHealthProcessor.WRONG_KEY_FAIL_STREAK"/> consecutive wrong keys) fails
        /// outright. Score/accuracy/combo are unaffected by health.
        /// </summary>
        public override HealthProcessor CreateHealthProcessor(double drainStartTime) => new TypeBeatHealthProcessor();

        /// <summary>
        /// Rank comes from COMPLETION (% of the map typed), not accuracy — see
        /// <see cref="TypeBeatScoreProcessor"/>. Score, combo and accuracy stay standardised.
        /// </summary>
        public override ScoreProcessor CreateScoreProcessor() => new TypeBeatScoreProcessor(this);

        /// <summary>
        /// type!beat only ever awards Great/Ok/Meh (+ implicit Miss). Restricting the valid results
        /// keeps the base ruleset from surfacing spurious rows on the results card — notably the
        /// obsolete <see cref="HitResult.LegacyComboIncrease"/>, which the base "all enum values"
        /// default would otherwise emit at count 0.
        /// </summary>
        public override IEnumerable<HitResult> GetValidHitResults() => new[]
        {
            HitResult.Great,
            HitResult.Ok,
            HitResult.Meh,
        };

        /// <summary>
        /// Results-screen statistics: completion (the number the rank is graded on) alongside the
        /// judgement counts. The accuracy shown in the expanded panel is unchanged.
        /// </summary>
        public override StatisticItem[] CreateStatisticsForScore(ScoreInfo score, IBeatmap playableBeatmap) => new[]
        {
            new StatisticItem("Completion", () => new SimpleStatisticTable(2, new SimpleStatisticItem[]
            {
                new CompletionStatistic(TypeBeatScoreProcessor.ComputeCompletion(score)),
                new SimpleStatisticItem<int>("Missed characters")
                {
                    Value = score.Statistics.GetValueOrDefault(HitResult.Miss),
                },
            })),
        };

        /// <summary>"98.7%" formatting for the completion statistic.</summary>
        private partial class CompletionStatistic : SimpleStatisticItem<double>
        {
            public CompletionStatistic(double completion)
                : base("Completion")
            {
                Value = completion;
            }

            protected override LocalisableString DisplayValue(double value) => value.ToString("0.0%", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// CS/AR/OD/HP are meaningless for typing; pace statistics (average WPM/CPM) surface
        /// through the playable beatmap's <see cref="TypeBeatBeatmap.GetStatistics"/> instead.
        /// </summary>
        public override IEnumerable<RulesetBeatmapAttribute> GetBeatmapAttributesForDisplay(IBeatmapInfo beatmapInfo, IReadOnlyCollection<Mod> mods)
            => Array.Empty<RulesetBeatmapAttribute>();

        public override IRulesetConfigManager CreateConfig(SettingsStore? settings) => new TypeBeatRulesetConfigManager(settings, RulesetInfo);

        public override RulesetSettingsSubsection CreateSettings() => new TypeBeatSettingsSubsection(this);

        public override string ShortName => "typebeat";

        /// <summary>
        /// type!beat's server-side ruleset ID. Claiming a "legacy" ID is what gives the ruleset
        /// an <c>OnlineID</c> (0) — without one, score submission (<c>SoloPlayer</c>) and global
        /// leaderboards (<c>LeaderboardManager</c>) silently no-op. Our server owns all
        /// interpretation of ID 0; there is no osu!standard to collide with.
        /// </summary>
        public int LegacyID => 0;

        public ILegacyScoreSimulator CreateLegacyScoreSimulator() => new TypeBeatLegacyScoreSimulator();

        /// <summary>
        /// Required for decoding stored replays (.osr) back into typed frames; see
        /// <see cref="TypeBeatReplayFrame"/> for the frame format and its legacy mapping.
        /// </summary>
        public override IConvertibleReplayFrame CreateConvertibleReplayFrame() => new TypeBeatReplayFrame();

        public override IEnumerable<KeyBinding> GetDefaultKeyBindings(int variant = 0) => new[]
        {
            new KeyBinding(InputKey.Z, TypeBeatAction.Button1),
            new KeyBinding(InputKey.X, TypeBeatAction.Button2),
        };

        public override Drawable CreateIcon() => new Icon();

        /// <summary>
        /// Rendered in the toolbar ruleset button and the intro's ruleset flow. Deliberately
        /// glyphless — the label rendered poorly at toolbar size.
        /// </summary>
        public partial class Icon : CompositeDrawable
        {
            public Icon()
            {
                InternalChild = new Circle
                {
                    Size = new Vector2(20),
                    Colour = Color4.White,
                };
            }
        }

        // Leave this line intact. It will bake the correct version into the ruleset on each build/release.
        public override string RulesetAPIVersionSupported => CURRENT_RULESET_API_VERSION;
    }
}
