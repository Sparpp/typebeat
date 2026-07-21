// TEMP calibration harness — reads the user's imported Foreigner "Cold as Ice" diffs and prints SR.
// Not a committed regression test (depends on local AppData); deleted after tuning.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class ForeignerCalibration
    {
        private static readonly string files = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TypeBeat", "files");

        private static readonly (string label, string sha)[] maps =
        {
            ("Insane (42)", "7f940f8604169a54ffb88a452dca313743e23f93dacc4fb11c11caeb34c45ff6"),
            ("Hard   (34)", "70b32e984b938c63a059c7158e0bd2c75a5c659af4f4d29db4092f1b16d8018b"),
            ("Cut    (23)", "2be3e1e0e4583be45464eebbda8a494fb1818a6ede5b7c749b917293cab76827"),
        };

        private static IReadOnlyList<LyricLine> load(string sha)
        {
            string path = Path.Combine(files, sha.Substring(0, 1), sha.Substring(0, 2), sha);
            string[] all = File.ReadAllLines(path);

            var raw = new List<TimingJsonLoader.RawLine>();
            double? songEndMs = null;
            bool inLyrics = false;

            foreach (string line in all)
            {
                string t = line.Trim();

                if (t.StartsWith("[") && t.EndsWith("]"))
                {
                    inLyrics = t == "[Lyrics]";
                    continue;
                }

                if (!inLyrics || t.Length == 0)
                    continue;

                using var doc = JsonDocument.Parse(t);
                var root = doc.RootElement;

                if (root.TryGetProperty("version", out _))
                {
                    if (root.TryGetProperty("song_end_ms", out var se) && se.TryGetDouble(out double v))
                        songEndMs = v;
                    continue;
                }

                if (TimingJsonLoader.TryParseRawLine(root, out var rl))
                    raw.Add(rl);
            }

            return TimingJsonLoader.BuildLines(raw, songEndMs);
        }

        [Test]
        public void PrintRatings()
        {
            var srs = new Dictionary<string, double>();
            var sb = new System.Text.StringBuilder();

            foreach (var (label, sha) in maps)
            {
                var lines = load(sha);
                double sr = LyricDifficulty.Compute(lines);
                srs[label] = sr;
                sb.AppendLine($"{label}: {lines.Count} lines -> {sr:0.000} stars");
            }

            sb.AppendLine($"Insane - Hard gap = {srs["Insane (42)"] - srs["Hard   (34)"]:0.000}");
            sb.AppendLine($"Hard   - Cut  gap = {srs["Hard   (34)"] - srs["Cut    (23)"]:0.000}");

            string outPath = Path.Combine(Path.GetTempPath(), "foreigner_sr.txt");
            File.WriteAllText(outPath, sb.ToString());
            TestContext.WriteLine(sb.ToString());
        }
    }
}
