// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using typebeat.Game.Beatmaps;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Overlays.Notifications;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Import;

namespace typebeat.Game.Rulesets.TypeBeat.Edit
{
    public partial class TypeBeatSetupSection
    {
        public const string GENERATE_LYRICS_TOOLTIP = "Generate original script lyrics for non-English languages. English lyrics already use their original script.";

        private FormButton generateLyricsButton = null!;
        private readonly OrderedLinesCache generationLines = new OrderedLinesCache();
        private CancellationTokenSource? lyricsCancellation;
        private bool generatingLyrics;
        private int generationRevision = -1;
        private bool missingOriginals;
        private string? providedLyrics;

        protected override void UpdateAfterChildren()
        {
            base.UpdateAfterChildren();

            var lines = generationLines.Get(Beatmap);

            if (generationRevision != generationLines.Version)
            {
                missingOriginals = lines.Any(h => h.Line.RawText.Length > 0 && !LyricOriginals.HasOriginals(new[] { h.Line }));
                generationRevision = generationLines.Version;
            }

            BeatmapLanguage language = Beatmap.Metadata.Language;
            bool hasProvided = providedLyrics != null && OriginalLyricGenerator.HasOriginalScript(providedLyrics, language);
            generateLyricsButton.Enabled.Value = OriginalLyricGenerator.CanGenerate(language) && (missingOriginals || hasProvided) && !generatingLyrics;
            generateLyricsButton.TooltipText = language == BeatmapLanguage.English ? GENERATE_LYRICS_TOOLTIP
                : language == BeatmapLanguage.Unspecified ? "Select the song's language to generate original script lyrics."
                : language == BeatmapLanguage.Instrumental ? "Instrumental maps have no sung lyrics to generate."
                : generatingLyrics ? "Generating original lyrics…"
                : hasProvided ? "Align the selected lyrics file to this map and replace its original lyrics."
                : !missingOriginals ? "This map already has original lyrics, or has no romanized lyrics to convert."
                : language == BeatmapLanguage.Japanese ? "Convert romaji to Japanese using the bundled dictionary. Works offline; review the suggested spellings in the lyric editor."
                : "Look up this song's original script lyrics using its artist and title. Requires an internet connection.";
        }

        private void generateLyrics()
        {
            if (!generateLyricsButton.Enabled.Value || generatingLyrics)
                return;

            string? provided = null;
            if (lyricsSelector.Current.Value is { } file)
            {
                if (!tryReadLyrics(file, out string content))
                    return;
                if (OriginalLyricGenerator.HasOriginalScript(content, Beatmap.Metadata.Language))
                    provided = content;
            }
            var targets = TypeBeatEditorOperations.OrderedLines(Beatmap);
            var snapshot = targets.Select(h => h.Line).ToArray();
            BeatmapLanguage language = Beatmap.Metadata.Language;
            string artist = string.IsNullOrWhiteSpace(Beatmap.Metadata.ArtistUnicode) ? Beatmap.Metadata.Artist : Beatmap.Metadata.ArtistUnicode;
            string title = string.IsNullOrWhiteSpace(Beatmap.Metadata.TitleUnicode) ? Beatmap.Metadata.Title : Beatmap.Metadata.TitleUnicode;
            string unicodeArtist = Beatmap.Metadata.ArtistUnicode;
            string unicodeTitle = Beatmap.Metadata.TitleUnicode;
            string romanizedArtist = Beatmap.Metadata.Artist;
            string romanizedTitle = Beatmap.Metadata.Title;

            lyricsCancellation?.Dispose();
            var cancellation = lyricsCancellation = new CancellationTokenSource();
            generatingLyrics = true;
            generateLyricsButton.Enabled.Value = false;

            var progress = new ProgressNotification
            {
                Text = provided != null ? "Aligning the selected original lyrics…" : language == BeatmapLanguage.Japanese ? "Generating Japanese lyrics from the dictionary…" : "Looking up original lyrics…",
                State = ProgressNotificationState.Active,
            };
            notifications?.Post(progress);
            progress.CancelRequested += () =>
            {
                cancellation.Cancel();
                return true;
            };

            Task.Run(async () =>
            {
                try
                {
                    var originals = provided != null
                        ? OriginalLyricGenerator.MatchLines(snapshot, provided, language, cancellation.Token)
                        : await OriginalLyricGenerator.GenerateAsync(artist, title, language, snapshot, cancellation.Token).ConfigureAwait(false);

                    if (provided == null && language != BeatmapLanguage.Japanese && originals.All(o => o == null) && (artist != romanizedArtist || title != romanizedTitle)
                        && !string.IsNullOrWhiteSpace(romanizedArtist) && !string.IsNullOrWhiteSpace(romanizedTitle))
                        originals = await OriginalLyricGenerator.GenerateAsync(romanizedArtist, romanizedTitle, language, snapshot, cancellation.Token).ConfigureAwait(false);

                    cancellation.Token.ThrowIfCancellationRequested();

                    Schedule(() =>
                    {
                        if (cancellation.IsCancellationRequested)
                            return;

                        bool sameSong = Beatmap.Metadata.Language == language && (language == BeatmapLanguage.Japanese
                                        || Beatmap.Metadata.Artist == romanizedArtist && Beatmap.Metadata.Title == romanizedTitle
                                        && Beatmap.Metadata.ArtistUnicode == unicodeArtist && Beatmap.Metadata.TitleUnicode == unicodeTitle);
                        int added = sameSong
                            ? TypeBeatEditorOperations.AddGeneratedOriginals(Beatmap, targets, snapshot, originals, replaceExisting: provided != null)
                            : 0;

                        progress.State = added > 0 ? ProgressNotificationState.Completed : ProgressNotificationState.Cancelled;
                        progress.Text = added > 0 ? $"Added original lyrics to {added} lines."
                            : provided != null ? "No supplied lyrics matched the romanized map. Check the lyrics file and selected language."
                            : language == BeatmapLanguage.Japanese ? "No romaji could be converted. Check the lyric text, or enter original lyrics manually."
                            : "No matching original lyrics could be added. Check the song's artist, title and language, or import original lyrics manually.";
                    });
                }
                catch (OperationCanceledException)
                {
                    Schedule(() =>
                    {
                        progress.State = ProgressNotificationState.Cancelled;
                        progress.Text = cancellation.IsCancellationRequested ? "Lyric generation cancelled." : "Lyric lookup timed out. Try again.";
                    });
                }
                catch (Exception e)
                {
                    Schedule(() =>
                    {
                        progress.State = ProgressNotificationState.Cancelled;
                        progress.Text = $"Couldn't generate original lyrics: {e.Message}";
                    });
                }
                finally
                {
                    Schedule(() => generatingLyrics = false);
                }
            });
        }
    }
}
