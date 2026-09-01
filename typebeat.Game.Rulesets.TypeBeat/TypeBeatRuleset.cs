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
            // Runs when RulesetStore instantiates the ruleset at game startup, before any
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

        /// <summary>
        /// pp for a finished score, for the shared results-screen components that reach it this way
        /// (the score panel's pp readout and the performance breakdown chart). Without it they fall
        /// back to a hardcoded 0 for every type!beat play.
        /// </summary>
        public override PerformanceCalculator CreatePerformanceCalculator() => new TypeBeatPerformanceCalculator(this);

        /// <summary>
        /// type!beat narrows the base rule with one gate of its own: only the BASE rates earn pp
        /// (DT/NC 1.50x, HT 0.75x, docs/pp.md). A custom rate still ranks on the score leaderboards
        /// exactly as before, it simply earns nothing, and no number describes that.
        ///
        /// <para>THIS IS THE SINGLE AUTHORITY on whether a type!beat play can earn pp. The score
        /// panel asks it through <c>score.Ruleset.CreateInstance()</c> and the results table asks it
        /// through <see cref="PerformancePointsDisplay.Eligible"/>, so the two surfaces on the same
        /// screen cannot disagree about whether a play was ever in the running.</para>
        /// </summary>
        public override bool ScoreEarnsPerformancePoints(ScoreInfo score)
            => base.ScoreEarnsPerformancePoints(score) && PerformancePoints.EligibleRate(score.Mods) != null;

        public override IEnumerable<Mod> GetModsFor(ModType type) => type switch
        {
            ModType.DifficultyReduction => new Mod[]
            {
                new TypeBeatModEasy(),
                new TypeBeatModNoFail(),
                new TypeBeatModHalfTime(),
            },
            ModType.DifficultyIncrease => new Mod[]
            {
                new TypeBeatModHardRock(),
                new TypeBeatModSuddenDeath(),
                new TypeBeatModDoubleTime(),
                new TypeBeatModNightcore(),
                new TypeBeatModFlashlight(),
                new TypeBeatModLiterate(),
                new TypeBeatModRecite(),
            },
            ModType.Conversion => new Mod[]
            {
                new TypeBeatModFletcher(),
                new TypeBeatModGatekeeper(),
                new TypeBeatModDyslexia(),
            },
            ModType.Automation => new Mod[]
            {
                new TypeBeatModAutoplay(),
                new TypeBeatModMashing(),
            },
            ModType.Fun => new Mod[]
            {
                new TypeBeatModMuted(),
                new ModWindUp(),
                new ModWindDown(),
                new TypeBeatModPuppeteer(),
            },
            // NOT a player-facing column: ModSelectOverlay builds columns for the five types above
            // only, and marks every System mod invalid for selection. What listing them here DOES
            // buy is resolution: Ruleset.CreateAllMods walks every ModType, so a stored score's
            // acronym still resolves to a real mod instead of UnknownMod.
            //
            // "FT" keeps its 0.98x multiplier, its 0.90x pp and (crucially) the
            // flexible-caret-without-snap era it was played under; see TypeBeatModLegacyFletcher.
            // "CT" keeps its own control law, so an old Conductor replay still watches as it was
            // played; backlog 257 retired it when Puppeteer took over as the one follower (and took
            // the NAME "Conductor" with it), and see TypeBeatModConductor for why the acronym can
            // never be re-pointed at the mod that replaced it.
            ModType.System => new Mod[]
            {
                new TypeBeatModLegacyFletcher(),
                new TypeBeatModConductor(),
            },
            _ => Array.Empty<Mod>(),
        };

        public override ScoreMultiplierCalculator CreateScoreMultiplierCalculator(ScoreMultiplierContext context) =>
            new TypeBeatScoreMultiplierCalculator(context);

        /// <summary>
        /// type!beat maps are stored in the "type!beat file format" .osu variant (currently v2,
        /// <see cref="LyricOsuFormat.FORMAT_VERSION"/>); the legacy encoder cannot represent the
        /// [Lyrics] section, so the ruleset serialises itself. This also makes the editor treat the
        /// ruleset as save-capable despite not being legacy.
        /// </summary>
        public override bool CanEncodeToNativeFormat => true;

        public override void EncodeToNativeFormat(IBeatmap beatmap, Storyboard? storyboard, System.IO.TextWriter writer) =>
            TypeBeatBeatmapEncoder.Encode(beatmap, storyboard, writer);

        /// <summary>
        /// Three things in this format are cosmetic for online status. The intro beatdrop
        /// (<c>beatdrop_ms</c>) only soundtracks the main-menu intro, the [Events] video offset only
        /// syncs a decorative clip to the song, and the magic line's FORMAT VERSION says how to read
        /// the file rather than what is in it; none of the three has any bearing on gameplay or
        /// scoring. So a save that changes only those must not demote a ranked map to
        /// LocallyModified: compare with all three normalised out. Everything else, the video's FILE
        /// included, still counts.
        /// </summary>
        public override bool NativeEncodingsEquivalentForStatus(string encodedA, string encodedB) =>
            normaliseForStatus(encodedA) == normaliseForStatus(encodedB);

        private static string normaliseForStatus(string encoded) =>
            LyricOsuFormat.StripFormatVersion(LyricOsuFormat.StripVideoOffset(LyricOsuFormat.StripBeatdrop(encoded)));

        /// <summary>Compose mode is type!beat's own lyric surface, not a circle composer.</summary>
        public override typebeat.Game.Screens.Edit.EditorScreen CreateEditorComposeScreen() => new LyricComposeScreen();

        /// <summary>
        /// The editor setup screen for type!beat: song metadata, audio/background resources, and a
        /// type!beat section (global offset + in-editor auto-timing). The circle-game sections
        /// (difficulty, combo colours, design) are dropped; they are meaningless for lyrics.
        ///
        /// <para>None of these may set <see cref="Drawable.RelativeSizeAxes"/> on X.
        /// <see cref="SetupScreen"/> assigns every section it is handed
        /// <c>Width = SetupScreen.COLUMN_WIDTH</c>, which a relative-X section reads as a MULTIPLE of
        /// its parent (450 columns wide, ~417000px), pushing the section's captions and controls
        /// hundreds of thousands of pixels off both edges of the screen. The base ruleset can set it
        /// because it nests its ResourcesSection inside a plain container that takes the width
        /// instead; type!beat lists the section directly, so the section itself must be the 450.</para>
        /// </summary>
        public override IEnumerable<Drawable> CreateEditorSetupSections() => new Drawable[]
        {
            new MetadataSection(),
            new ResourcesSection(),
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
        /// Rank comes from COMPLETION (% of the map typed), not accuracy; see
        /// <see cref="TypeBeatScoreProcessor"/>. Score, combo and accuracy stay standardised.
        /// </summary>
        public override ScoreProcessor CreateScoreProcessor() => new TypeBeatScoreProcessor(this);

        /// <summary>
        /// type!beat only ever awards Great/Ok/Meh and the implicit Miss. Restricting the
        /// valid results keeps the base ruleset from surfacing spurious rows on the results card,
        /// notably the obsolete <see cref="HitResult.LegacyComboIncrease"/>, which the base "all
        /// enum values" default would otherwise emit at count 0.
        ///
        /// <para>The uncorrected-typo tier (<see cref="TypeBeatResultMapping.UNFIXED_TYPO"/>) is
        /// deliberately NOT listed, which is backlog 140. A cell can still end up as one and it
        /// still costs accuracy, completion and rank exactly as it did; what it no longer does is
        /// show up as a SECOND typo number. The player-facing account of typing wrong is one stat,
        /// TYPOS, which counts wrong KEYPRESSES (see
        /// <see cref="CreateCompletionStatistics"/>), and every uncorrected typo'd cell implied one
        /// of those, so listing the seal-state count beside it offered a smaller number under a
        /// near-identical name and no way to tell which was which. This list is what the judgement
        /// counter, the in-game score table and the results card read, so dropping it here is what
        /// retires the split everywhere at once.</para>
        ///
        /// <para>Backlog 213 makes that absence LOAD-BEARING rather than merely tidy: the typo's
        /// count is now folded into the Miss column (<see cref="GetDisplayResultFor"/>), so listing
        /// it here would give it a column of its own AND add it to Miss, double-counting it and
        /// breaking the property that the shown columns sum to the judged cell count.</para>
        /// </summary>
        public override IEnumerable<HitResult> GetValidHitResults() => new[]
        {
            HitResult.Great,
            HitResult.Ok,
            HitResult.Meh,
        };

        /// <summary>
        /// <see cref="TypeBeatResultMapping.UNFIXED_TYPO"/> is a borrowed enum member, not a grade:
        /// its stock description would print "Good" beside Great/Ok/Meh, which reads as the opposite
        /// of what it is. Everything else keeps the base description.
        ///
        /// <para>Kept after backlog 140 dropped the result from
        /// <see cref="GetValidHitResults"/>, which is what the results card and the judgement
        /// counter filter on: the name is a property of the result, and anything that asks for one
        /// by a route that does not consult that list must still be told the truth rather than
        /// "Good".</para>
        /// </summary>
        public override LocalisableString GetDisplayNameForHitResult(HitResult result)
            => result == TypeBeatResultMapping.UNFIXED_TYPO ? "Typo" : base.GetDisplayNameForHitResult(result);

        /// <summary>
        /// The display fold of backlog 213: an UNCORRECTED TYPO
        /// (<see cref="TypeBeatResultMapping.UNFIXED_TYPO"/>) is counted in the MISS column.
        ///
        /// <para>Which is the display half of the same one-sided change the accuracy weight
        /// (<see cref="Scoring.UnfixedTypoWorthRule"/>) and pp
        /// (<c>PerformancePoints.CountNotes</c>) make: the seal keeps writing the typo's own
        /// key, so the WIRE, realm and MessagePack shapes are untouched and a stored row means today
        /// what it meant when it was submitted, and every CONSUMER reclassifies. The fold therefore
        /// reaches OLD rows and new ones alike, which is the point: the field report that forced it
        /// was a stored score reading MISS 0 while carrying <c>good: 2</c>, so two characters the
        /// player never typed right appeared in no column at all.</para>
        ///
        /// <para>The typo takes no column of its own, and must not: it is absent from
        /// <see cref="GetValidHitResults"/> (backlog 140 dropped it, for its own reasons), so
        /// nothing yields it as a row and the fold cannot double-count. With it folded in, the shown
        /// columns (Great, Ok, Meh, Miss) once again SUM to the judged cell count, which they had
        /// not done since backlog 124 gave the typo a key nothing displayed.</para>
        ///
        /// <para>Read by <c>ScoreInfo.GetStatisticsForDisplay</c> (the results panels, the
        /// leaderboard tooltips, the beatmap-set score table) and by the live judgement counter, so
        /// it is the one place the fold is implemented for every surface at once.
        /// <see cref="CreateCompletionStatistics"/>'s "Missed characters" row folds the same way,
        /// and cannot come through here because it reads the score directly.</para>
        /// </summary>
        public override HitResult GetDisplayResultFor(HitResult result)
            => result == TypeBeatResultMapping.UNFIXED_TYPO ? HitResult.Miss : result;

        /// <summary>
        /// Results-screen statistics: completion (the number the rank is graded on) alongside the
        /// judgement counts. The accuracy shown in the expanded panel is unchanged.
        ///
        /// <para>TYPOS is ONE number (backlog 140), counting wrong KEYPRESSES as events, and it is
        /// the only typo figure the player is shown: the cells left holding a wrong character at the
        /// seal are not counted separately, because each of them took a wrong keypress that this
        /// number already carries. It sits BESIDE the missed-character count and is not folded into
        /// it, which is a different statement from backlog 213's fold: a keypress is an event and a
        /// cell is a cell, so the typo count still counts every wrong key including the ones the
        /// player went back and fixed, while an UNCORRECTED typo's CELL is now a missed character
        /// and is counted in the row above. The row appears only for a score that actually CARRIES
        /// the stat (<see cref="TypeBeatScoreProcessor.MistypesOf"/>); plays from before it existed
        /// show no row at all rather than a fabricated 0.</para>
        ///
        /// <para>PP closes the table, after the raw counts it is derived from: it is what the whole
        /// play was worth. Unlike the typo row it is unconditional, because a pp reading is
        /// always knowable, either as a number or as "this could never have earned any"
        /// (<see cref="PerformancePointsDisplay"/>, which also owns the gates and the rounding, so
        /// this table and the live in-game counter can never disagree). No round trip is involved:
        /// an offline play, an imported <c>.osr</c> and a replay downloaded from the website are all
        /// priced here on the spot; a play the server DID price shows the server's number.</para>
        /// </summary>
        public override StatisticItem[] CreateStatisticsForScore(ScoreInfo score, IBeatmap playableBeatmap) => new[]
        {
            new StatisticItem("Completion", () => new SimpleStatisticTable(2, CreateCompletionStatistics(score, playableBeatmap))),
        };

        /// <summary>
        /// The rows of the completion table. Public so a test can pin WHICH rows a score gets
        /// (notably: none for typos on a play that carries no typo stat) against the very
        /// list the results screen renders, rather than a second copy of the rule.
        /// </summary>
        /// <param name="score">The score being shown.</param>
        /// <param name="playableBeatmap">
        /// The map it was set on, converted with its mods, which is what the pp row is priced from.
        /// Null is the honest reading of "no map to price against", and shows pp as ineligible.
        /// </param>
        public static SimpleStatisticItem[] CreateCompletionStatistics(ScoreInfo score, IBeatmap? playableBeatmap = null)
        {
            var items = new List<SimpleStatisticItem>
            {
                new CompletionStatistic(TypeBeatScoreProcessor.ComputeCompletion(score)),
                new SimpleStatisticItem<int>("Missed characters")
                {
                    // Backlog 213: a character left holding the WRONG letter is a missed character
                    // too. Folded here rather than at the seal, exactly as in
                    // GetDisplayResultFor, and it reaches old rows for the same reason.
                    Value = score.Statistics.GetValueOrDefault(HitResult.Miss)
                            + score.Statistics.GetValueOrDefault(TypeBeatResultMapping.UNFIXED_TYPO),
                },
            };

            if (TypeBeatScoreProcessor.MistypesOf(score) is int typos)
            {
                items.Add(new SimpleStatisticItem<int>("Typos")
                {
                    Value = typos,
                });
            }

            items.Add(new PerformanceStatistic(PerformancePointsDisplay.ForScore(score, playableBeatmap)));

            return items.ToArray();
        }

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
        /// The play's performance points, or <see cref="PerformancePointsDisplay.INELIGIBLE_TEXT"/>
        /// when it could never have earned any. Nullable rather than a plain double precisely so
        /// those two stay apart: 0 is a real price (a give-up run is worth 0 and still counts),
        /// while "ineligible" is the absence of a price.
        /// </summary>
        private partial class PerformanceStatistic : SimpleStatisticItem<double?>
        {
            public PerformanceStatistic(double? pp)
                : base("pp")
            {
                Value = pp;
            }

            protected override LocalisableString DisplayValue(double? value) => PerformancePointsDisplay.Format(value);
        }

        /// <summary>
        /// CS/AR/OD/HP are meaningless for typing; pace statistics (average WPM/CPM) surface
        /// through the playable beatmap's <see cref="TypeBeatBeatmap.GetStatistics"/> instead.
        /// </summary>
        public override IEnumerable<RulesetBeatmapAttribute> GetBeatmapAttributesForDisplay(IBeatmapInfo beatmapInfo, IReadOnlyCollection<Mod> mods)
            => Array.Empty<RulesetBeatmapAttribute>();

        public override IRulesetConfigManager CreateConfig(SettingsStore? settings) => new TypeBeatRulesetConfigManager(settings, RulesetInfo);

        public override RulesetSettingsSubsection CreateSettings() => new TypeBeatSettingsSubsection(this);

        public override RulesetSettingsSubsection CreateExperimentalSettings() => new TypeBeatExperimentalSettingsSubsection(this);

        public override string ShortName => "typebeat";

        /// <summary>
        /// type!beat's server-side ruleset ID. Claiming a "legacy" ID is what gives the ruleset
        /// an <c>OnlineID</c> (0); without one, score submission (<c>SoloPlayer</c>) and global
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

        /// <summary>
        /// The ruleset's rebindable actions, as shown in the key configuration screen's type!beat
        /// section. Z/X are vestigial (typing is taken from raw key events, so they never fire while
        /// a line is being typed); the TYPING GESTURES are the real content, defaulting to the chords
        /// (and, for the line skip, the keys) every other typing site uses for them.
        ///
        /// <para>Appended to <see cref="TypeBeatAction"/> rather than inserted, because the stored
        /// binding rows key off the enum's INTEGER value: renumbering Button1/Button2 would silently
        /// re-point every existing user's saved rows.</para>
        /// </summary>
        public override IEnumerable<KeyBinding> GetDefaultKeyBindings(int variant = 0) => new[]
        {
            new KeyBinding(InputKey.Z, TypeBeatAction.Button1),
            new KeyBinding(InputKey.X, TypeBeatAction.Button2),
            new KeyBinding(new KeyCombination(InputKey.Control, InputKey.BackSpace), TypeBeatAction.EraseWord),
            new KeyBinding(new KeyCombination(InputKey.Control, InputKey.A), TypeBeatAction.SelectBackToTypo),

            // The line skip (backlog 241) takes BOTH Enter keys, which is why this list is not one
            // binding per action: a keyboard has two keys that mean "next" and a player reaching for
            // either of them means the same thing.
            new KeyBinding(InputKey.Enter, TypeBeatAction.SkipLine),
            new KeyBinding(InputKey.KeypadEnter, TypeBeatAction.SkipLine),
        };

        public override Drawable CreateIcon() => new Icon();

        /// <summary>
        /// Rendered in the toolbar ruleset button and the intro's ruleset flow. Deliberately
        /// glyphless; the label rendered poorly at toolbar size.
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
