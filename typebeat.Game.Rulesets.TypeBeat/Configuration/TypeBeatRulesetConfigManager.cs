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

        // NOTE (backlog 107): there used to be an AllowWrongInput member here. Typing wrong
        // characters through is now the DEFAULT gameplay for everyone and strict rejection is a mod
        // (Gatekeeper), so the choice is no longer a setting. Removing a member is safe for exactly
        // the reason stated on CaretStyle above: rows are keyed by member NAME, so every other
        // setting keeps its row and the orphaned AllowWrongInput row is simply never looked up.

        /// <summary>
        /// Whether pressing space in the middle of a word abandons the rest of it (every character
        /// of it you had not resolved yet counts as a miss) and moves you on to the next word, rather
        /// than being rejected as a wrong key. OFF by default: it changes how a keypress is judged, so
        /// nobody gets it without asking for it. See <see cref="Gameplay.TypingEngine.SpaceSkipsWord"/>
        /// for what it costs and why it needs no multiplier.
        /// </summary>
        SpaceSkipsWord,

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

    /// <summary>
    /// Monkeytype's caret styles. <see cref="Line"/> is the classic 3px beam.
    ///
    /// <para><see cref="None"/> is the odd one out: it is not a caret shape at all, it is the
    /// choice to have NO sung playhead, so it is offered by the SUNG playhead dropdown only (see
    /// <see cref="TypeBeatRulesetSetting.SungCaretStyle"/>) and deliberately left out of the typing
    /// caret's dropdown, which builds its item list explicitly for that reason. The enum is shared
    /// because both heads are the same <c>Caret</c> class; the restriction lives in the UI.</para>
    ///
    /// <para>APPEND ONLY. Values are databased by member NAME, exactly as the lookup keys are (see
    /// <see cref="TypeBeatRulesetSetting.CaretStyle"/>), so renaming or reordering a member does not
    /// merely reset stored player choices, it can take the game down:
    /// <c>RulesetConfigManager.AddBindable</c> hands the stored string to <c>Bindable.Parse</c>,
    /// which routes an enum through <c>Enum.Parse</c> and THROWS <c>ArgumentException</c> on a name
    /// that no longer exists (measured against the framework build this repo pins, not assumed).
    /// Adding a member at the end is free.</para>
    ///
    /// <para>WHY <see cref="None"/> COULD STILL BE RENAMED from <c>Highlight</c> (backlog 177), the
    /// one exception to the paragraph above: the member was ADDED by backlog 175 earlier the same
    /// day and the client has not been shipped since, so no install anywhere can hold a
    /// <c>RealmRulesetSetting</c> row reading "Highlight" except a developer's own working install,
    /// and only then if they opened the dropdown and picked it (everyone else's row was written from
    /// <see cref="TypeBeatRulesetConfigManager.DEFAULT_SUNG_CARET_STYLE"/> and says "Line"). Keeping
    /// the old name as an alias member was NOT the safer option it looks like: two members sharing a
    /// value makes <c>Enum.GetValues</c> return both, and <c>OsuEnumDropdown</c> feeds exactly that
    /// into <c>Dropdown.Items</c>, which throws on a duplicate, so the alias would break the settings
    /// panel for everybody in order to protect one machine. The residual risk is covered at the READ
    /// instead: a stored value that no longer parses now falls back to the default rather than
    /// throwing (see <c>RulesetConfigManager.AddBindable</c>), which also retires this landmine for
    /// any future rename.</para>
    /// </summary>
    public enum CaretStyle
    {
        Line,
        Block,
        Outline,
        Underline,

        /// <summary>
        /// Sung playhead only: NO playhead at all. The sung caret is hidden and the underline sweep
        /// is never fed, so where the song is up to is carried by the lit syllable group alone.
        ///
        /// <para>This member selects nothing but that absence. The lit group is NOT part of it:
        /// since backlog 177 the group the vocals are on lifts its untyped cells to a lighter grey
        /// under EVERY style, alongside the caret and the sweep, so picking this one subtracts the playhead and
        /// adds nothing. It was called <c>Highlight</c> while the two were one presentation.</para>
        ///
        /// <para>Independent of <see cref="Gameplay.TypingEngine.SyllableTiming"/>, which is a
        /// JUDGEMENT rule: <see cref="Gameplay.TypingLine.Syllables"/> is built for every line
        /// whatever the engine is judging on, so the lit group renders identically under classic
        /// judgement. Keeping the two apart is what stops the highlight from silently doing nothing
        /// in a Release build.</para>
        /// </summary>
        None
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
            SetDefault(TypeBeatRulesetSetting.SpaceSkipsWord, false);
            SetDefault(TypeBeatRulesetSetting.LineSpacing, 96.0f, 40.0f, 200.0f, 1.0f);
            SetDefault(TypeBeatRulesetSetting.LyricFont, LYRIC_FONT_DEFAULT);
        }
    }
}
