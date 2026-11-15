// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;

namespace typebeat.Game.Configuration.Presets
{
    /// <summary>
    /// Every share code schema there has ever been, oldest first. APPEND ONLY: a shipped schema is
    /// frozen forever, because codes players have already pasted into chats and guides carry its version
    /// and are decoded against it. Never edit, reorder or delete one.
    ///
    /// <para>The LAST schema is the live one: it decides which settings presets cover at all, and every
    /// code this build makes is written in it. The coverage test (TestSceneGameplayPresetCoverage)
    /// derives the same list from the controls actually in Settings &gt; Gameplay, and fails while the
    /// two differ, printing the schema to append. So adding, removing or re-ranging a Gameplay setting is:
    /// run the test, paste what it prints at the end of <see cref="ALL"/> with the next version number,
    /// and add a frozen code fixture for it next to version 1's.</para>
    ///
    /// <para>What an OLD code means after that is decided by <see cref="GameplayPresetManager"/>: a
    /// setting it carries that has since been removed is ignored, a setting added since takes its Minimal
    /// default, and a changed domain clamps or snaps (an enum member that no longer exists takes the
    /// default).</para>
    /// </summary>
    public static class GameplayPresetSchemas
    {
        /// <summary>The key prefix of a game (<see cref="OsuSetting"/>) setting. A ruleset's settings use its short name.</summary>
        public const string GAME_SOURCE = "game";

        public static readonly IReadOnlyList<PresetSchema> ALL = new[]
        {
            // Backlog 484: the Gameplay section after backlog 483's cleanup.
            new PresetSchema(1,
                "typebeat:SpaceSkipsWord bool",
                "typebeat:ManualNewlines bool",
                "typebeat:UseMapFonts bool",
                "typebeat:UseTypingFontInEditor bool",
                "typebeat:LyricFontSize range 24 72 1",
                "typebeat:LineSpacing range 40 200 1",
                "typebeat:CaretStyle enum Line Block Outline Underline",
                "typebeat:SungCaretStyle enum Line Block Outline Underline None",
                "typebeat:CaretSmoothing range 0 50 1",
                "typebeat:UseSpaceErrorDot bool",
                "typebeat:ApproachBars bool",
                "typebeat:ApproachBarAlignment range 0 100 1",
                "typebeat:JudgementIndicator enum None FadeIn BottomToTopFill",
                "typebeat:IndicatorAlignment range 0 100 1",
                "typebeat:SyllableFadeInDuration range 0 2000 1",
                "typebeat:SyllableBrightness range 0 100 1",
                "typebeat:TextPopIn bool",
                "typebeat:TextPopInAmount range 0 20 1",
                "typebeat:ShowSyllableMarkers bool",
                "typebeat:ShowBackingVocals bool",
                "typebeat:ShowPaceColours bool",
                "typebeat:PaceColourMode enum AccelerationBased MapRelative",
                "typebeat:PaceColourMaxChange range 25 150 1",
                "typebeat:PaceColourOpacityCurve range 0 100 1",
                "typebeat:PaceColourGradient range 0 100 1",
                "typebeat:ShowSyncMetric bool",
                "typebeat:PaceBarReveal enum Off Build ColourIn",
                "typebeat:JudgementColouredBars bool",
                "typebeat:LinePushWarningEdges bool",
                "typebeat:ShowTimingTint bool",
                "typebeat:TimingTintMinStrength range 0 100 1",
                "typebeat:TimingTintMaxStrength range 0 100 1",
                "typebeat:TimingTintEarlyColour colour",
                "typebeat:TimingTintLateColour colour",
                "game:DimLevel range 0 1 0.01",
                "game:BlurLevel range 0 1 0.01",
                "game:LightenDuringBreaks bool",
                "game:ShowStoryboard bool",
                "game:HUDVisibilityMode enum Never HideDuringGameplay Always",
                "game:ReplaySettingsOverlay bool",
                "game:GameplayLeaderboard bool",
                "game:AlwaysRequireHoldingForPause bool",
                "game:AlwaysShowHoldForMenuButton bool",
                "game:ShowHealthDisplayWhenCantFail bool"),
        };

        /// <summary>The live schema: the one every new code is written in.</summary>
        public static PresetSchema LATEST => ALL[^1];

        /// <summary>
        /// Settings in Settings &gt; Gameplay that presets deliberately leave alone: never encoded, and never
        /// written by applying a preset. The coverage test accepts a Gameplay control bound to one of these
        /// and nothing else that the latest schema lacks.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> EXCLUDED = new Dictionary<string, string>
        {
            // A font family name: an installed system font one machine has and the next may not, and a free
            // string, which has no finite domain to encode in.
            ["typebeat:LyricFont"] = "machine-specific font name",

            // Only exists on Windows (InputSettings is added there only), so a code could not mean the same
            // thing on every machine it is pasted on.
            [$"{GAME_SOURCE}:{nameof(OsuSetting.GameplayDisableWinKey)}"] = "Windows only",
        };

        public static PresetSchema? Find(int version) => ALL.FirstOrDefault(s => s.Version == version);
    }
}
