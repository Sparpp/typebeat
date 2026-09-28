// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Collections.Generic;

namespace typebeat.Game.Graphics.Fonts
{
    /// <summary>
    /// The font families the major desktop OSes ship by default (backlog 291), which is exactly the
    /// set whose licences generally FORBID redistribution: Windows and macOS system fonts are
    /// commercially licensed to the OS, so a mapper may not bundle their files with a map. The
    /// editor's "bundle font file with the map" toggle therefore defaults OFF for these (with a
    /// cannot-bundle warning: players who lack the family get the fallback font), and ON for
    /// everything else (Google Fonts, itch pixel fonts and other downloads are usually OFL or
    /// freeware, with a one-line reminder that the licence is the mapper's responsibility).
    /// </summary>
    /// <remarks>
    /// One fixed, conservative table rather than any attempt at licence detection: membership is
    /// drawn from the default font sets of Windows 10/11 ("Fonts supplied with Windows",
    /// learn.microsoft.com/typography/fonts/windows_10_font_list), macOS ("Fonts included with
    /// macOS", support.apple.com/en-us/103197) and stock Ubuntu (the ubuntu-family-fonts, DejaVu
    /// and Liberation packages of a default desktop install). Conservative means: only families
    /// clearly in a DEFAULT install are listed, because a false positive merely defaults the
    /// toggle off (the mapper can be corrected by reality), while the list existing at all is what
    /// stops the obvious Arial/Segoe UI/Helvetica cases. The Ubuntu families are free licences
    /// (Ubuntu Font Licence, Bitstream/DejaVu, OFL) but are listed anyway: everyone on that OS has
    /// them, so bundling would be dead weight in every download.
    /// Matching is case-insensitive on the exact family name.
    /// </remarks>
    public static class OsBundledFontFamilies
    {
        private static readonly HashSet<string> families = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Windows (10/11 default set, the commonly picked families).
            "Arial", "Arial Black", "Bahnschrift", "Calibri", "Cambria", "Cambria Math", "Candara",
            "Comic Sans MS", "Consolas", "Constantia", "Corbel", "Courier New", "Ebrima",
            "Franklin Gothic", "Franklin Gothic Medium", "Gabriola", "Gadugi", "Georgia", "Impact",
            "Ink Free", "Lucida Console", "Lucida Sans Unicode", "Malgun Gothic", "Microsoft JhengHei",
            "Microsoft YaHei", "MingLiU-ExtB", "MS Gothic", "MV Boli", "Nirmala UI",
            "Palatino Linotype", "Segoe Print", "Segoe Script", "Segoe UI", "Segoe UI Variable",
            "SimSun", "Sitka", "Sylfaen", "Symbol", "Tahoma", "Times New Roman", "Trebuchet MS",
            "Verdana", "Webdings", "Wingdings", "Yu Gothic",

            // macOS (default set).
            "American Typewriter", "Avenir", "Avenir Next", "Baskerville", "Chalkboard",
            "Chalkboard SE", "Charter", "Cochin", "Copperplate", "Courier", "Didot", "Futura",
            "Geneva", "Gill Sans", "Helvetica", "Helvetica Neue", "Hoefler Text", "Menlo", "Monaco",
            "Optima", "Palatino", "SF Compact", "SF Mono", "SF Pro", "San Francisco", "Times",
            "Zapfino",

            // Ubuntu (default desktop install).
            "Ubuntu", "Ubuntu Condensed", "Ubuntu Mono", "DejaVu Sans", "DejaVu Sans Mono",
            "DejaVu Serif", "Liberation Mono", "Liberation Sans", "Liberation Serif", "FreeMono",
            "FreeSans", "FreeSerif",
        };

        /// <summary>Whether <paramref name="family"/> is a known OS-bundled family (case-insensitive).</summary>
        public static bool Contains(string? family)
            => !string.IsNullOrWhiteSpace(family) && families.Contains(family.Trim());
    }
}
