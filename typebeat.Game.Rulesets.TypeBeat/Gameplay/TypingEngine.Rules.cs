// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// TypingEngine, the RULE SWITCHES: every era and mod flag a run is judged under. Each is set
// before the first keypress (by the replay CONFIG frame, the score's mod list or the live engine
// factory) and left alone afterwards. The code that reads a switch lives with the concern it
// decides; only the switch and its essay live here.

using System;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    public sealed partial class TypingEngine
    {
        /// <summary>
        /// Whether correcting a wrong cell resumes the streak its keypress broke (see
        /// <see cref="ComboRestored"/>). <see cref="ComboRestoreRule.OnFix"/> is the live rule
        /// (backlog 140) and the default; only <see cref="Scoring.TypeBeatReplayScorer"/> ever sets
        /// the other one, to re-derive a score from before the restore existed. It must be set
        /// BEFORE the first keypress and left alone afterwards: combo already awarded is never
        /// revisited.
        /// </summary>
        public ComboRestoreRule ComboRestore { get; set; } = ComboRestoreRule.OnFix;

        /// <summary>
        /// Which break owns the streak when a redeemable one lands on top of an outstanding claim
        /// (see <see cref="ComboRestored"/>). <see cref="ComboClaimRule.StreakedBreakWins"/> is the
        /// live rule (backlog 176) and the default: a break takes ownership only if it HAS a streak
        /// to own. Only <see cref="Scoring.TypeBeatReplayScorer"/> ever sets the other one, to
        /// re-derive a score from before that was true. Set BEFORE the first keypress and left alone
        /// afterwards, exactly like <see cref="ComboRestore"/>, whose companion it is: it decides
        /// nothing at all under <see cref="ComboRestoreRule.Never"/>, where no snapshot is ever
        /// taken.
        /// </summary>
        public ComboClaimRule ComboClaim { get; set; } = ComboClaimRule.StreakedBreakWins;

        /// <summary>
        /// Whether combo credited by the press that TOOK the outstanding claim counts as a streak the
        /// next break may take that claim with (see <see cref="ComboRestored"/>).
        /// <see cref="SkipSpaceCreditRule.NotAStreakOfItsOwn"/> is the live rule (backlog 243) and the
        /// default: a skipping space rebuilds the run to 1 on the gap it lands on, and that 1 is the
        /// break's own press rather than anything the player typed after it. Only
        /// <see cref="Scoring.TypeBeatReplayScorer"/> ever sets the other one, to re-derive a score
        /// from before that was true. Set BEFORE the first keypress and left alone afterwards, exactly
        /// like <see cref="ComboRestore"/> and <see cref="ComboClaim"/>, whose refinement it is: it
        /// decides nothing under <see cref="ComboClaimRule.LatestBreakWins"/>, where no break is
        /// passive, nor under <see cref="ComboRestoreRule.Never"/>, where no snapshot exists.
        /// </summary>
        public SkipSpaceCreditRule SkipSpaceCredit { get; set; } = SkipSpaceCreditRule.NotAStreakOfItsOwn;

        /// <summary>
        /// Whether a word abandoned by <see cref="SpaceSkipsWord"/> stays re-typeable (see
        /// <see cref="WordAbandoned"/>). <see cref="WordSkipRule.Reclaimable"/> is the live rule
        /// (backlog 167) and the default; only <see cref="Scoring.TypeBeatReplayScorer"/> ever sets
        /// the other one, to re-derive a score from before the reclaim existed. Set BEFORE the first
        /// keypress and left alone afterwards, exactly like <see cref="ComboRestore"/>: cells already
        /// given up are never revisited.
        /// </summary>
        public WordSkipRule WordSkip { get; set; } = WordSkipRule.Reclaimable;

        /// <summary>
        /// What an OFF-TIME press costs: the right character struck outside the outermost Meh
        /// window, judged <see cref="JudgementType.Premature"/> or <see cref="JudgementType.Lagging"/>.
        /// <see cref="OffTimeRule.MehHit"/> is the live rule (backlog 199) and the default: the press
        /// earns no points but EXTENDS the run, and only accuracy pays. Only
        /// <see cref="Scoring.TypeBeatReplayScorer"/> ever sets the other one, to re-derive a score
        /// from when such a press broke the combo. Set BEFORE the first keypress and left alone
        /// afterwards, exactly like <see cref="ComboRestore"/>: combo already awarded is never
        /// revisited.
        /// </summary>
        public OffTimeRule OffTime { get; set; } = OffTimeRule.MehHit;

        /// <summary>
        /// What a CORRECTED typo's cell is worth: the judgement a cell earns from a correct retype,
        /// having held a wrong character before it was ever judged.
        /// <see cref="CorrectionCreditRule.Capped"/> is the live rule (backlog 210) and the default:
        /// such a cell resolves at min(the retype's own tier, <see cref="JudgementType.Ok"/>), so a
        /// fix always costs some accuracy and perfect play strictly beats corrected play per cell.
        /// Only <see cref="Scoring.TypeBeatReplayScorer"/> ever sets the other one, to re-derive a
        /// score from when a fast fix was free. Set BEFORE the first keypress and left alone
        /// afterwards, exactly like <see cref="ComboRestore"/>: judgements already awarded are never
        /// revisited.
        /// </summary>
        public CorrectionCreditRule CorrectionCredit { get; set; } = CorrectionCreditRule.Capped;

        /// <summary>
        /// THE live judgement rule (backlog 174, graduated by backlog 179, narrowed by backlog 180
        /// to every mod stack except Hard Rock): judge each keypress
        /// against its cell's SYLLABLE time span instead of the cell's point target. Characters
        /// belong to a syllable
        /// (<see cref="TypingLine.Syllables"/>), and any character of a syllable is perfectly timed
        /// while that syllable is being sung: the judged delta is 0 anywhere inside
        /// [<see cref="SyllableGroup.StartTime"/>, <see cref="SyllableGroup.EndTime"/>]
        /// (edge-inclusive) and the signed distance to the nearer edge outside it, fed through the
        /// same <see cref="SyncWindows.Classify"/> ladder and stored in
        /// <see cref="TypingCell.JudgedDelta"/> like any point delta, so points, combo, the sync
        /// readouts and the results screen all work unmodified. A cell in no group keeps the
        /// classic point delta: space cells, any line with no groups, and since backlog 178 every
        /// cell of a token that is not a syllabifiable English word ("wooooooords", "ohhh"), which
        /// is how a stylised spelling keeps the per-character rule it is actually timed on.
        ///
        /// <para>FALSE by default, and era-styled like <see cref="SpaceTiming"/>: set before the
        /// first keypress and left alone afterwards, judgements already made are never revisited.
        /// Live play turns it on for every mod stack but Hard Rock
        /// (<c>DrawableTypeBeatRuleset.createEngine</c>), which reverts to the classic rule because
        /// a span that grants delta 0 over hundreds of milliseconds undercuts HR's halved windows
        /// (see <see cref="Mods.TypeBeatModHardRock"/>). The default is the CLASSIC era, which is
        /// what a bare engine, every replay recorded before backlog 179, and every HR replay must
        /// judge under. Which one a re-derivation gets is decided by the replay's
        /// own CONFIG frame (<see cref="Replays.TypeBeatReplayFrame.SyllableTiming"/>, flags bit 2)
        /// and applied in <see cref="Replays.ReplayEngineFeed.Apply"/>, so a stored score always
        /// reproduces the rule its fingers were graded on.</para>
        /// </summary>
        public bool SyllableTiming { get; set; }

        /// <summary>
        /// The EASY mod's shelter: a cell is judged against the span of its whole WORD
        /// (<see cref="TypingLine.Words"/>) instead of the span of its syllable
        /// (<see cref="TypingLine.Syllables"/>). 0 anywhere inside the word, the signed distance to
        /// the nearer edge outside it, through the same <see cref="SyncWindows.Classify"/> ladder --
        /// the same bargain <see cref="SyllableTiming"/> strikes one level down, over a unit that
        /// contains one or more syllables. On a word with no subdivisions the two readings agree,
        /// which is why this is a widening and never a rule change of its own: what it buys is the
        /// freedom to be late on one syllable because the word is still being sung.
        ///
        /// <para>It is the Easy arm and NOT an era. <see cref="Mods.TypeBeatModEasy"/> is the only
        /// thing that sets it (its <c>ApplyToEngine</c>, which both the live factory and
        /// <c>TypeBeatReplayScorer</c> call, and
        /// <see cref="Mods.TypeBeatModAutoplay"/> passes it to the generator), and the mod ships in
        /// this release, so no stored row predates it and no CONFIG bit records it. Hard Rock does
        /// not interact with it: HR turns <see cref="SyllableTiming"/> off outright, and the two
        /// mods are mutually exclusive on a stack anyway.</para>
        ///
        /// <para>A cell in no word keeps the classic point delta under either reading (the
        /// inter-word SPACE cell, whose own target is the word boundary it types). The two
        /// narrowings still narrow it: <see cref="CharTimedStretch"/> reverts a stretch cell to its
        /// own point target, and <see cref="FirstCharTiming"/> anchors the WORD's first cell to the
        /// word's start rather than a syllable's, so under Easy the word opening is on the clock and
        /// the rest of the word is paid 0.</para>
        /// </summary>
        public bool WordShelter { get; set; }

        /// <summary>
        /// The narrowing backlog 209 puts on <see cref="SyllableTiming"/>: a STRETCH cell
        /// (<see cref="TypingLine.IsCharTimedStretch"/>, a freestyle slot or a cell of a run of
        /// three or more identical characters inside one syllable) reverts to its own point target
        /// even while the rest of the line is judged on syllable spans.
        ///
        /// <para>The span rule prices a press on WHICH CHARACTER of the syllable is being sung, and
        /// those two shapes carry no such information: every key satisfies a freestyle slot, and the
        /// characters of "&amp;&amp;&amp;&amp;&amp;&amp;" or the "000" of "1000" are interchangeable
        /// to the matcher. So a player could mash the whole run the instant its syllable opened,
        /// seconds ahead of the vocal, and be graded a delta of ZERO on every press: a field report
        /// had accuracy going UP for spamming a freestyle section. Reverting exactly those cells to
        /// the character targets they are actually timed on (the equal division of the syllable's
        /// time range that <c>TypingLine.syllableCharTarget</c> already assigns) puts the mash back
        /// on the clock and leaves every ordinary character on the span rule it was given.</para>
        ///
        /// <para>FALSE by default, and era-styled exactly like the flags above: set before the first
        /// keypress and left alone. Live play sets it for EVERY mod stack
        /// (<c>DrawableTypeBeatRuleset.createEngine</c>), Hard Rock included, where it is inert
        /// because HR turns <see cref="SyllableTiming"/> off and is therefore already
        /// point-timed; recording it unconditionally is what makes re-derivation uniform. It
        /// travels per replay on the CONFIG frame's flags bit 6
        /// (<see cref="Replays.TypeBeatReplayFrame.CharTimedStretch"/>) and is applied in
        /// <see cref="Replays.ReplayEngineFeed.Apply"/>, so every replay recorded before it existed
        /// carries the bit clear and re-derives on the pure span rule its fingers were graded on,
        /// exploit and all.</para>
        /// </summary>
        public bool CharTimedStretch { get; set; }

        /// <summary>
        /// The second narrowing on <see cref="SyllableTiming"/> (backlog 247): the FIRST cell of a
        /// syllable group is judged on its distance from the group's <see cref="SyllableGroup.StartTime"/>
        /// rather than paid 0 anywhere inside the span, so pacing a syllable out beats bursting its
        /// characters just before the window closes. Every other cell of the group keeps the span
        /// rule, which is what makes this a hybrid: the syllable's opening is back on the clock and
        /// the rest of the syllable stays as forgiving as backlog 179 made it.
        ///
        /// <para>The anchor is the SPAN START, deliberately not the cell's own
        /// <see cref="TypingCell.TargetTime"/>: the playhead reaches a syllable's first character
        /// when the syllable starts being sung, and under mapper subtimings the flat-ramp target
        /// routinely sits OUTSIDE its own group's span (see
        /// <see cref="Replays.TypeBeatAutoGenerator"/>, whose 99.23% autoplay incident came from
        /// exactly that gap). The EARLY side is unchanged either way, since a press before
        /// <see cref="SyllableGroup.StartTime"/> already judges on that same distance; only the late
        /// side tightens. A stretch cell (<see cref="CharTimedStretch"/>) that opens a group is NOT
        /// re-anchored: it is already point-judged, and stricter, so the stretch narrowing keeps
        /// precedence.</para>
        ///
        /// <para>FALSE by default, and era-styled exactly like the flags above: set before the first
        /// keypress and left alone. Live play sets it for EVERY mod stack
        /// (<c>DrawableTypeBeatRuleset.createEngine</c>), Hard Rock included, where it is inert
        /// because HR turns <see cref="SyllableTiming"/> off; recording it unconditionally is what
        /// keeps re-derivation uniform. It travels per replay on the CONFIG frame's flags bit 8
        /// (<see cref="Replays.TypeBeatReplayFrame.FirstCharTiming"/>) and is applied in
        /// <see cref="Replays.ReplayEngineFeed.Apply"/>, so every replay recorded before it existed
        /// carries the bit clear and re-derives with the whole span paying its first character 0,
        /// exactly as its player was scored.</para>
        /// </summary>
        public bool FirstCharTiming { get; set; }

        /// <summary>
        /// THE SEAL'S COMBO BREAK IS BACK-DATED to the cells it is about to miss (backlog 259). A
        /// line's misses only exist at its SEAL, which under the unpinned caret can land a second and
        /// a half after the song left the line and long after the player has moved on and started
        /// rebuilding. The break was landing on the run they hold NOW, wiping combo earned on cells
        /// the missed ones sit nowhere near. With this set the break destroys only what was earned AT
        /// OR BEFORE the line's LAST unforeseen missed cell in (line, cell) order; every increment
        /// earned strictly past that position (a later cell of the same line, or any cell of a later
        /// line) SURVIVES, and <see cref="Combo"/> is left at exactly that surviving count instead of
        /// at zero.
        ///
        /// <para>The player's report is the sharpest case: a typo backspaced away and left empty
        /// takes its break at the wrong KEYPRESS, and the empty cell it leaves behind is a miss, so
        /// the seal took a SECOND break for the same fumble, on a run the player had rebuilt in the
        /// meantime. Back-dated, that seal destroys nothing at all (everything at or before the cell
        /// was already gone), which is exactly what "my combo already broke from those misses" asks
        /// for.</para>
        ///
        /// <para>WHAT IT DOES NOT MOVE. Nothing already judged is re-priced: a keypress made before
        /// the seal keeps the points and the <c>ComboAtJudgement</c> it was awarded at (the same rule
        /// <see cref="ComboRestored"/> follows), and <see cref="MaxCombo"/> is never reduced, since it
        /// records a run the player really did hold. HEALTH is untouched: the misses, their drain and
        /// their timing are all exactly as they were, and only the combo the break takes moves. An
        /// ABANDONED cell still breaks nothing here (its break was taken at the skip) and a seal with
        /// no unforeseen miss is still a no-op, both exactly as before.</para>
        ///
        /// <para>The one outstanding <see cref="ComboRestored"/> claim is still discarded outright by
        /// a seal that breaks, back-dated or not, and deliberately: the streak a claim holds was
        /// earned EARLIER than the run this break cuts back, so redeeming it afterwards could only
        /// put back combo the break was entitled to take. Keeping it would mean back-dating the
        /// claim's own streak as well, which buys the player at most one already-broken run and costs
        /// a second ledger.</para>
        ///
        /// <para>FALSE by default, and era-styled exactly like the flags above: set before the first
        /// keypress and left alone afterwards. Live play sets it for EVERY mod stack
        /// (<c>DrawableTypeBeatRuleset.createEngine</c>), because it is not a window, an input model
        /// or a caret, and no mod has an opinion about when a break lands. It travels per replay on
        /// the CONFIG frame's flags bit 10
        /// (<see cref="Replays.TypeBeatReplayFrame.BackDatedSealBreak"/>) and is applied in
        /// <see cref="Replays.ReplayEngineFeed.Apply"/>, so every replay recorded before it existed
        /// carries the bit clear and re-derives with the seal wiping the whole run, which is the
        /// <c>max_combo</c> and the <c>total_score</c> its player was given.</para>
        ///
        /// <para>It can only ever differ from the wipe where the player was ALLOWED to be past the
        /// missed cells before the line sealed: the unpinned caret (backlog 208/218, the live
        /// default) puts them on the next line while the old one is still running out its drag grace,
        /// and <see cref="AnyOrderWithinWord"/> lets them get past an untyped cell inside one word.
        /// With a pinned caret and no Dyslexia there is no such increment to save, so the two arms
        /// agree cell for cell.</para>
        /// </summary>
        public bool BackDatedSealBreak { get; set; }

        /// <summary>
        /// LOSSLESS SKIP RECLAIM (backlog 260): one law, in two places, saying that a word given up by
        /// accident and then typed out in full costs the run NOTHING. A player reported finishing a
        /// map with 0 misses, every one of its 920 cells typed, and a max combo of 919: the increment
        /// missing was the WORD GAP the skipping space was judged on, and there are two ways that one
        /// increment was being dropped.
        ///
        /// <para><b>The rush cap charged the space for the word it abandoned.</b>
        /// <see cref="skipCurrentWord"/> moves the caret past the whole word BEFORE the same press is
        /// judged on the gap it parked on, and <see cref="rushesPastCap"/> measures the caret
        /// POSITIONALLY, so the abandoned tail counted against a budget the player never spent: with
        /// the tail plus the lead over <see cref="FLETCHER_MAX_CHARS_AHEAD"/> the gap earned no combo
        /// at all, and silently, since the skip's own break two statements earlier had already zeroed
        /// the run (no <see cref="ComboBroken"/>, no claim discarded). The gap is then written Correct
        /// with its <see cref="TypingCell.FirstCorrectDelta"/>, so every later retype of it is inert
        /// and the increment can never be earned back. Under this rule the skipping space is measured
        /// against the caret as it stood BEFORE the skip moved it, which is what makes
        /// <see cref="rushesPastCap"/>'s own "a space spends no budget" true of the one space that
        /// could spend a whole word of it. An ordinary press is untouched: it is measured where it
        /// always was.</para>
        ///
        /// <para><b>The passive claim arm dropped the run it stood on.</b> A break that takes no more
        /// than the claim's own credit is passive (backlog 243) and keeps the held claim, but it had
        /// already called <see cref="breakRun"/> at its call site, so the increments that run held
        /// were discarded with nothing to redeem them. Under this rule the passive break FOLDS its
        /// spent run into the claim it left standing (streak and positions both, in run order), so a
        /// full correction restores the whole of it. Reached by a double space, and by any typo
        /// landing on the gap a skip just took.</para>
        ///
        /// <para>FALSE by default, and era-styled exactly like <see cref="BackDatedSealBreak"/>: set
        /// before the first keypress and left alone afterwards. Live play sets it for EVERY mod stack
        /// (<c>DrawableTypeBeatRuleset.createEngine</c>), because no mod has an opinion about what a
        /// skip costs. It travels per replay on the CONFIG frame's flags bit 11
        /// (<see cref="Replays.TypeBeatReplayFrame.LosslessSkipReclaim"/>) and is applied in
        /// <see cref="Replays.ReplayEngineFeed.Apply"/>, so every replay recorded before it existed
        /// carries the bit clear and re-derives with the increment still dropped, which is the
        /// <c>max_combo</c> and the <c>total_score</c> its player was given.</para>
        ///
        /// <para>Judgement relevant in the same narrow sense bit 10 is: it moves no delta, no tier,
        /// no cell state and no keystroke's landing place. It moves only COMBO, and therefore the
        /// combo weight of every judgement after the skip.</para>
        /// </summary>
        public bool LosslessSkipReclaim { get; set; }

        /// <summary>
        /// DISPLACED CLAIM FOLD (backlog 262): a break that takes the claim off an older break FOLDS
        /// that claim into its own instead of discarding it, so the chain is redeemed by coming back
        /// to the NEWEST of the cells rather than lost the moment a second accident happens.
        ///
        /// <para>A player 477 combo deep typo'd the first letter of a word, typed the second letter
        /// correctly (which rebuilt the run to 1), then typo'd the word gap after it. That second
        /// break stood on a streak of 1 it had really earned, so it was not passive (backlog 243) and
        /// took the claim, and the overwrite arm of <see cref="snapshotRedeemableBreak"/> threw the
        /// 477 away. Three backspaces and a perfect retype then restored 1. The run was ended by two
        /// accidents that were both fully corrected, which the repo's law (backlogs 243 and 260) says
        /// costs nothing: the player finished with 0 misses, 100% completion and a max combo of 477
        /// out of 894.</para>
        ///
        /// <para>Under this rule the displacing break's claim is <c>displacedStreak + brokenStreak</c>
        /// against its own cell, with the displaced claim's positions in front of its own (run order,
        /// oldest first, because <see cref="resumeStreakIfThisRedeemsTheBreak"/> puts them back at the
        /// HEAD of the ledger). <c>positions.Count == streak</c> is preserved, and the new claim's
        /// <c>ownPressCredit</c> starts at 0 exactly as it did before, so backlog 243's one-break
        /// exemption is neither granted nor extended by folding. Chains transitively: a third break
        /// folds the pair, and so on.</para>
        ///
        /// <para>What keeps it honest is backlog 259's <see cref="BackDatedSealBreak"/>. The fold
        /// restores increments earned before the older break without that break's own cell having
        /// been fixed, but the restored positions go back WHERE THEY WERE EARNED, so a line sealing
        /// on cells nobody typed back-dates its break against them and destroys every increment at or
        /// before its last unforeseen miss, folded ones included.</para>
        ///
        /// <para>FALSE by default, and era-styled exactly like <see cref="LosslessSkipReclaim"/>: set
        /// before the first keypress and left alone afterwards. Live play sets it for EVERY mod stack
        /// (<c>DrawableTypeBeatRuleset.createEngine</c>), because no mod has an opinion about what a
        /// second accident costs. It travels per replay on the CONFIG frame's flags bit 12
        /// (<see cref="Replays.TypeBeatReplayFrame.FoldsDisplacedClaim"/>) and is applied in
        /// <see cref="Replays.ReplayEngineFeed.Apply"/>, so every replay recorded before it existed
        /// carries the bit clear and re-derives with the displaced claim discarded, which is the
        /// <c>max_combo</c> and the <c>total_score</c> its player was given.</para>
        ///
        /// <para>Judgement relevant in the same narrow sense bits 10 and 11 are: it moves no delta,
        /// no tier, no cell state and no keystroke's landing place. It moves only COMBO, and
        /// therefore the combo weight of every judgement after the redemption.</para>
        /// </summary>
        public bool FoldsDisplacedClaim { get; set; }

        /// <summary>
        /// DISPLACED CLAIMS STACK (backlog 422), a refinement of <see cref="FoldsDisplacedClaim"/> and
        /// read only alongside it: correcting a broken cell refunds the run THAT break broke, and
        /// nothing owed to another break unless that break's cell is corrected too.
        ///
        /// <para>The fold credited the displaced streak whether or not its cell was ever fixed. A
        /// player who typo'd a cell on one line, left it wrong, moved on and typo'd a cell on the next
        /// line got the combo back to its value before the FIRST typo by correcting only the second:
        /// a cell left wrong is not a miss at seal (backlog 124), so the seal's back-dated break
        /// (<see cref="BackDatedSealBreak"/>) had nothing to claw back.</para>
        ///
        /// <para>Under this rule the displaced claim is kept, separately, below the new one (a stack,
        /// oldest first). Correcting the cell on top refunds its own streak and brings the next one
        /// down back up; correcting a cell further down joins its streak to the next newer claim,
        /// because the run after it is still broken by that newer claim. So two accidents both fully
        /// corrected still cost nothing, in either order, which keeps backlog 262's report whole, and
        /// one left wrong costs exactly the run it broke. A sealed line's claims, and every claim
        /// older than the newest of them, are dropped at the seal (<see cref="ComboLedger.DropClaimOn"/>):
        /// their cells can no longer be corrected. Positions travel with their streaks throughout, so
        /// <c>positions.Count == streak</c> holds for every claim.</para>
        ///
        /// <para>FALSE by default, and era-styled exactly like <see cref="FoldsDisplacedClaim"/>. Live
        /// play sets it for EVERY mod stack (<c>DrawableTypeBeatRuleset.createEngine</c>), because no
        /// mod has an opinion about what an uncorrected typo costs. It travels per replay on bit 14
        /// (value 16384) of the SECOND CONFIG flags word
        /// (<see cref="Replays.TypeBeatReplayFrame.StacksDisplacedClaims"/>), so every replay recorded
        /// before it carries the bit clear and re-derives with the fold, which is the
        /// <c>max_combo</c> and the <c>total_score</c> its player was given. It moves only COMBO.</para>
        /// </summary>
        public bool StacksDisplacedClaims { get; set; }

        /// <summary>
        /// Whether <see cref="AllowWrongInput"/> reaches the WORD GAP as well as the lyric
        /// characters (backlog 181). With it on, a wrong (non-space) key pressed while the caret
        /// sits on a space cell is typed THROUGH exactly like a wrong letter on a lyric cell: the
        /// gap takes the typo character, shows it in the error red instead of an invisible red space
        /// (see <c>LyricLineDisplay.CellGlyph</c>), the caret advances, backspace erases it, and
        /// retyping the space earns the cell's real judgement plus any streak the typo broke. With
        /// it off, that same press is REJECTED by the gatekeeper branch, which is the strict outcome
        /// the word gap has always had.
        ///
        /// <para>It moves the CELL side only. The space KEY stays strict under both arms: no wrong
        /// space is ever typed into any cell, because the spacebar is the word-advance key and not a
        /// glyph a player means to leave in a lyric (backlog 50, and <see cref="SpaceSkipsWord"/>'s
        /// interception of a space on a lyric character is untouched). It is also inert under
        /// <see cref="AllowWrongInput"/> = false, being an extension of that flag and not a
        /// competitor to it: Gatekeeper rejects every wrong key, gap or no gap.</para>
        ///
        /// <para>FALSE by default, and era-styled exactly like <see cref="SyllableTiming"/>: set
        /// before the first keypress and left alone afterwards, judgements already made are never
        /// revisited. Live play turns it on UNCONDITIONALLY, Hard Rock included
        /// (<c>DrawableTypeBeatRuleset.createEngine</c>): HR halves the judgement windows, and this
        /// is not a window, it is the input model, which HR does not touch (its runs already type
        /// wrong LETTERS through). The default is the CLASSIC era, which every replay recorded
        /// before backlog 181 must re-derive under: those runs contain wrong-key-on-gap frames that
        /// were REJECTED at record time, and typing them through would move the caret, the cells and
        /// the whole account. Which arm a re-derivation gets is decided by the replay's own CONFIG
        /// frame (<see cref="Replays.TypeBeatReplayFrame.WrongInputOnWordGaps"/>, flags bit 3) and
        /// applied in <see cref="Replays.ReplayEngineFeed.Apply"/>, so a stored score always
        /// reproduces the model its fingers were graded on.</para>
        /// </summary>
        public bool WrongInputOnWordGaps { get; set; }

        /// <summary>
        /// MONKEYTYPE SPACE DISCIPLINE (backlog 184): the spacebar is the WORD BOUNDARY, a key the
        /// player owes at every gap rather than one the caret glides over. Two rules, and
        /// <see cref="SpaceSkipsWord"/> decides which of them a run gets, because each fixes what that
        /// setting's own arm did with a misplaced space. Not a user setting: live play turns it on
        /// unconditionally, exactly as <see cref="WrongInputOnWordGaps"/> is turned on.
        ///
        /// <para>With <see cref="SpaceSkipsWord"/> ON, a wrong letter typed on a WORD GAP spoils the
        /// gap WITHOUT moving the caret. The gap takes the typo in every other particular
        /// <see cref="WrongInputOnWordGaps"/> already gives it (the keypress, the error, the streak
        /// snapshot, the deferred judgement and the error-red typed glyph), but the caret PARKS on it:
        /// a second wrong letter overwrites that same cell instead of spoiling the next one, so one
        /// parked gap is one unfixed typo however many letters land on it; SPACE steps over the gap
        /// and leaves the typo standing; backspace clears it where it sits. Without the park, the
        /// follow-up space met a gap whose <c>Expected</c> the skip gate no longer reads as a gap, and
        /// one mistimed keystroke threw away the whole of the next word.</para>
        ///
        /// <para>With <see cref="SpaceSkipsWord"/> OFF, a SPACE typed on a lyric character is typed
        /// THROUGH as an ordinary typo instead of being rejected: with no word to skip, that press is
        /// nothing but a wrong character, so it takes the path every other wrong character takes (cell
        /// <see cref="CellState.Wrong"/>, caret advances, backspace-correctable). Rendering needs no
        /// arm: a wrong LYRIC cell shows its EXPECTED character in the error red
        /// (<c>LyricLineDisplay.CellGlyph</c> substitutes the typed one for gaps only), which is what
        /// this wants, since a red space is nothing at all to look at. The knock-on is deliberate:
        /// mid-word spaces stop feeding the mash-fail streak in live play, because they no longer
        /// reach the rejection branch that grows it.</para>
        ///
        /// <para>FALSE by default, and era-styled exactly like <see cref="WrongInputOnWordGaps"/>: set
        /// before the first keypress and left alone afterwards. Both halves change what an
        /// already-recorded keystroke MEANS, i.e. where the caret sits after it, so a replay written
        /// before backlog 184 re-derived under the live rule would desynchronise every keystroke that
        /// follows the first misplaced space in it. Which arm a re-derivation gets is decided by the
        /// replay's own CONFIG frame (<see cref="Replays.TypeBeatReplayFrame.StrictSpaces"/>, flags
        /// bit 4) and applied in <see cref="Replays.ReplayEngineFeed.Apply"/>.</para>
        /// </summary>
        public bool StrictSpaces { get; set; }

        /// <summary>
        /// Whether the spacebar is inside the timing challenge (see <see cref="ProcessKey"/>).
        /// <see cref="SpaceTimingRule.Untimed"/> is the live rule (backlog 148) and the default; only
        /// <see cref="Scoring.TypeBeatReplayScorer"/> ever sets the other one, to re-derive a score
        /// from before the exemption existed. It must be set BEFORE the first keypress and left alone
        /// afterwards, exactly like <see cref="ComboRestore"/> and <see cref="WindowScale"/>:
        /// judgements already made are never revisited.
        ///
        /// <para>Setting it recomputes the sync readouts' denominator
        /// (<see cref="totalTimedCells"/>), which is the one piece of the rule that is decided per
        /// BEATMAP rather than per keypress. Doing that here rather than in the constructor is what
        /// lets the rule be selected after the engine is built, which is when a replay harness knows
        /// which era it is judging.</para>
        /// </summary>
        public SpaceTimingRule SpaceTiming
        {
            get => spaceTiming;
            set
            {
                spaceTiming = value;
                countTimedCells();
            }
        }

        /// <summary>
        /// Whether the map's first line takes the <see cref="FIRST_LINE_LEAD_MS"/> head start at all
        /// (see <see cref="FirstLineTypingOpensAt"/>). Clear, the first line opens exactly as it did
        /// before the head start existed: on its own <see cref="TypingLine.ActivationTime"/>, with a
        /// press before that refused.
        ///
        /// <para>An ERA flag on CONFIG frame bit 16, like every other rule that decides whether a
        /// keystroke is ACCEPTED: the live client sets it for every stack
        /// (<c>DrawableTypeBeatRuleset.createEngine</c>), and <c>ReplayEngineFeed.Apply</c> sets it
        /// from the frame, so a bare engine and every replay stored before it keep the old gate. That
        /// matters even though a press made before a line opened was inert and never recorded: a
        /// frame's time is ROUNDED to the millisecond at capture, so a first keystroke made a fraction
        /// after a fractional activation can be stored a fraction BEFORE it. The old gate refuses
        /// that frame on re-derivation and the head start would accept it, which moves every
        /// keystroke after it onto a different cell. With the bit carried, a stored run re-derives
        /// under the gate it was played on, whichever that was.</para>
        /// </summary>
        public bool FirstLineLeadIn { get; set; }

        /// <summary>Mashing mod (Relax): every keypress is judged as the caret cell's expected char.</summary>
        public bool MashingEnabled { get; set; }

        /// <summary>
        /// Dyslexia mod (backlog 231, unranked): the characters of a WORD may be typed in ANY ORDER.
        /// A press is matched against the first cell of the word the caret is inside that nobody has
        /// typed anything into yet, scanned ascending, under exactly the rules the caret cell is
        /// matched under today (<see cref="CaseSensitive"/> included). Only a key that matches
        /// NOTHING still untyped in that word is a wrong key, and it is then priced exactly as it is
        /// now, at the caret, by the same wrong-input machinery.
        ///
        /// <para>The caret stays the LEFTMOST untyped typeable cell of the line, which is what the
        /// mod costs to implement and what it buys: <see cref="CaretCountablePosition"/>, the
        /// Fletcher rush cap (<see cref="rushesPastCap"/>), the Flashlight reveal window and
        /// <see cref="IsLineComplete"/> all read the caret as a monotone frontier, so a caret allowed
        /// to sit anywhere else would make all four measure something else. A press that lands ahead
        /// of the caret therefore moves nothing; the frontier rolls forward over the run of cells
        /// already typed the moment the leftmost one is finally struck
        /// (<see cref="advanceCaretToFrontier"/>).</para>
        ///
        /// <para>A FREESTYLE slot is not an any-order target. It matches every key but space, so a
        /// scan that offered it would consume it with the first press and starve the exact match the
        /// player meant; it still accepts anything AT THE CARET, exactly as it does today, which is
        /// reached whenever the scan finds nothing (so the slot fills with the key that fits nothing
        /// else, which is the character it stands for).</para>
        ///
        /// <para>Deterministic, which is what makes a Dyslexia replay re-derive: first match
        /// ascending is a pure function of (the cells' states, the pressed char), and a replay stores
        /// (char, time) alone.</para>
        ///
        /// <para>A MOD flag and NOT an era flag, which is why it has no CONFIG frame bit and no line
        /// in <c>ReplayEngineFeed.Apply</c>. An era bit exists to disambiguate runs recorded BEFORE a
        /// rule existed, and no stored run can carry a mod that did not exist when it was recorded:
        /// re-derivation is driven by the score's MOD LIST, read by the two engine factories
        /// (<c>DrawableTypeBeatRuleset.createEngine</c> and
        /// <see cref="Scoring.TypeBeatReplayScorer"/>). <see cref="MashingEnabled"/> is the exact
        /// precedent, and the two mods are declared incompatible: mashing rewrites the press into the
        /// caret cell's expected char before any of this is reached, so on an ordinary cell the
        /// leftmost untyped cell always matches and the scan can only ever return the caret. A
        /// FREESTYLE cell is exempt from that rewrite (the pressed char is the one thing such a cell
        /// must remember), which is the one place the pair would not merely be inert, and the
        /// incompatibility covers it rather than a guard here.</para>
        /// </summary>
        public bool AnyOrderWithinWord { get; set; }

        /// <summary>
        /// Literate mod: when true, input is matched against the target's EXACT case (no
        /// <see cref="Typeability.Fold"/>), so a right letter typed in the wrong case is judged
        /// wrong: rejected/miss, exactly like any other wrong char. Off by default: gameplay is
        /// case-insensitive. Requires the input path to actually produce upper-case chars for
        /// Shift-held keys (the OS layout commits them since backlog 383, and <see cref="TextInputFold"/>
        /// keeps their case), else capitals would be untypeable.
        /// Set from <see cref="Literate"/> at construction; still settable so a test can exercise
        /// exact-case matching on its own.
        /// </summary>
        public bool CaseSensitive { get; set; }

        /// <summary>
        /// The DEFAULT typing model (backlog 107): wrong (non-space) characters are typed through
        /// and marked red instead of rejected, and can be backspaced, which is what every typing
        /// site does. ON by default; the <see cref="Mods.TypeBeatModGatekeeper"/> mod turns it off
        /// to get strict rejection back.
        ///
        /// <para>Deliberately still phrased as "allow wrong input" rather than as the mod's own
        /// name. This flag is what the replay CONFIG frame persists as a single bit (see
        /// <see cref="Replays.TypeBeatReplayFrame"/>), and that bit's meaning is fixed by every
        /// replay already on disk: 1 = wrong input allowed. Naming the property for the mod would
        /// invert it against the wire and force a negation at every encode/decode site, for nothing.</para>
        ///
        /// <para>Three consequences of the flip worth stating where the flag lives:</para>
        /// <list type="bullet">
        /// <item>The 13-in-a-row mash-fail streak (<see cref="ConsecutiveWrongKeys"/>) only ever
        /// accrued on the rejection path, so it is now a Gatekeeper-only guard. That is where it
        /// belongs: it exists to stop a masher farming a model that refuses wrong keys.</item>
        /// <item>Backspace is gated on this flag at the INPUT layer (see <c>TypeBeatPlayfield</c>),
        /// so it is now live by default and inert under Gatekeeper, which is the same rule as
        /// before ("erasing exists only where an erasable char can land") resolving the other way.
        /// A retype selection can still be consumed under Gatekeeper (backlog 244).</item>
        /// <item>A wrong char typed through does NOT resolve its cell against the score processor
        /// (backlog 109). A miss is a character the line ran out of time on; a typo is a typo, and
        /// backspace makes it fixable, so the cell's result waits to see which of the two it turns
        /// out to be. Uncorrected it resolves at the seal as
        /// <see cref="Scoring.TypeBeatResultMapping.UNFIXED_TYPO"/> and NOT as a miss (backlog 124):
        /// the cell was finished, just wrongly. It still counts as one judged note, so accuracy, the
        /// combo ratio and the pp length term stay honest, and it still costs the mistype and the
        /// combo break it took at the keypress; what it no longer costs is rank and the miss
        /// count.</item>
        /// </list>
        ///
        /// <para>WHICH CELLS it reaches is a second axis, <see cref="WrongInputOnWordGaps"/>: the
        /// lyric characters always, the word gap only under that era flag (backlog 181). This one
        /// stays the gate on both, so Gatekeeper is still a single "no wrong key lands anywhere".</para>
        /// </summary>
        public bool AllowWrongInput { get; set; } = true;

        /// <summary>
        /// "Space to skip current word" (backlog 110), a local SETTING and not a mod, OFF by default.
        /// When on, a space pressed while the caret sits inside a word abandons the rest of that word
        /// and lands the caret on the word gap, so one bad character costs a word instead of the run.
        /// Japanese romaji input skips the remainder of the authored word from an unfinished
        /// character. A complete reading uses space as confirmation, and repeated confirmation
        /// spaces preserve the next character. Older replays retain single-character skipping.
        ///
        /// <para>What "abandons" means precisely, since backlog 167: every
        /// <see cref="CellState.Untyped"/> cell of that word enters
        /// <see cref="CellState.Abandoned"/>, which is a PHANTOM state and not a resolution. Nothing
        /// else does. A cell typed CORRECTLY has handed its Great over and there is no un-apply
        /// (<c>DrawableTypeBeatCharObject.ApplyEngineResult</c> drops every later result on an
        /// already-judged cell). A cell typed WRONG is a cell the player finished, so abandoning the
        /// word cannot make it a miss (backlog 124); its deferred result is decided at the seal like
        /// every other unfixed typo.</para>
        ///
        /// <para><b>Abandoning is not giving up (backlog 167).</b> A skipped word is RE-TYPEABLE:
        /// under <see cref="InputEra2"/> one backspace resets the phantom cells and the following
        /// typed space to <see cref="CellState.Untyped"/>, leaving the caret on the first abandoned
        /// character and keeping the correctly typed prefix intact (in the era before it, one
        /// backspace took the typed space and the next stepped transparently back over the phantom
        /// cells onto the last character actually typed). Re-typing earns ordinary judgements, HP recovery and
        /// the streak the skip broke. The setting therefore means "I will come back to this" rather
        /// than "I give up on this word", which is the accepted consequence of making the cells
        /// earnable at all: a cell takes exactly ONE osu result, so applying a Miss at the skip is
        /// precisely what made re-earning impossible.</para>
        ///
        /// <para>What the skip takes IMMEDIATELY is the one thing that cannot wait: a single combo
        /// break, snapshotted against the first abandoned cell through the same backlog 140
        /// machinery a typo's break uses, so the run resumes when that cell is finally typed. The
        /// miss COUNT and the osu RESULTS wait for the seal, where any cell still phantom resolves
        /// exactly as an untyped cell does. So a skip nobody goes back for costs precisely what it
        /// always cost, and one the player returns to costs nothing beyond the detour.</para>
        ///
        /// <para>The press itself is NOT a keypress judgement: it never enters the accuracy counters
        /// and never counts as a <see cref="Mistyped"/>, because it is a deliberate control action
        /// rather than a typo. It can only ever LOSE cells, never earn any, which is why it needs no
        /// score or pp multiplier despite being judgement-relevant.</para>
        ///
        /// <para>Under <see cref="InputEra2"/> it is only active while <see cref="AllowWrongInput"/>
        /// is on: Gatekeeper rejects a space inside a word just as it rejects any other wrong key. In
        /// the era before it the two were orthogonal and the skip worked under Gatekeeper too, which
        /// is what every stored Gatekeeper run re-derives on. It is also inert under
        /// <see cref="MashingEnabled"/>, where a pressed space has already been rewritten into the
        /// cell's expected char before this is reached.</para>
        ///
        /// <para>Judgement-relevant, so it travels in the replay CONFIG frame as bit 1 (see
        /// <see cref="Replays.TypeBeatReplayFrame"/>).</para>
        /// </summary>
        public bool SpaceSkipsWord { get; set; }

        /// <summary>
        /// FLEXIBLE LINES: the player's caret is decoupled from the song's playhead. Three
        /// behaviours, all confined to this flag so the pinned path stays byte-identical:
        /// <list type="bullet">
        /// <item>RUSH FREEDOM: finishing a line moves the caret straight on to the next one instead
        /// of waiting for its cue (<see cref="rollForwardIfFinishedEarly"/>), and since backlog 218
        /// no earlier than <see cref="FLETCHER_DRAG_GRACE_MS"/> before that cue
        /// (<see cref="BoundedRush"/>). The finished line is left unsealed and seals on its own
        /// normal deadline with nothing missed.</item>
        /// <item>DRAG FREEDOM: a line the player is still typing is not force-sealed at its normal
        /// deadline; the seal is deferred by <see cref="FLETCHER_DRAG_GRACE_MS"/> so the caret is
        /// never yanked off a line mid-word (<see cref="sealPermitted"/>).</item>
        /// <item>CHARACTER-DISTANCE RUSH CAP: a press that puts the caret more than
        /// <see cref="RushCap"/> countable chars ahead of the playhead lands and is awarded Meh
        /// whatever its timing (backlog 347, <see cref="RushCapCostsAccuracy"/>), or, in the era
        /// before that, scores as normal but earns no combo. Neither applies under
        /// <see cref="InputEra2"/>, which removed the cap outright for every new live run.</item>
        /// </list>
        /// Per-char judgement windows are untouched: rushing reads as early deltas and dragging as
        /// late ones, so accuracy, sync% and the judgement counts report the drift honestly.
        ///
        /// <para>Since backlog 208 this is the LIVE default for every stack
        /// (<c>DrawableTypeBeatRuleset.createEngine</c>), and the mod named Fletcher is the one that
        /// turns it OFF and re-pins the caret to the playhead
        /// (<see cref="Mods.TypeBeatModFletcher"/>). It stays FALSE by default here, which is the
        /// classic pinned ERA every replay recorded before 208 was played under, and
        /// <see cref="FlexibleLineSnap"/> is the bit that says a stored run was played the new
        /// way.</para>
        /// </summary>
        public bool FletcherEnabled { get; set; }

        /// <summary>
        /// The FLEXIBLE-LINES era (backlog 208), and the one behaviour that separates the new
        /// default from the old "FT" mod that shipped the same three freedoms: a caret sitting PAST
        /// THE LAST CHARACTER of its line is SNAPPED to the next line the moment that line starts
        /// (its <see cref="TypingLine.ActivationTime"/>), so a player who has finished their line is
        /// carried onto the new one exactly as pinned play always carried them
        /// (<see cref="snapForwardOnLineStart"/>). A line the player has NOT finished is never
        /// snapped: lagging behind is the freedom the flexible caret exists to grant.
        ///
        /// <para>Judgement relevant, so it is an ERA flag of its own on CONFIG frame bit 5, and NOT
        /// simply implied by <see cref="FletcherEnabled"/>: every stored "FT" run was played without
        /// the snap, so re-deriving one with it would move the caret onto a line its player was
        /// still parked behind and desynchronise every keystroke after it. Set for every new live
        /// stack that is not running the pinning mod; false everywhere else.</para>
        /// </summary>
        public bool FlexibleLineSnap { get; set; }

        /// <summary>
        /// THE SYMMETRIC RUSH BOUND (backlog 218): a finished caret may enter the next line only
        /// from <see cref="FLETCHER_DRAG_GRACE_MS"/> before that line's own
        /// <see cref="TypingLine.ActivationTime"/> onward (<see cref="entryPermitted"/>). The exact
        /// mirror of the drag side (<see cref="sealPermitted"/>), which holds a line open exactly
        /// that long past its natural end: one constant, both directions, so the player may run
        /// ahead of the song by the same margin they may fall behind it.
        ///
        /// <para>Without it RUSH was time-UNBOUNDED while DRAG never was.
        /// <see cref="rollForwardIfFinishedEarly"/> handed the caret to the next line the instant the
        /// last cell of the current one landed, however many seconds before that line's cue, and the
        /// roll is TRANSITIVE (the line it lands on can be typed out and rolled off in turn), so a
        /// fast player could walk the whole map at the top of the song. Nothing stopped that but the
        /// rush cap, which costs combo and blocks nothing.</para>
        ///
        /// <para>What a refused roll does: the caret PARKS past the last cell of its line, the state
        /// <see cref="FlexibleLineSnap"/> already understands. Keypresses there are inert
        /// (<see cref="ProcessKey"/> answers false on a complete line: no judgement, no typo, no
        /// combo break, exactly the dead-zone semantics the pinned game has), the WPM clock does not
        /// run (<see cref="Update"/> accrues only while the line is INCOMPLETE), and
        /// <see cref="snapForwardOnLineStart"/> performs the deferred roll the moment the bound
        /// opens. The DRAG side is untouched, and neither the drag cutoff's hand-over nor the seal
        /// loop's ordinary one is refused: those are the SONG arriving, an entry that is late rather
        /// than early.</para>
        ///
        /// <para>FALSE by default, and era-styled exactly like <see cref="FlexibleLineSnap"/>: it
        /// decides which line the caret is on at a given time, so a run stored before it existed
        /// carries CONFIG frame bit 7 (<see cref="Replays.TypeBeatReplayFrame.BoundedRush"/>) clear
        /// and re-derives with the unbounded roll its player actually had, byte for byte. Set for
        /// EVERY new live stack (<c>DrawableTypeBeatRuleset.createEngine</c>), the pinning mod's
        /// included, where it is inert because <see cref="FletcherEnabled"/> gates every roll: the
        /// same "record it uniformly" convention bits 3, 4 and 6 follow.</para>
        /// </summary>
        public bool BoundedRush { get; set; }

        /// <summary>
        /// MANUAL NEWLINES: the player closes a finished line themselves. With this set, a caret
        /// that has walked past the last cell of its line is NOT handed the next one by the two
        /// time-driven arms (<see cref="rollForwardIfFinishedEarly"/> on the press that finished it,
        /// <see cref="snapForwardOnLineStart"/> when the next line's entry window opens); it waits for
        /// a SPACE or an ENTER (<see cref="rollForwardManually"/>), or for the engine to TAKE the line
        /// at its seal - the instant the push warning's red bar completes, and the same one that
        /// forces a caret on with the setting off. That is also when a step back up closes (see
        /// <see cref="ProcessBackspace"/>).
        ///
        /// <para>WHERE THAT SEAL IS: the line is HELD to that instant rather than left to seal on its
        /// own deadline (<see cref="manualNewlineHoldsLineOpen"/>). A typed-out line would otherwise
        /// seal the moment its deadline passed - which is the next line's first word in any ordinary
        /// map - and the seal loop's own hand-over would move the caret there, well before the push.
        /// The hold is what makes "the engine took the line" and "the red bar reached the end" one
        /// instant for a finished line, exactly as they already are for a dragging one, so the caret
        /// is never pulled while the line it is standing on is still the player's to type or to step
        /// back into.</para>
        ///
        /// <para>THE NEWLINE ALWAYS LANDS, and the WINDOW REFUSES THE TYPING INSTEAD. A press made
        /// before the next line's <see cref="entryPermitted"/> window opens still moves the caret
        /// there; the line then waits - characters greyed (<see cref="AwaitingEntry"/>), keys and
        /// Enter swallowed - until the window opens, and a backspace from its head steps back up to
        /// the line it came from for as long as the engine has not taken that line yet. Refusing the
        /// press itself made the player press again at the right moment, which reads as the newline
        /// being broken; the window is about when the player may TYPE, which is what it now gates.</para>
        ///
        /// <para><b>Inert under a pinned caret</b>, like every other roll: <see cref="FletcherEnabled"/>
        /// gates it, so the Fletcher mod's players are unaffected wherever they set this. Live-only
        /// otherwise: it changes WHICH LINE the caret is on at a given time, so it is an ERA flag on
        /// CONFIG frame bit 14, recorded by the live client and re-applied from the frame
        /// (<c>ReplayEngineFeed.Apply</c>) so a run played with manual newlines re-derives with the
        /// caret parked exactly where its player parked it, and every run stored before the setting
        /// existed keeps the automatic hand-over it was played with. The setting's own era IS the
        /// bit, so a manual run stored before this rule changed re-derives under the new one: the
        /// refusal moved from the hand-over to the typing, which can put the caret on a later frame
        /// than it used to. Nothing outside the setting moves.</para>
        /// </summary>
        public bool ManualNewlines { get; set; }

        /// <summary>
        /// Whether a finished line can ALSO be handed on by TYPING, not only by space or Enter: any
        /// letter pressed while the caret is parked past the line's last cell moves to the next line
        /// and lands on that line's first slot, right or wrong. It moves on the space's own terms -
        /// no window gate on the press - and the ENTRY WINDOW then decides whether the character may
        /// be typed, exactly as it does for any other press on a line that has not opened yet; see
        /// the branch in <see cref="ProcessKey"/>. Inert under a pinned caret, because the space
        /// newline it rides on is.
        ///
        /// <para>Its own replay era bit (CONFIG bit 15), because it decides whether a keystroke is
        /// ACCEPTED, not merely where the caret is.</para>
        /// </summary>
        public bool NewlineOnTypedLetter { get; set; }

        /// <summary>
        /// THE RUSH CAP DOES NOT APPLY (backlog 261). With this set, <see cref="rushesPastCap"/> is
        /// never consulted: a press that puts the caret more than
        /// <see cref="FLETCHER_MAX_CHARS_AHEAD"/> countable chars past the playhead credits combo like
        /// any other. Nothing else about the unpinned caret moves, and
        /// <see cref="FletcherEnabled"/> itself is untouched, so the roll, the drag, the snap and the
        /// seal grace all behave exactly as they do for everyone else.
        ///
        /// <para><b>Set by the Puppeteer mod, and by nothing else</b>
        /// (<see cref="Mods.TypeBeatModPuppeteer.ApplyToEngine"/>, which the live factory and
        /// <see cref="Scoring.TypeBeatReplayScorer"/> both call, which is what keeps the two accounts
        /// of one run equal). Under that mod THE PLAYHEAD IS THE TAPE, and the tape is
        /// walled at the preset's <c>MaxVelocity</c>, so a player typing faster than the tape can run
        /// opens a lead that grows without bound: the cap then breaks their combo on a press it is
        /// still judging Great, and cannot re-arm while the sprint continues. Every mod-shaped
        /// alternative is worse. A bigger constant only moves the wall. The cap cannot measure TIME
        /// here (a press's distance from its target is exactly what this mod has declared meaningless,
        /// see <c>WINDOW_SCALE</c>). And the cap's purpose, stopping a player typing the whole map at
        /// the top of the song, is already served by the tape itself, which will not play a line the
        /// player has not reached.</para>
        ///
        /// <para><b>A MOD FLAG AND NOT AN ERA</b>, the precedent being
        /// <see cref="AnyOrderWithinWord"/> exactly: no stored run can predate a mod that did not
        /// exist when it was recorded, so there is nothing for a CONFIG bit to disambiguate and the
        /// score's MOD LIST is the whole mechanism. That is only true because Puppeteer is UNSHIPPED,
        /// and it is worth saying plainly: had one released build carried it, this would have needed
        /// an era bit like any judgement change, because it moves max_combo on runs already
        /// submitted. It is re-read at every press rather than at construction, so there is no
        /// window in which it can be momentarily wrong, and no replay recorder stamps it.</para>
        ///
        /// <para>A side effect worth knowing, on the replay side: the cap is the one judgement input
        /// that reads the PLAYHEAD rather than the keystroke, so a press near a countable boundary
        /// could re-derive on the far side of the cap threshold and give a Puppeteer replay a
        /// different combo than the run it stores (<c>PuppeteerReplayTransform</c> judges at the
        /// model's position, the live run judged at the clock's). With the cap inert under that mod,
        /// that whole divergence class is gone.</para>
        /// </summary>
        public bool RushCapExempt { get; set; }

        private bool alignSubdivisionTargets;

        /// <summary>Use the editor's effective character cuts for subdivided words. Recorded as a timing era.</summary>
        public bool AlignSubdivisionTargets
        {
            get => alignSubdivisionTargets;
            set
            {
                if (alignSubdivisionTargets == value)
                    return;
                alignSubdivisionTargets = value;
                foreach (var line in lines)
                    line.SetAlignedSubdivisionTargets(value);

                // The rush cap's playhead reads the cells' TargetTimes through a cached, sorted copy;
                // a swap of the targets that left it on the old era would measure the cap (and
                // RushCapCostsAccuracy's Meh) against targets this run is not judged on.
                rebuildCountableTargets();
                AlignSubdivisionTargetsChanged?.Invoke();
            }
        }

        /// <summary>
        /// Raised whenever <see cref="AlignSubdivisionTargets"/> actually changes value, which outside
        /// construction is only a replay's header frames re-selecting the target era (the watch path
        /// attaching or detaching a stored run). Read by the lyric stack to re-lay the pace bands it
        /// drew from the other era's targets, exactly as <see cref="AuthoredSyllablesOnlyChanged"/>.
        /// </summary>
        public event Action? AlignSubdivisionTargetsChanged;

        /// <summary>
        /// THE RUSH CAP COSTS ACCURACY, NOT COMBO (backlog 347). With this set, a press that leaves
        /// the caret more than <see cref="RushCap"/> countable chars past the playhead credits combo
        /// like any other press (no run break, no discarded restorable claim, no
        /// <see cref="ComboBroken"/>, <c>max_combo</c> untouched) and its penalty is its JUDGEMENT:
        /// the cell is awarded Meh whatever its delta says
        /// (<see cref="EraRules.RushCapTier"/>), the delta itself recorded
        /// untouched so the sync readouts stay honest, and <see cref="TypingCell.JudgedPastRushCap"/>
        /// marks the cell so an inert retype re-derives the same Meh and the display can draw it in
        /// the off-time styling. The cap is still re-evaluated per press, so every character typed
        /// out past it is a Meh and the first one back inside is judged on its timing. It also
        /// LOOSENS the cap from <see cref="LEGACY_FLETCHER_MAX_CHARS_AHEAD"/> (5) to
        /// <see cref="FLETCHER_MAX_CHARS_AHEAD"/> (6), which is the same re-derivation, so one flag
        /// carries both.
        ///
        /// <para>Why: under span timing a press far ahead by COUNT could still be judged Great by
        /// TIME, so the old cap took the run from a player whose every keystroke was well timed, and
        /// the break was the only thing it cost. Meh is the price backlog 199 already set for an
        /// off-time press, so the two "you are not with the song" cases now cost the same.</para>
        ///
        /// <para>An ERA, the first bit of the SECOND CONFIG flags word
        /// (<see cref="Replays.TypeBeatReplayFrame.CONFIG_EXTENDED"/>, bit 0), because the first
        /// word's carrier was full at bit 16. FALSE by default, which is the rule every replay stored
        /// before 347 was played under and what a replay with no extended header re-derives on
        /// (<c>ReplayEngineFeed.Apply</c> clears it on every CONFIG frame and sets it from the
        /// extended one). Set for EVERY live stack (<c>DrawableTypeBeatRuleset.createEngine</c>),
        /// inert wherever the caret is pinned (the cap belongs to <see cref="FletcherEnabled"/>) and
        /// under <see cref="RushCapExempt"/>, which takes the cap out of the question for both
        /// arms. <see cref="BoundedRush"/> is unrelated and unchanged: it refuses an early entry into
        /// the NEXT line and never broke combo.</para>
        /// </summary>
        public bool RushCapCostsAccuracy { get; set; }

        /// <summary>
        /// The map's final line seals the moment every typeable cell of it is typed correctly, rather
        /// than at the line's end, so the run finishes with the player's last word and not with the
        /// song. A player still behind when the line ends is unaffected: the ordinary seal rules
        /// (grace, drag cutoff) hold the line open until they finish or run out, and the final cell
        /// then seals it at once.
        ///
        /// <para>An ERA, bit 4 of the SECOND CONFIG flags word
        /// (<see cref="Replays.TypeBeatReplayFrame.CONFIG_EXTENDED"/>). FALSE by default, which is
        /// what every replay stored before it was played under: a key after the last word (a
        /// backspace) could still change the line before its end there. Set for every live stack
        /// (<c>DrawableTypeBeatRuleset.createEngine</c>); a seal is judgement-neutral on a fully
        /// correct line, so only the instant moves.</para>
        /// </summary>
        public bool EarlyFinish { get; set; }

        /// <summary>
        /// A Latin or Japanese-romaji press is judged at the instant its KEY went down, not at the
        /// instant the OS's text commit for it was paired with the key (see
        /// <c>TypeBeatPlayfield.PAIRING_GRACE_MS</c>): a commit that arrives frames after its key, and
        /// the spacebar's fallback on a platform that commits no space at all, are both stamped with
        /// the key's own time. The IME path of the other Polyglot languages is untouched: a composed
        /// commit has no key behind it, and is judged when it arrives.
        ///
        /// <para>An ERA, bit 9 of the SECOND CONFIG flags word
        /// (<see cref="Replays.TypeBeatReplayFrame.CONFIG_EXTENDED"/>), and like the first word's
        /// <c>WallClockFrames</c> it describes the FRAMES rather than a rule this class judges under:
        /// nothing here reads it. Its one consumer is the live key handler, which holds the engine at
        /// a waiting press's key time so the press is judged on exactly the call sequence a replay
        /// feeds (<c>Update(t)</c>, then the key at <c>t</c>), and the recorder writes it so a stored
        /// run says which instant its frames were stamped at. FALSE by default, which is what every
        /// replay stored before it was recorded under (pairing-time stamps, re-derived as stamped).
        /// Set for every live stack (<c>DrawableTypeBeatRuleset.createEngine</c>).</para>
        /// </summary>
        public bool KeyTimeJudgement { get; set; }

        /// <summary>
        /// THE SECOND INPUT ERA. One flag for a set of input and judgement rules that changed
        /// together, named for the era rather than for any one rule so the set moves as one. With it
        /// set the engine plays by all of the following, and with it clear by none of them:
        /// <list type="number">
        /// <item>NO RUSH CAP: <see cref="rushesPastCap"/> is never consulted, so a press any distance
        /// past the playhead is judged on its timing and credits combo, whatever
        /// <see cref="RushCapCostsAccuracy"/> says (that flag is still recorded, and inert here exactly
        /// as it is under <see cref="RushCapExempt"/>).</item>
        /// <item>BACKSPACE UNDOES A WORD SKIP (<see cref="tryUndoWordSkip"/>): one press re-opens the
        /// skipped word's abandoned cells, erases the space the skip typed onto the following gap, and
        /// parks the caret on the first abandoned cell, keeping the correctly typed prefix. The old
        /// rule took the gap on one press and then stepped transparently over the abandoned run onto
        /// the last character actually typed, erasing it.</item>
        /// <item>SPACE TO SKIP NEEDS WRONG INPUT: <see cref="SpaceSkipsWord"/> abandons a word only with
        /// <see cref="AllowWrongInput"/> on; under Gatekeeper a mid-word space is a rejected wrong
        /// key.</item>
        /// <item>THE RETYPE ANCHOR (<see cref="RetypeSelectionAnchor"/>): a gap typo selects the word
        /// before it, a wholly abandoned word anchors on its own head (no backlog 260 widening, which
        /// only the old backspace needed), and leading auto-skipped punctuation is stepped over.</item>
        /// </list>
        ///
        /// <para>An ERA, bit 1 of the SECOND CONFIG flags word
        /// (<see cref="Replays.TypeBeatReplayFrame.CONFIG_EXTENDED"/>), on exactly the terms of
        /// <see cref="RushCapCostsAccuracy"/>: FALSE by default, which is the rule every stored replay
        /// was played under, cleared by <c>ReplayEngineFeed.Apply</c> on every CONFIG frame and set from
        /// the extended one, and set for EVERY live stack (<c>DrawableTypeBeatRuleset.createEngine</c>).
        /// The replay frame's remarks carry the same list, and are the contract a port follows.</para>
        /// </summary>
        public bool InputEra2 { get; set; }

        /// <summary>
        /// AUTHORED SYLLABLES ONLY (backlog 363): which of the line's two syllable groupings this run
        /// is judged, lit, marked and paced on. On, a word the mapper did not subdivide (no authored
        /// boundary, no usable pause) is ONE group spanning its whole unit
        /// (<see cref="TypingLine.Syllables"/>), exactly what the editor shows; off, the automatic
        /// syllabifier's natural cut of that word at gameplay is used instead
        /// (<see cref="TypingLine.NaturalGrouping"/>), the grouping every replay stored before this
        /// was played against. A stylised spelling stays ungrouped under both, and no target moves
        /// under either: only which cells share a span, which cell the <see cref="FirstCharTiming"/>
        /// anchor applies to, and the <see cref="CharTimedStretch"/> runs derived from the groups.
        ///
        /// <para>An ERA, bit 2 (value 4) of the SECOND CONFIG flags word, on exactly the terms of
        /// <see cref="InputEra2"/>: FALSE by default, cleared by <c>ReplayEngineFeed.Apply</c> on every
        /// CONFIG frame and set from the extended one, and set for EVERY live stack. It cannot be a
        /// construction-time choice, because the groups are built when the engine is and the watch
        /// path attaches a replay to an engine the live factory already built; the line therefore
        /// carries both groupings and this flag selects one (<see cref="TypingLine.GroupingFor"/>).
        /// A display reading the groups has to re-lay its marks when the flag flips
        /// (<see cref="AuthoredSyllablesOnlyChanged"/>).</para>
        /// </summary>
        public bool AuthoredSyllablesOnly
        {
            get => authoredSyllablesOnly;
            set
            {
                if (authoredSyllablesOnly == value)
                    return;

                authoredSyllablesOnly = value;
                AuthoredSyllablesOnlyChanged?.Invoke();
            }
        }

        private bool authoredSyllablesOnly;

        /// <summary>
        /// Raised whenever <see cref="AuthoredSyllablesOnly"/> actually changes value, which outside
        /// construction is only a replay's header frames re-selecting the grouping (the watch path
        /// attaching or detaching a stored run). Read by the lyric stack to re-lay the syllable marks
        /// and pace bands it drew from the other grouping.
        /// </summary>
        public event Action? AuthoredSyllablesOnlyChanged;

        /// <summary>The syllable grouping <see cref="AuthoredSyllablesOnly"/> selects on <paramref name="line"/>.</summary>
        public SyllableGrouping GroupingOf(TypingLine line) => line.GroupingFor(AuthoredSyllablesOnly);

        /// <summary>
        /// Whether the flexible caret was asked for by a MOD rather than by the era bit, which is
        /// the one thing a CONFIG frame cannot say for itself. The retired "FT" mod is the only
        /// pre-208 way a run was flexible, and it recorded flags bit 5 CLEAR (the bit did not
        /// exist), so re-deriving such a run from the frame alone would pin a caret that was played
        /// unpinned. Set by the two engine factories from the score's mod list
        /// (<c>DrawableTypeBeatRuleset.createEngine</c> and
        /// <see cref="Scoring.TypeBeatReplayScorer"/>) and read in exactly one place,
        /// <c>ReplayEngineFeed.Apply</c>.
        /// </summary>
        public bool FlexibleCaretFromMod { get; set; }

        private SpaceTimingRule spaceTiming = SpaceTimingRule.Untimed;
    }
}
