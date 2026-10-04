// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Linq;
using System.Runtime.CompilerServices;
using osu.Framework.Localisation;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;

namespace typebeat.Game.Rulesets.TypeBeat.Mods
{
    /// <summary>
    /// Polyglot (backlog 331): the lyric is typed in its ORIGINAL SCRIPT ("Привет", "こんにちは",
    /// "안녕") rather than the romanisation the map stores for everyone else.
    ///
    /// <list type="bullet">
    /// <item>THE CELL LIST changes, like <see cref="TypeBeatModLiterate"/>'s: each word that records an
    /// original plays it, at the word's own start and end, and the cells are re-spread over it (see
    /// <see cref="Gameplay.PolyglotLine"/>). A word with no original plays its romanised text, so a
    /// mixed line works, and a word the romaniser could not spell plays its original. Stamped on every
    /// line object in the one window where that is safe, after conversion and before ApplyDefaults,
    /// and read by both engine factories off the mod list.</item>
    /// <item>INPUT is the OS's committed text rather than the physical key map: whatever the player's
    /// layout or IME produces is the character judged (see the playfield's key handler), matched in
    /// NFC and case-insensitively unless Literate is on (<see cref="Gameplay.PolyglotText"/>).
    /// Japanese maps instead process romaji internally, accepting IME spelling variants and
    /// displaying furigana over kanji. No per-character IME submission is required.</item>
    /// <item>LOCAL ONLY (<see cref="Mod.LocalOnly"/>): no token, no submission, no online board for a
    /// selection holding it, and the play is kept in the local score list with its replay. It is
    /// unranked regardless, and 1.0x, because nothing it produces reaches a leaderboard.</item>
    /// <item>AVAILABLE only on a map that records originals (<see cref="IsAvailableFor"/>).</item>
    /// </list>
    ///
    /// <para>The star rating and the WPM figures stay the ROMANISED map's: neither the difficulty
    /// calculator nor the pace statistics read the stamp, and the song select marks them as such
    /// while this mod is selected.</para>
    ///
    /// <para>A MOD, not an ERA: a replay is identified as a Polyglot play by the mod in its score, and
    /// no CONFIG frame bit is spent on the mod itself. Extended bit 4 separately records the
    /// Japanese romaji input era so older committed-text Polyglot replays stay unchanged.</para>
    /// </summary>
    public class TypeBeatModPolyglot : Mod, IApplicableAfterBeatmapConversion
    {
        public override string Name => "Polyglot";

        public override string Acronym => "PG";

        public override LocalisableString Description => "Type the lyric in its original script. Japanese maps accept romaji.";

        /// <summary>CONVERSION: the question the game asks changes (which text), nothing is tightened or loosened.</summary>
        public override ModType Type => ModType.Conversion;

        // Legacy self-report only; the authoritative multiplier lives in
        // TypeBeatScoreMultiplierCalculator, which leaves this mod unlisted (1.0x) on purpose.
#pragma warning disable CS0672 // Member overrides obsolete member
        public override double ScoreMultiplier => 1.0;
#pragma warning restore CS0672

        public override bool Ranked => false;

        public override bool LocalOnly => true;

        public override bool ValidForMultiplayer => false;

        public override bool ValidForMultiplayerAsFreeMod => false;

        public override LocalisableString UnavailableReason => "This map has no original-script lyrics";

        /// <summary>Song select rates a Polyglot selection on the romanised lyric (see the class summary) and says so.</summary>
        public override LocalisableString RatingNote => "star rating and WPM are the romanised lyric's";

        /// <summary>Whether each decoded beatmap records originals, computed once per beatmap instance.</summary>
        private static readonly ConditionalWeakTable<IBeatmap, object> has_originals = new ConditionalWeakTable<IBeatmap, object>();

        /// <summary>
        /// Available only on a map that records an original somewhere
        /// (<see cref="LyricOriginals.HasOriginals(IBeatmap)"/>), cached per beatmap. No beatmap known
        /// at all is answered available, as the base does; a beatmap that cannot be read is not.
        /// </summary>
        public override bool IsAvailableFor(IWorkingBeatmap? beatmap)
        {
            if (beatmap == null)
                return true;

            try
            {
                return HasOriginals(beatmap.Beatmap);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary><see cref="LyricOriginals.HasOriginals(IBeatmap)"/>, cached per beatmap instance.</summary>
        public static bool HasOriginals(IBeatmap? beatmap)
        {
            if (beatmap == null)
                return false;

            return (bool)has_originals.GetValue(beatmap, b => LyricOriginals.HasOriginals(b));
        }

        /// <summary>
        /// The language a map's originals romanise under: its own <c>Language:</c> when it names one,
        /// else the one read off the originals themselves. Both engine factories and
        /// <see cref="ApplyToBeatmap"/> ask this one question, so the cells and the nested scoring
        /// objects carry the same syllable cuts.
        /// </summary>
        public static string? LanguageFor(IBeatmap beatmap)
        {
            var lines = beatmap.HitObjects.OfType<TypeBeatHitObject>().Select(h => h.Line).ToList();
            var originals = lines.Select(l => l.Original)
                                 .Concat(lines.SelectMany(l => l.Units.Select(u => u.Original)))
                                 .Concat(lines.SelectMany(l => l.UnromanisedWords.Select(w => (string?)w.Original)));

            return LyricOriginals.RomanisationLanguage(beatmap.BeatmapInfo.Metadata.Language, originals);
        }

        public void ApplyToBeatmap(IBeatmap beatmap)
        {
            string? language = LanguageFor(beatmap);

            foreach (var line in beatmap.HitObjects.OfType<TypeBeatHitObject>())
            {
                line.Polyglot = true;
                line.PolyglotLanguage = language;
            }
        }
    }
}
