// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using osu.Framework.Logging;
using osu.Framework.Platform;
using typebeat.Game.Beatmaps;
using typebeat.Game.Graphics;
using typebeat.Game.Graphics.Containers;
using typebeat.Game.Graphics.Fonts;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Overlays;
using typebeat.Game.Overlays.Notifications;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Import;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;
using typebeat.Game.Screens.Edit.Setup;
using typebeat.Game.Screens.ImportLyrics;
using typebeat.Game.Screens.Menu;

namespace typebeat.Game.Rulesets.TypeBeat.Edit
{
    /// <summary>
    /// The type!beat-specific editor setup section: a global lyric-vs-song offset (baked into the
    /// map data, unlike the per-player LyricOffsetMs preference) and an in-editor auto-timer that
    /// aligns a lyrics file to the map's audio and replaces the lines.
    ///
    /// <para>The lyrics file chooser takes .txt, .lrc, .elrc and Apple Music .ttml files. Its own registration
    /// as a file-import handler is what makes a drop work: the window hands a dropped file to the
    /// most recently registered handler that claims its extension, so while this screen is up a
    /// lyric file lands here rather than in the global song-import flow. TTML and enhanced LRC
    /// are applied on arrival using their own word timing; plain text and line-stamped LRC use
    /// the Generate timing button.</para>
    /// </summary>
    public partial class TypeBeatSetupSection : SetupSection
    {
        public override LocalisableString Title => "type!beat";

        /// <summary>Caption of the lyric font picker; also how tests find it among the section's controls.</summary>
        public const string LYRIC_FONT_CAPTION = "Lyric font";

        /// <summary>Caption of the bundle toggle; also how tests find it.</summary>
        public const string BUNDLE_FONT_CAPTION = "Bundle font file with the map";

        /// <summary>Caption of the freestyle colour row (backlog 384); also how tests find it.</summary>
        public const string FREESTYLE_COLOUR_CAPTION = "Freestyle character colour";

        /// <summary>Caption of the estimated vocals toggle (backlog 354); also how tests find it.</summary>
        public const string ESTIMATED_VOCALS_CAPTION = "Estimated vocals (pace lines from their stamps)";

        /// <summary>The picker item meaning "no map font": the game's built-in lyric font.</summary>
        public const string LYRIC_FONT_NONE = "None (built-in font)";

        /// <summary>
        /// The web's per-file cap for a bundled font, mirrored here so a mapper finds out in the
        /// editor rather than at submission (PackageValidator enforces the same figure server-side).
        /// </summary>
        public const long MAX_BUNDLED_FONT_BYTES = 5 * 1024 * 1024;

        /// <summary>
        /// THE BUNDLING RULE (backlog 291): the toggle defaults OFF for a known OS-bundled family,
        /// because the fonts everybody has are exactly the ones whose licences forbid
        /// redistribution, and ON for everything else, which is usually OFL or freeware (with the
        /// licence remaining the mapper's responsibility).
        /// </summary>
        public static bool BundleDefaultFor(string family) => !OsBundledFontFamilies.Contains(family);

        [Resolved]
        private IBindable<WorkingBeatmap> working { get; set; } = null!;

        [Resolved(CanBeNull = true)]
        private ILyricMapImporter? importer { get; set; }

        [Resolved(CanBeNull = true)]
        private INotificationOverlay? notifications { get; set; }

        [Resolved]
        private EditorClock editorClock { get; set; } = null!;

        [Resolved(CanBeNull = true)]
        private Editor? editor { get; set; }

        [Resolved(CanBeNull = true)]
        private OsuGame? game { get; set; }

        [Resolved]
        private Storage storage { get; set; } = null!;

        [Resolved(CanBeNull = true)]
        private BeatmapManager? beatmaps { get; set; }

        [Resolved(CanBeNull = true)]
        private LyricFontManager? fontManager { get; set; }

        private FormNumberBox beatdropBox = null!;
        private FormNumberBox offsetBox = null!;
        private FormButton demoButton = null!;
        private FormFileSelector lyricsSelector = null!;
        private FormCheckBox estimatedVocalsToggle = null!;
        private FormDropdown<string> fontDropdown = null!;
        private OsuSpriteText fontPreview = null!;
        private FormCheckBox bundleToggle = null!;
        private OsuTextFlowContainer bundleNote = null!;
        private FormColourSwatch freestyleColourRow = null!;

        /// <summary>
        /// What the row edits: never null, the default colour standing for "no key". Its DEFAULT is
        /// the ruleset default, so an empty hex commit resets the map to carrying no key at all.
        /// </summary>
        private readonly Bindable<Colour4> freestyleColour = new Bindable<Colour4>(FreestyleColourKey.Default);

        private IBindable<Colour4?> mapFreestyleColour = null!;

        // Guards the programmatic writes fontChanged makes to the bundle toggle (and rollbacks of a
        // refused bundle), so they do not re-enter bundleChanged; same shape as ResourcesSection's
        // rollingBack* flags.
        private bool updatingFontControls;

        [BackgroundDependencyLoader]
        private void load()
        {
            Children = new Drawable[]
            {
                beatdropBox = new FormNumberBox(allowDecimals: true)
                {
                    Caption = "Intro beatdrop (ms)",
                    HintText = "Optional. Flags this map to soundtrack the game intro: playback is timed so this moment lands exactly on the main menu reveal. Leave empty to unset.",
                    PlaceholderText = "unset",
                },
                new FormButton
                {
                    Caption = "Stamp the beatdrop at the editor's current playhead position",
                    ButtonText = "Set @ playhead",
                    Action = () => Beatmap.IntroBeatdrop.Value = Math.Round(editorClock.CurrentTime),
                },
                demoButton = new FormButton
                {
                    Caption = IntroBeatdropDemo.CAPTION,
                    ButtonText = "Restart & demo",
                    Action = requestBeatdropDemo,
                },
                offsetBox = new FormNumberBox(allowDecimals: true)
                {
                    Caption = "Shift all timings (ms)",
                    HintText = "Positive = lyrics later. Bakes a global offset into the map (all lines and words move together).",
                    PlaceholderText = "e.g. -50",
                },
                new FormButton
                {
                    Caption = "Apply offset to the whole map",
                    ButtonText = "Shift timings",
                    Action = applyShift,
                },
                lyricsSelector = new FormFileSelector(LyricImportExtensions.LYRICS)
                {
                    Caption = "Lyrics file",
                    PlaceholderText = "Click to select a .txt / .lrc / .elrc / .ttml lyrics file",
                },
                new FormButton
                {
                    Caption = "Auto-time the lyrics to this song's audio",
                    ButtonText = "Generate timing",
                    Action = runImport,
                },
                estimatedVocalsToggle = new FormCheckBox
                {
                    Caption = ESTIMATED_VOCALS_CAPTION,
                    HintText = "For vocals the auto-aligner cannot follow (screamed, effect-heavy): Generate timing paces every line evenly "
                               + "from its [mm:ss.xx] stamp instead of following the audio. Needs line stamps and an up-to-date local "
                               + "auto-aligner. Usually much worse on ordinary songs, so turn it on only when the aligned words are wrong. "
                               + "Remembered for this map set on this machine; never uploaded.",
                },
                fontDropdown = new FormDropdown<string>
                {
                    Caption = LYRIC_FONT_CAPTION,
                    HintText = "Font the lyrics are typed in on this map. Players see it unless they picked a font of their own; anyone whose machine can't show it gets the built-in font.",
                    Items = buildFontItems(),
                },
                fontPreview = new OsuSpriteText
                {
                    Text = "the quick brown fox jumps over the lazy dog",
                    Font = OsuFont.Default.With(size: 24),
                    Margin = new MarginPadding { Left = 9 },
                },
                bundleToggle = new FormCheckBox
                {
                    Caption = BUNDLE_FONT_CAPTION,
                    HintText = "Copies the font's file into the map, so players who don't have it installed still see it. Fonts that ship with an operating system can't be bundled (their licences forbid redistribution). Applies in the desktop client only for now; the browser player keeps its own font.",
                },
                bundleNote = new OsuTextFlowContainer(t => t.Font = OsuFont.Default.With(size: 14))
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Padding = new MarginPadding { Left = 9, Right = 9 },
                },
                // Backlog 384: the map's freestyle colour, straight under the bundle-font row (and its
                // note). A hex field plus the shared 2D picker; the map carries no key until the
                // mapper picks a colour other than the default.
                freestyleColourRow = new FormColourSwatch
                {
                    Caption = FREESTYLE_COLOUR_CAPTION,
                    HintText = "Colour of the freestyle (&) characters on this map, in gameplay and in the editor preview. Type a hex code or click the swatch for a picker; clear the field to go back to the default. Applies in the desktop client and on the website's player.",
                    Current = { BindTarget = freestyleColour },
                },
            };

            // Seed the font controls from what the map already carries; the value-changed wiring in
            // LoadComplete only runs on later, user-made changes.
            string initialFamily = string.IsNullOrEmpty(Beatmap.Metadata.LyricFont) ? LYRIC_FONT_NONE : Beatmap.Metadata.LyricFont;
            fontDropdown.Current.Value = initialFamily;
            bundleToggle.Current.Value = !string.IsNullOrEmpty(Beatmap.Metadata.LyricFontFile);
            bundleToggle.Current.Disabled = initialFamily == LYRIC_FONT_NONE;
            updateBundleNote(initialFamily);
            updateFontPreview(initialFamily);

            // The set's stored aligner vocal mode (realm user data, see BeatmapSetInfo.AlignerVocalMode).
            estimatedVocalsToggle.Current.Value = storedVocalMode() == AlignerVocalMode.Estimated;

            beatdropBox.OnCommit += (_, _) => commitBeatdrop();

            // Files with their own word timing are applied on arrival, whether selected or
            // dropped. Plain lyrics still wait for the Generate timing button.
            lyricsSelector.Current.BindValueChanged(selected =>
            {
                if (selected.NewValue is FileInfo file && tryReadLyrics(file, out string content))
                    tryImportTimedLyrics(file, content);
            });
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            fontDropdown.Current.BindValueChanged(e => fontChanged(e.NewValue));

            // Stored the moment it is toggled, like song select's intro pool toggle, rather than on an
            // editor save: it is user data about the song's vocals, not map content, so it never
            // dirties the map, and a re-align made before saving already reads it.
            estimatedVocalsToggle.Current.BindValueChanged(e =>
            {
                if (beatmaps != null && working.Value.BeatmapSetInfo is BeatmapSetInfo set)
                    beatmaps.SetAlignerVocalMode(set, e.NewValue ? AlignerVocalMode.Estimated : AlignerVocalMode.Aligned);
            });
            bundleToggle.Current.BindValueChanged(e => bundleChanged(e.NewValue));

            // Two-way between the row and the map (backlog 384). Map to row seeds it and follows an
            // undo; row to map stores the NORMALISED pick, so choosing the default removes the key.
            // Neither direction loops: each write lands on a value the other side already holds.
            mapFreestyleColour = Beatmap.FreestyleColour.GetBoundCopy();
            mapFreestyleColour.BindValueChanged(c => freestyleColour.Value = FreestyleColourKey.Resolve(c.NewValue), true);
            freestyleColour.BindValueChanged(c => Beatmap.FreestyleColour.Value = FreestyleColourKey.Normalise(c.NewValue));

            Beatmap.IntroBeatdrop.BindValueChanged(drop =>
            {
                beatdropBox.Current.Value = drop.NewValue is double d ? d.ToString("0", CultureInfo.InvariantCulture) : string.Empty;

                // With no beatdrop there is nothing to demo, so the button says so in place of its normal
                // caption and goes dead, rather than playing an intro on some fallback the user would hear
                // as their own map's.
                demoButton.Caption = IntroBeatdropDemo.CaptionFor(drop.NewValue);
                demoButton.Enabled.Value = IntroBeatdropDemo.CanDemo(drop.NewValue);
            }, true);
        }

        /// <summary>
        /// The map set's stored aligner vocal mode; <see cref="AlignerVocalMode.Aligned"/> when there is no
        /// beatmap manager or no stored set (a visual test, a set not yet saved).
        /// </summary>
        private AlignerVocalMode storedVocalMode()
            => beatmaps != null && working.Value.BeatmapSetInfo is BeatmapSetInfo set ? beatmaps.GetAlignerVocalMode(set) : AlignerVocalMode.Aligned;

        /// <summary>
        /// Restarts the game so that its startup intro is soundtracked by this map's beatdrop. A reboot
        /// throws the editor session away for real, so the standard save prompt is raised first and nothing
        /// is armed or torn down until it has been answered.
        /// </summary>
        private void requestBeatdropDemo() => IntroBeatdropDemo.Request(Beatmap.IntroBeatdrop.Value, promptToSave, rebootIntoDemo);

        private void promptToSave(Action confirmed)
        {
            // The editor is only absent in visual tests, where there is nothing to save.
            if (editor == null)
                confirmed();
            else
                editor.PromptToSaveThenExit(confirmed);
        }

        private void rebootIntoDemo(double dropTime)
        {
            if (game == null)
            {
                notify("Restarting isn't reachable from here.");
                return;
            }

            // The map is handed over by ID: whatever the reboot costs, it must come back up on THIS map
            // rather than on the pool's pick (see IntroBeatdropDemo).
            IntroBeatdropDemo.Reboot(storage, working.Value.BeatmapInfo.ID, dropTime, game.RestartAppWhenExited, game.AttemptExit, notify);
        }

        private void commitBeatdrop()
        {
            string text = beatdropBox.Current.Value?.Trim() ?? string.Empty;

            if (text.Length == 0)
            {
                Beatmap.IntroBeatdrop.Value = null;
                return;
            }

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && parsed >= 0)
                Beatmap.IntroBeatdrop.Value = Math.Round(parsed);
            else
            {
                // Invalid input: restore the current value's display.
                var current = Beatmap.IntroBeatdrop.Value;
                beatdropBox.Current.Value = current is double d ? d.ToString("0", CultureInfo.InvariantCulture) : string.Empty;
            }
        }

        private void applyShift()
        {
            string raw = offsetBox.Current.Value?.Trim() ?? string.Empty;

            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double delta))
            {
                notify("Enter a number of milliseconds to shift by.");
                return;
            }

            if (delta == 0)
                return;

            TypeBeatEditorOperations.ShiftAllTimes(Beatmap, delta);
            offsetBox.Current.Value = string.Empty;
            notify($"Shifted all lyric timings by {delta:0.##} ms.");
        }

        private CancellationTokenSource? importCancellation;

        private void runImport()
        {
            var lyricsFile = lyricsSelector.Current.Value;

            if (lyricsFile == null || !lyricsFile.Exists)
            {
                notify("Select a lyrics file (.txt, .lrc, .elrc or .ttml) first.");
                return;
            }

            // TTML and enhanced LRC already contain word timing and need neither audio nor
            // an aligner. Content detection also covers enhanced LRC saved with a .lrc name.
            if (!tryReadLyrics(lyricsFile, out string lyricsContent))
                return;

            if (tryImportTimedLyrics(lyricsFile, lyricsContent))
                return;

            if (importer == null)
            {
                notify("The lyric aligner is not available in this build.");
                return;
            }

            string audioFilename = working.Value.Metadata.AudioFile;

            if (string.IsNullOrEmpty(audioFilename))
            {
                notify("Set the song's audio first (Resources section), then generate timing.");
                return;
            }

            string? audioStoragePath = working.Value.BeatmapSetInfo.GetPathForFile(audioFilename);

            if (audioStoragePath == null)
            {
                notify("Couldn't locate the map's audio file in storage.");
                return;
            }

            // Copy the stored (hashed) audio out to a real temp path for the aligner subprocess.
            string tempAudio;

            try
            {
                tempAudio = Path.Combine(Path.GetTempPath(), "typebeat_editor_align", Guid.NewGuid().ToString("N") + Path.GetExtension(audioFilename));
                Directory.CreateDirectory(Path.GetDirectoryName(tempAudio)!);

                using (var source = working.Value.GetStream(audioStoragePath))
                using (var dest = File.Create(tempAudio))
                    source.CopyTo(dest);
            }
            catch (Exception e)
            {
                notify($"Couldn't read the map's audio: {e.Message}");
                return;
            }

            string artist = working.Value.Metadata.Artist;
            string title = working.Value.Metadata.Title;

            // The toggle beside the button, which mirrors the set's stored mode.
            AlignerVocalMode vocalMode = estimatedVocalsToggle.Current.Value ? AlignerVocalMode.Estimated : AlignerVocalMode.Aligned;

            var progressNotification = new ProgressNotification
            {
                Text = "Aligning lyrics to audio…",
                State = ProgressNotificationState.Active,
            };
            notifications?.Post(progressNotification);

            importCancellation?.Cancel();
            importCancellation = new CancellationTokenSource();
            var token = importCancellation.Token;
            progressNotification.CancelRequested += () =>
            {
                importCancellation?.Cancel();
                return true;
            };

            // The pipeline streams its own internals (model names, chunk counters, temp paths); the
            // notification shows the same short stage summaries the import screen does, and feeds
            // any fraction it emits to the notification's own bar.
            ImportStage? reportedStage = null;

            void reportProgress(string line)
            {
                var update = ImportProgressParser.Parse(line);
                reportedStage = update.Stage ?? reportedStage;

                string label = ImportProgressParser.LabelFor(reportedStage ?? ImportStage.Preparing);
                progressNotification.Text = char.ToUpperInvariant(label[0]) + label.Substring(1);

                if (update.Progress is float fraction)
                    progressNotification.Progress = fraction;
            }

            Task.Run(async () =>
            {
                try
                {
                    var (result, timingJson) = await importer.ProduceTimingJsonAsync(
                        tempAudio, lyricsContent, artist, title,
                        reportProgress,
                        token,
                        language: romanisationLanguage(lyricsContent),
                        vocalMode: vocalMode).ConfigureAwait(false);

                    if (!result.Success || timingJson == null)
                    {
                        progressNotification.State = ProgressNotificationState.Cancelled;
                        progressNotification.Text = $"Timing failed: {result.Error}";
                        return;
                    }

                    if (!TimingJsonLoader.TryParse(timingJson, out var lines) || lines.Count == 0)
                    {
                        progressNotification.State = ProgressNotificationState.Cancelled;
                        progressNotification.Text = "The alignment produced no usable lines.";
                        return;
                    }

                    Schedule(() =>
                    {
                        TypeBeatEditorOperations.ReplaceLines(Beatmap, lines, TypeBeatEditorOperations.InferGranularity(lines));
                        progressNotification.State = ProgressNotificationState.Completed;
                        progressNotification.Text = $"Imported {lines.Count} lyric lines.";
                    });
                }
                catch (Exception e)
                {
                    progressNotification.State = ProgressNotificationState.Cancelled;
                    progressNotification.Text = $"Timing failed: {e.Message}";
                }
                finally
                {
                    try { File.Delete(tempAudio); }
                    catch { /* best-effort */ }
                }
            }, token);
        }

        // ---- the lyric font picker (backlog 291) ----

        /// <summary>
        /// The picker's items: the built-in sentinel, then every installed system family, plus the
        /// map's own current font when it is not installed here (a set made on another machine must
        /// still display, and re-picking anything else clears it).
        /// </summary>
        private List<string> buildFontItems()
        {
            var items = new List<string> { LYRIC_FONT_NONE };

            if (fontManager != null)
                items.AddRange(fontManager.GetSystemFontFamilies());

            string current = Beatmap.Metadata.LyricFont;

            if (!string.IsNullOrEmpty(current) && !items.Contains(current))
                items.Add(current);

            return items;
        }

        /// <summary>
        /// A new font was picked. The old bundled file (if any) belongs to the OLD font, so it is
        /// removed unconditionally, and the bundle toggle is reset to the new family's DEFAULT under
        /// the bundling rule (see <see cref="BundleDefaultFor"/>): ON, with the copy made right
        /// here, for a family the mapper installed themselves; OFF with a cannot-bundle note for a
        /// known OS-bundled one.
        /// </summary>
        private void fontChanged(string family)
        {
            if (updatingFontControls)
                return;

            bool none = family == LYRIC_FONT_NONE;

            Beatmap.Metadata.LyricFont = none ? string.Empty : family;

            bool touchedFiles = removeBundledFile();

            bool wantBundle = !none && BundleDefaultFor(family);

            updatingFontControls = true;
            bundleToggle.Current.Disabled = false;
            bundleToggle.Current.Value = wantBundle;
            bundleToggle.Current.Disabled = none;
            updatingFontControls = false;

            if (wantBundle)
            {
                if (tryBundleFont(family))
                    touchedFiles = true;
                else
                    setToggleSilently(false);
            }

            updateBundleNote(family);
            updateFontPreview(family);

            Beatmap.SaveState();

            // File additions/removals live outside the editor's change tracking, so they are saved
            // through the same immediate-save convention every resource edit here follows.
            if (touchedFiles)
                editor?.SaveAndReload(withDialog: false);
        }

        private void bundleChanged(bool on)
        {
            if (updatingFontControls)
                return;

            string family = fontDropdown.Current.Value;

            if (family == LYRIC_FONT_NONE)
                return;

            if (on)
            {
                if (!tryBundleFont(family))
                {
                    setToggleSilently(false);
                    return;
                }
            }
            else
            {
                if (!removeBundledFile())
                    return;
            }

            Beatmap.SaveState();
            editor?.SaveAndReload(withDialog: false);
        }

        private void setToggleSilently(bool value)
        {
            updatingFontControls = true;
            bundleToggle.Current.Value = value;
            updatingFontControls = false;
        }

        /// <summary>
        /// Copies <paramref name="family"/>'s file out of the system font folder into the beatmap
        /// set (SixLabors exposes the source path) and points <c>LyricFontFile</c> at it. Refused,
        /// with the reason said out loud, for: a known OS-bundled family (licence), a family whose
        /// source SixLabors cannot name, a font collection (.ttc/.otc: one file carries several
        /// families, and extracting a single face means rewriting font tables, so the honest answer
        /// is to refuse rather than bundle every family in the collection), and a file over the
        /// submission cap. Returns whether the file is now bundled.
        /// </summary>
        private bool tryBundleFont(string family)
        {
            if (OsBundledFontFamilies.Contains(family))
            {
                notify($"'{family}' ships with an operating system and its licence does not allow bundling. Players who don't have it installed will see the built-in font.");
                return false;
            }

            if (beatmaps == null)
            {
                notify("Bundling isn't available in this context.");
                return false;
            }

            string? path = systemFontPath(family);

            if (path == null)
            {
                notify($"Couldn't locate the file behind '{family}', so it can't be bundled. Players who don't have it installed will see the built-in font.");
                return false;
            }

            string extension = Path.GetExtension(path);

            if (string.Equals(extension, ".ttc", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".otc", StringComparison.OrdinalIgnoreCase))
            {
                notify($"'{family}' lives inside a font collection ({extension}), which can't be bundled as a single face. Players who don't have it installed will see the built-in font.");
                return false;
            }

            try
            {
                var source = new FileInfo(path);

                if (source.Length > MAX_BUNDLED_FONT_BYTES)
                {
                    notify($"'{family}' is {source.Length / (1024.0 * 1024.0):0.#} MiB, over the {MAX_BUNDLED_FONT_BYTES / (1024 * 1024)} MiB cap a submitted map allows for a font.");
                    return false;
                }

                string filename = $"lyricfont{extension.ToLowerInvariant()}";

                using (var stream = source.OpenRead())
                    beatmaps.AddFile(working.Value.BeatmapSetInfo, stream, filename);

                Beatmap.Metadata.LyricFontFile = filename;

                notify($"Bundled {source.Name} with the map. Make sure its licence allows redistribution (OFL and similar free licences do); that responsibility is yours as the mapper.");
                return true;
            }
            catch (Exception e)
            {
                Logger.Error(e, $"Failed to bundle the font file for '{family}'.");
                notify($"Couldn't copy the font file for '{family}': {e.Message}");
                return false;
            }
        }

        /// <summary>Removes the currently bundled font file, if any. Returns whether anything changed.</summary>
        private bool removeBundledFile()
        {
            string filename = Beatmap.Metadata.LyricFontFile;

            if (string.IsNullOrEmpty(filename))
                return false;

            var file = working.Value.BeatmapSetInfo.GetFile(filename);

            if (file != null && beatmaps != null)
                beatmaps.DeleteFile(working.Value.BeatmapSetInfo, file);

            Beatmap.Metadata.LyricFontFile = string.Empty;
            return true;
        }

        /// <summary>
        /// The regular face's file path for an installed family, via SixLabors (which read that
        /// file to enumerate the family in the first place); null when it cannot be named.
        /// </summary>
        private static string? systemFontPath(string family)
        {
            try
            {
                if (!SixLabors.Fonts.SystemFonts.TryGet(family, out var fontFamily))
                    return null;

                var font = fontFamily.CreateFont(16);

                return font.TryGetPath(out string? path) && !string.IsNullOrEmpty(path) ? path : null;
            }
            catch (Exception e)
            {
                Logger.Error(e, $"Failed to resolve the system font path for '{family}'.");
                return null;
            }
        }

        private void updateBundleNote(string family)
        {
            if (family == LYRIC_FONT_NONE)
                bundleNote.Text = string.Empty;
            else if (OsBundledFontFamilies.Contains(family))
                bundleNote.Text = $"'{family}' ships with an operating system, so its file cannot be bundled: players on a platform without it will see the built-in font. Pick a freely licensed font to bundle one.";
            else
                bundleNote.Text = "Bundling copies the font file into the map. The font's licence is your responsibility as the mapper; OFL and similar free licences allow this.";
        }

        /// <summary>
        /// Live preview: the pangram above re-renders in the picked family the moment it is chosen
        /// (registered into the game font store on the spot, exactly as gameplay would). A family
        /// that cannot load previews in the built-in font, which is also what a player would see.
        /// </summary>
        private void updateFontPreview(string family)
        {
            if (family != LYRIC_FONT_NONE && fontManager?.EnsureRegistered(family) == true)
                fontPreview.Font = new FontUsage(family, 24);
            else
                fontPreview.Font = OsuFont.Default.With(size: 24);
        }

        private void notify(string message) => notifications?.Post(new SimpleNotification { Text = message });

        /// <summary>Reads a lyrics file, reporting (rather than throwing) anything that stops it.</summary>
        private bool tryReadLyrics(FileInfo file, out string content)
        {
            try
            {
                content = File.ReadAllText(file.FullName);
                return true;
            }
            catch (Exception e)
            {
                content = string.Empty;
                notify($"Couldn't read {file.Name}: {e.Message}");
                return false;
            }
        }

        /// <summary>Handles files with authored word timing before the audio/aligner path.</summary>
        private bool tryImportTimedLyrics(FileInfo file, string content)
        {
            if (TtmlParser.IsTtmlFile(file.FullName) || TtmlParser.LooksLikeTtml(content))
            {
                importTtml(file, content);
                return true;
            }

            if (!LrcParser.HasWordStamps(content))
                return false;

            var lines = ImportSyllables.Apply(LrcParser.Parse(content, romanisationLanguage(content), out int clamped));

            if (lines.Count == 0)
            {
                notify($"{file.Name} has no timed lyrics in it.");
                return true;
            }

            TypeBeatEditorOperations.ReplaceLines(Beatmap, lines, TypeBeatEditorOperations.InferGranularity(lines));
            string message = $"Imported {lines.Count} lyric lines from {file.Name}.";

            if (LyricMapImporter.ClampedWordStampsNotice(clamped) is string notice)
                message += " " + notice + ".";

            notify(message);
            return true;
        }

        /// <summary>
        /// Replaces the map's lines with a TTML's own word timing, in one transaction so the whole
        /// import is a single undo. The document's timings are taken as written; a mapper whose rip
        /// needs nudging uses the "shift all timings" control above, which is also where the
        /// leading-silence note points.
        /// </summary>
        private void importTtml(FileInfo file, string content)
        {
            if (!TtmlParser.TryParse(content, out IReadOnlyList<LyricLine> lines, out TtmlParser.TtmlMetadata metadata, language: romanisationLanguage(content))
                || lines.Count == 0)
            {
                notify($"{file.Name} has no timed lyrics in it.");
                return;
            }

            // Syllabified ONCE, here, like every other import sink (backlog 363): this path parses
            // the TTML itself rather than going through the importer's synthesis.
            lines = ImportSyllables.Apply(lines);

            TypeBeatEditorOperations.ReplaceLines(Beatmap, lines, TypeBeatEditorOperations.InferGranularity(lines));

            string message = $"Imported {lines.Count} lyric lines from {file.Name}.";

            // Not applied, only said out loud: an Apple TTML states how much silence precedes the
            // first word, and a rip that needs that shift is common enough to be worth naming.
            if (metadata.LeadingSilenceMs > 0)
                message += $" The file declares {metadata.LeadingSilenceMs:0.#} ms of leading silence; use Shift timings if the lyrics sit early.";

            notify(message);
        }

        /// <summary>
        /// The language non-Latin lyrics are romanised under (backlog 330): the map's own when the
        /// mapper has set one, else the one the lyrics' script says.
        /// </summary>
        private string? romanisationLanguage(string lyrics) => LyricOriginals.RomanisationLanguage(Beatmap.Metadata.Language, new[] { lyrics });

        protected override void Dispose(bool isDisposing)
        {
            importCancellation?.Cancel();
            base.Dispose(isDisposing);
        }
    }
}
