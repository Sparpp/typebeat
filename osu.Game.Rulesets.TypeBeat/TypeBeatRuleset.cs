// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Input.Bindings;
using osu.Game.Beatmaps;
using osu.Game.Configuration;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osu.Game.Rulesets.Configuration;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Scoring;
using osu.Game.Rulesets.TypeBeat.Beatmaps;
using osu.Game.Rulesets.TypeBeat.Configuration;
using osu.Game.Rulesets.TypeBeat.Edit;
using osu.Game.Rulesets.TypeBeat.Scoring;
using osu.Game.Rulesets.TypeBeat.UI;
using osu.Game.Screens.Edit.Setup;
using osu.Game.Rulesets.UI;
using osuTK;
using osuTK.Graphics;

namespace osu.Game.Rulesets.TypeBeat
{
    public partial class TypeBeatRuleset : Ruleset
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

        public override IEnumerable<Mod> GetModsFor(ModType type) => Array.Empty<Mod>();

        /// <summary>
        /// type!beat maps are stored in the "type!beat file format v1" .osu variant; the legacy
        /// encoder cannot represent the [Lyrics] section, so the ruleset serialises itself. This
        /// also makes the editor treat the ruleset as save-capable despite not being legacy.
        /// </summary>
        public override bool CanEncodeToNativeFormat => true;

        public override void EncodeToNativeFormat(IBeatmap beatmap, System.IO.TextWriter writer) =>
            TypeBeatBeatmapEncoder.Encode(beatmap, writer);

        /// <summary>Compose mode is type!beat's own lyric surface, not a circle composer.</summary>
        public override osu.Game.Screens.Edit.EditorScreen CreateEditorComposeScreen() => new LyricComposeScreen();

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
        /// Desync is punished with score/combo only; the sole fail condition is mashing —
        /// <see cref="TypeBeatHealthProcessor.WRONG_KEY_FAIL_STREAK"/> consecutive wrong keys.
        /// </summary>
        public override HealthProcessor CreateHealthProcessor(double drainStartTime) => new TypeBeatHealthProcessor();

        /// <summary>
        /// CS/AR/OD/HP are meaningless for typing; pace statistics (average WPM/CPM) surface
        /// through the playable beatmap's <see cref="TypeBeatBeatmap.GetStatistics"/> instead.
        /// </summary>
        public override IEnumerable<RulesetBeatmapAttribute> GetBeatmapAttributesForDisplay(IBeatmapInfo beatmapInfo, IReadOnlyCollection<Mod> mods)
            => Array.Empty<RulesetBeatmapAttribute>();

        public override IRulesetConfigManager CreateConfig(SettingsStore? settings) => new TypeBeatRulesetConfigManager(settings, RulesetInfo);

        public override string ShortName => "typebeat";

        public override IEnumerable<KeyBinding> GetDefaultKeyBindings(int variant = 0) => new[]
        {
            new KeyBinding(InputKey.Z, TypeBeatAction.Button1),
            new KeyBinding(InputKey.X, TypeBeatAction.Button2),
        };

        public override Drawable CreateIcon() => new Icon("T!");

        public partial class Icon : CompositeDrawable
        {
            public Icon(string label)
            {
                InternalChildren = new Drawable[]
                {
                    new Circle
                    {
                        Size = new Vector2(20),
                        Colour = Color4.White,
                    },
                    new OsuSpriteText
                    {
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        Text = label,
                        Font = OsuFont.Default.With(size: 18)
                    }
                };
            }
        }

        // Leave this line intact. It will bake the correct version into the ruleset on each build/release.
        public override string RulesetAPIVersionSupported => CURRENT_RULESET_API_VERSION;
    }
}
