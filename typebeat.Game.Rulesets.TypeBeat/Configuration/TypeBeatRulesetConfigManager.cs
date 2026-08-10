// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Configuration;
using typebeat.Game.Rulesets.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.Configuration
{
    public enum TypeBeatRulesetSetting
    {
        /// <summary>
        /// Positive = lyrics later relative to the music, negative = earlier. Applied at the
        /// playfield's single engine-feed seam (the lyric-offset clock the engine, stage, HUD
        /// extras and key handler all read). Settings UI surfacing is deferred to M7.
        /// </summary>
        LyricOffsetMs,

        /// <summary>
        /// Optional explicit path to the vendored lyriclab aligner directory (the folder holding
        /// align_lyrics.py). Empty = auto-discover by walking up from the game's runtime location.
        /// Read by the import pipeline's directory resolution; UI surfacing is deferred to M7.
        /// </summary>
        LyricLabPath,

        /// <summary>
        /// Whether the locally installed lyriclab auto-aligner is used for imports. On by default:
        /// when an installed environment exists the local aligner is preferred over the server one;
        /// when off (or nothing is installed) imports use the server aligner / LRC fallback. This
        /// only decides which aligner runs; it never triggers the multi-GB install, which stays an
        /// explicit action (the first-run prompt and the Settings button).
        /// </summary>
        LocalAlignerEnabled,

        /// <summary>
        /// Rendering style (monkeytype's caret options) of the PLAYER's typing caret only. The sung
        /// playhead has its own <see cref="SungCaretStyle"/>, so the two heads can be shaped apart.
        ///
        /// <para>DO NOT RENAME THIS MEMBER, however lopsided the pair looks next to
        /// <see cref="SungCaretStyle"/>. Rows in <c>RealmRulesetSetting</c> are keyed by the enum
        /// member's NAME (<c>RulesetConfigManager.AddBindable</c> matches <c>s.Key == lookup.ToString()</c>,
        /// and <c>PerformSave</c> writes the same), not by its ordinal. Renaming it would leave every
        /// existing player's stored row orphaned under the old key and silently reset their caret to
        /// the default. Adding members anywhere in this enum is safe for the same reason: position
        /// carries no meaning.</para>
        /// </summary>
        CaretStyle,

        /// <summary>
        /// Rendering style of the SUNG playhead: the second head on the lyric line, which tracks the
        /// vocals rather than the player. Independent of the typing caret's <see cref="CaretStyle"/>,
        /// so a player can shape the two apart (they are already told apart by colour, damping and
        /// blink). Defaults to <see cref="TypeBeatRulesetConfigManager.DEFAULT_SUNG_CARET_STYLE"/>.
        /// </summary>
        SungCaretStyle,

        /// <summary>
        /// Physical keyboard layout the player types on. Keys arrive by physical position, so a
        /// non-QWERTY layout needs the produced character remapped (see <see cref="KeyboardLayout"/>).
        /// </summary>
        KeyboardLayout,

        /// <summary>
        /// Legacy typing model. Off (default) = strict: a wrong key is rejected and 13 in a row fail
        /// the play. On = a wrong character is typed through (shown red) and can be backspaced; only
        /// the space key stays strict, and the mash-fail streak does not apply.
        ///
        /// <para>Doubles as the BACKSPACE gate (see <c>TypeBeatPlayfield</c>'s key handler): strict
        /// play never writes an erasable char, so backspace is ignored outright while this is off.</para>
        /// </summary>
        AllowWrongInput,

        /// <summary>Vertical gap (px) between the three gameplay lyric lines.</summary>
        LineSpacing,

        /// <summary>
        /// Family name of the font used for the gameplay typing surface (the lyric stack and typed
        /// characters). <see cref="TypeBeatRulesetConfigManager.LYRIC_FONT_DEFAULT"/> keeps the game's
        /// built-in font; <c>"OpenDyslexic"</c> selects the bundled accessibility face; any other value
        /// is treated as an installed system-font family. Unknown or failed fonts fall back to the
        /// default. Only the typing surface is affected; the rest of the UI keeps its default fonts.
        /// </summary>
        LyricFont
    }

    /// <summary>Monkeytype's caret styles. <see cref="Line"/> is the classic 3px beam.</summary>
    public enum CaretStyle
    {
        Line,
        Block,
        Outline,
        Underline
    }

    public class TypeBeatRulesetConfigManager : RulesetConfigManager<TypeBeatRulesetSetting>
    {
        /// <summary>Sentinel <see cref="TypeBeatRulesetSetting.LyricFont"/> value meaning "keep the game's built-in font".</summary>
        public const string LYRIC_FONT_DEFAULT = "Default";

        /// <summary>
        /// The caret style a NEW install starts on. Changing this cannot disturb an existing player, and the reason is
        /// worth stating because the usual assumption about defaults is the opposite one: a default normally leaks to
        /// everyone who never touched the setting.
        ///
        /// <para>
        /// It does not here because <see cref="Rulesets.Configuration.RulesetConfigManager{TLookup}.AddBindable"/>
        /// databases EVERY setting the first time the config is constructed, not just the ones a player later changes.
        /// So anyone who has already launched the game owns an explicit <c>RealmRulesetSetting</c> row holding
        /// whatever they were given at the time, and <c>PerformLoad</c> parses that row back over this default on every
        /// subsequent boot. A player who has never opened the caret dropdown is therefore just as pinned as one who
        /// has. Only an install with no row yet, which is to say a new player, reads this value at all.
        /// </para>
        /// </summary>
        public const CaretStyle DEFAULT_CARET_STYLE = CaretStyle.Underline;

        /// <summary>
        /// The sung playhead's style on a NEW key, which is to say on EVERY install, existing players included.
        /// Read the reasoning in <see cref="DEFAULT_CARET_STYLE"/> and then note that it does NOT transfer here:
        /// that argument turns on every install already owning a databased row for that key, and
        /// <see cref="TypeBeatRulesetSetting.SungCaretStyle"/> is a brand-new key, so no install anywhere has a
        /// row for it. Every player therefore reads this value once, and the row that gets written from it pins
        /// them to whatever it said at that moment. Changing this constant later moves nobody who has already
        /// booted, but changing it BEFORE the next ship moves everybody.
        ///
        /// <para>
        /// <see cref="CaretStyle.Line"/> is chosen so that shipping the split costs zero visual change. The
        /// client has not been reshipped since the playhead first started following the caret-style setting, so
        /// no player has ever seen a non-Line playhead; Line is also what <c>Caret.Style</c>'s own field
        /// initialiser holds, which makes the playhead byte-identical to its long-standing behaviour for anyone
        /// who never opens the dropdown. It also sidesteps the one bad pairing: <see cref="CaretStyle.Underline"/>
        /// (the typing caret's default) draws a 3px bar under the playhead roughly 6px above the 3px sung sweep
        /// rail <c>LyricLineDisplay.SetSungPosition</c> already draws in the same accent colour, so an
        /// Underline playhead reads as a double bar. The new shapes stay available, just opt-in.
        /// </para>
        /// </summary>
        public const CaretStyle DEFAULT_SUNG_CARET_STYLE = CaretStyle.Line;

        public TypeBeatRulesetConfigManager(SettingsStore? settings, RulesetInfo ruleset, int? variant = null)
            : base(settings, ruleset, variant)
        {
        }

        protected override void InitialiseDefaults()
        {
            base.InitialiseDefaults();

            SetDefault(TypeBeatRulesetSetting.LyricOffsetMs, 0.0, -500.0, 500.0, 1.0);
            SetDefault(TypeBeatRulesetSetting.LyricLabPath, string.Empty);
            SetDefault(TypeBeatRulesetSetting.LocalAlignerEnabled, true);
            SetDefault(TypeBeatRulesetSetting.CaretStyle, DEFAULT_CARET_STYLE);
            SetDefault(TypeBeatRulesetSetting.SungCaretStyle, DEFAULT_SUNG_CARET_STYLE);
            SetDefault(TypeBeatRulesetSetting.KeyboardLayout, Gameplay.KeyboardLayout.Qwerty);
            SetDefault(TypeBeatRulesetSetting.AllowWrongInput, false);
            SetDefault(TypeBeatRulesetSetting.LineSpacing, 96.0f, 40.0f, 200.0f, 1.0f);
            SetDefault(TypeBeatRulesetSetting.LyricFont, LYRIC_FONT_DEFAULT);
        }
    }
}
