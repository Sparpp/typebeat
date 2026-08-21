// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Corpus-driven pins for <see cref="Syllabifier"/>. Where a word has two defensible splits
    /// the chosen one is pinned and commented; the phonotactic convention here follows the
    /// maximal-onset rule (no|thing, hea|ven) rather than dictionary hyphenation (noth|ing,
    /// heav|en), which is equally defensible for grouping characters into timing windows.
    ///
    /// Known corpus failures (documented, not asserted): onion (3 for 2), naked (1 for 2),
    /// client/science (1 for 2), chaos (1 for 2), idea (2 for 3), poet (1 for 2), ruin (1 for 2),
    /// argue (1 for 2), persuade (3 for 2).
    /// </summary>
    [TestFixture]
    public class SyllabifierTest
    {
        // ---- one-syllable words (the bulk of any lyric stream) ----
        [TestCase("the")]
        [TestCase("love")]
        [TestCase("heart")]
        [TestCase("night")]
        [TestCase("world")]
        [TestCase("girl")]
        [TestCase("friend")] // "ie" stays one nucleus off the quiet/diet pattern
        [TestCase("dream")]
        [TestCase("time")]
        [TestCase("life")]
        [TestCase("eyes")] // y is a glide, the -es is silent after it
        [TestCase("eye")] // word-final silent e keeps "eye" at one nucleus
        [TestCase("bye")]
        [TestCase("make")]
        [TestCase("gave")]
        [TestCase("fire")] // "re" after a vowel is NOT the syllabic a|cre case
        [TestCase("hour")]
        [TestCase("know")]
        [TestCase("strength")]
        [TestCase("loved")] // -ed silent after v
        [TestCase("called")] // -ed silent after l
        [TestCase("goes")] // "oe" is one nucleus, so the -es rule never fires
        [TestCase("times")] // -es silent after m
        [TestCase("clothes")] // th before -es is not a sibilant (unlike wishes)
        [TestCase("league")] // gu + vowel: the u is silent, then the final e is too
        [TestCase("one")]
        [TestCase("once")]
        [TestCase("here")]
        [TestCase("where")]
        [TestCase("dont")] // contractions arrive with the apostrophe stripped
        [TestCase("cant")]
        [TestCase("wont")]
        [TestCase("aint")]
        [TestCase("im")]
        [TestCase("ive")]
        [TestCase("its")]
        [TestCase("youre")]
        [TestCase("theyre")]
        [TestCase("thats")]
        [TestCase("don't")] // and defensively, with it intact
        [TestCase("hmm")] // no vowel at all still counts as one group
        [TestCase("a")]
        [TestCase("&")] // freestyle marker: one sane group, no phantom syllables
        [TestCase("24")] // a digit run is one spoken chunk
        public void OneSyllable(string word)
        {
            Assert.AreEqual(1, Syllabifier.CountSyllables(word), word);
            Assert.IsEmpty(Syllabifier.SplitPoints(word).ToArray(), word);
        }

        // ---- multi-syllable words with pinned split points ----
        // brief-pinned worked example
        [TestCase("probably", new[] { 3, 5 })] // pro|ba|bly
        // boundary-placement basics
        [TestCase("open", new[] { 1 })] // o|pen (V|CV)
        [TestCase("better", new[] { 3 })] // bet|ter (doubled consonant)
        [TestCase("apron", new[] { 1 })] // a|pron (pr onset cluster)
        [TestCase("table", new[] { 2 })] // ta|ble (syllabic C+le)
        [TestCase("little", new[] { 3 })] // lit|tle
        [TestCase("turtle", new[] { 3 })] // tur|tle (C+le where "tl" is no onset)
        [TestCase("acre", new[] { 1 })] // a|cre (syllabic C+re)
        [TestCase("people", new[] { 3 })] // peo|ple (pinned exception)
        // hiatus
        [TestCase("quiet", new[] { 3 })] // qui|et
        [TestCase("radio", new[] { 2, 4 })] // ra|di|o
        [TestCase("video", new[] { 2, 4 })] // vi|de|o
        [TestCase("usual", new[] { 1, 3 })] // u|su|al
        [TestCase("diamond", new[] { 2, 3 })] // di|a|mond
        // -tion / -sion / -cial family (the i fuses, no hiatus)
        [TestCase("nation", new[] { 2 })] // na|tion
        [TestCase("vision", new[] { 2 })] // vi|sion
        [TestCase("special", new[] { 3 })] // spe|cial
        [TestCase("attention", new[] { 2, 5 })] // at|ten|tion
        [TestCase("musician", new[] { 2, 4 })] // mu|si|cian
        [TestCase("religion", new[] { 2, 4 })] // re|li|gion
        // vowel + -ing is always split
        [TestCase("being", new[] { 2 })] // be|ing
        [TestCase("going", new[] { 2 })] // go|ing
        [TestCase("dying", new[] { 2 })] // dy|ing
        [TestCase("saying", new[] { 3 })] // say|ing
        [TestCase("carrying", new[] { 3, 5 })] // car|ry|ing
        // common lyric vocabulary
        [TestCase("baby", new[] { 2 })] // ba|by
        [TestCase("money", new[] { 2 })] // mo|ney
        [TestCase("away", new[] { 1 })] // a|way
        [TestCase("again", new[] { 1 })] // a|gain
        [TestCase("alone", new[] { 1 })] // a|lone
        [TestCase("inside", new[] { 2 })] // in|side
        [TestCase("believe", new[] { 2 })] // be|lieve
        [TestCase("another", new[] { 1, 3 })] // a|no|ther (dictionary an|oth|er; maximal onset here)
        [TestCase("nothing", new[] { 2 })] // no|thing (th digraph onsets; dictionary noth|ing)
        [TestCase("something", new[] { 4 })] // some|thing (pinned exception, medial silent e)
        [TestCase("sometimes", new[] { 4 })] // some|times
        [TestCase("someone", new[] { 4 })] // some|one
        [TestCase("somebody", new[] { 4, 6 })] // some|bo|dy
        [TestCase("somewhere", new[] { 4 })] // some|where
        [TestCase("every", new[] { 2 })] // ev|ery (pinned: sung/Merriam 2; formal ev|er|y is 3)
        [TestCase("everything", new[] { 2, 5 })] // ev|ery|thing
        [TestCase("everyone", new[] { 2, 5 })] // ev|ery|one
        [TestCase("everybody", new[] { 2, 5, 7 })] // ev|ery|bo|dy
        [TestCase("everywhere", new[] { 2, 5 })] // ev|ery|where
        [TestCase("remember", new[] { 2, 5 })] // re|mem|ber
        [TestCase("yesterday", new[] { 2, 6 })] // ye|ster|day (st kept as onset per cluster table; dictionary yes|ter|day)
        [TestCase("beautiful", new[] { 4, 6 })] // beau|ti|ful ("eau" is one nucleus)
        [TestCase("together", new[] { 2, 4 })] // to|ge|ther
        [TestCase("tonight", new[] { 2 })] // to|night
        [TestCase("never", new[] { 2 })] // ne|ver
        [TestCase("heaven", new[] { 3 })] // hea|ven (dictionary heav|en; maximal onset here)
        [TestCase("ocean", new[] { 1 })] // o|cean
        [TestCase("music", new[] { 2 })] // mu|sic
        [TestCase("story", new[] { 3 })] // sto|ry
        [TestCase("crazy", new[] { 3 })] // cra|zy
        [TestCase("body", new[] { 2 })] // bo|dy
        [TestCase("sorry", new[] { 3 })] // sor|ry
        [TestCase("happy", new[] { 3 })] // hap|py
        [TestCase("gonna", new[] { 3 })] // gon|na
        [TestCase("wanna", new[] { 3 })] // wan|na
        [TestCase("power", new[] { 2 })] // po|wer
        [TestCase("flower", new[] { 3 })] // flo|wer
        [TestCase("goodbye", new[] { 4 })] // good|bye (final "ye" keeps its nucleus on the y)
        [TestCase("okay", new[] { 1 })] // o|kay
        [TestCase("ready", new[] { 3 })] // rea|dy
        [TestCase("maybe", new[] { 3 })] // may|be (pinned exception)
        [TestCase("lovely", new[] { 4 })] // love|ly (pinned exception, medial silent e)
        [TestCase("lonely", new[] { 4 })] // lone|ly
        [TestCase("million", new[] { 3 })] // mil|lion (pinned exception; li|on splits)
        [TestCase("create", new[] { 3 })] // cre|ate (pinned exception)
        [TestCase("mountain", new[] { 4 })] // moun|tain
        [TestCase("morning", new[] { 3 })] // mor|ning
        [TestCase("burning", new[] { 3 })] // bur|ning
        [TestCase("darling", new[] { 3 })] // dar|ling
        [TestCase("falling", new[] { 3 })] // fal|ling
        [TestCase("feeling", new[] { 3 })] // fee|ling
        [TestCase("waiting", new[] { 3 })] // wai|ting
        [TestCase("running", new[] { 3 })] // run|ning
        [TestCase("dancing", new[] { 3 })] // dan|cing
        [TestCase("wanted", new[] { 3 })] // wan|ted (-ed after t is a nucleus)
        [TestCase("changes", new[] { 4 })] // chan|ges (-es after g is a nucleus)
        [TestCase("wishes", new[] { 2 })] // wi|shes (sh onsets; dictionary wish|es)
        [TestCase("memories", new[] { 2, 4 })] // me|mo|ries ("ies" is one nucleus)
        [TestCase("singin", new[] { 3 })] // sin|gin (dropped-g form does not trigger the -ing rule)
        public void PinnedSplits(string word, int[] expected)
        {
            Assert.AreEqual(expected, Syllabifier.SplitPoints(word).ToArray(), word);
            Assert.AreEqual(expected.Length + 1, Syllabifier.CountSyllables(word), word);
        }

        [Test]
        public void EmptyAndNull()
        {
            Assert.AreEqual(0, Syllabifier.CountSyllables(""));
            Assert.IsEmpty(Syllabifier.SplitPoints("").ToArray());
            Assert.IsEmpty(Syllabifier.SplitPoints("", 5).ToArray());
        }

        [Test]
        public void UppercaseIsFolded()
        {
            Assert.AreEqual(new[] { 3, 5 }, Syllabifier.SplitPoints("PROBABLY").ToArray());
            Assert.AreEqual(new[] { 3 }, Syllabifier.SplitPoints("Quiet").ToArray());
            Assert.AreEqual(2, Syllabifier.CountSyllables("People")); // exception table hits case-insensitively
        }

        [Test]
        public void DigitRunsAreTheirOwnSyllable()
        {
            Assert.AreEqual(new[] { 1, 2 }, Syllabifier.SplitPoints("b2b").ToArray());
            Assert.AreEqual(new[] { 3, 6 }, Syllabifier.SplitPoints("abc123def").ToArray());
            Assert.AreEqual(1, Syllabifier.CountSyllables("1000"));
        }

        [Test]
        public void PunctuationNeverStartsASyllable()
        {
            // the apostrophe is transparent: "don't" is one group, not don|t.
            Assert.AreEqual(1, Syllabifier.CountSyllables("don't"));
            Assert.AreEqual(new[] { 3 }, Syllabifier.SplitPoints("singin'").ToArray());
        }

        // ---- forcedCount reconciliation ----

        [Test]
        public void ForcedMatchingNaturalIsIdentity()
        {
            Assert.AreEqual(new[] { 3, 5 }, Syllabifier.SplitPoints("probably", 3).ToArray());
            Assert.AreEqual(new[] { 2 }, Syllabifier.SplitPoints("believe", 2).ToArray());
        }

        [Test]
        public void ForcedBelowNaturalMergesWeakestBoundary()
        {
            // pro(3) ba(2) bly(3): both merges make a 5-char group, leftmost boundary goes.
            Assert.AreEqual(new[] { 5 }, Syllabifier.SplitPoints("probably", 2).ToArray());

            // beau(4) ti(2) ful(3): merging ti into ful (5) beats merging beau+ti (6).
            Assert.AreEqual(new[] { 4 }, Syllabifier.SplitPoints("beautiful", 2).ToArray());

            Assert.IsEmpty(Syllabifier.SplitPoints("beautiful", 1).ToArray());
            Assert.IsEmpty(Syllabifier.SplitPoints("probably", 0).ToArray()); // below 1 clamps to 1
        }

        [Test]
        public void ForcedAboveNaturalAddsSplitsAtVowelConsonantEdges()
        {
            // natural 1 syllable; the added split lands on the vowel-to-consonant edge, not midpoint.
            Assert.AreEqual(new[] { 2 }, Syllabifier.SplitPoints("fire", 2).ToArray()); // fi|re
            Assert.AreEqual(new[] { 2 }, Syllabifier.SplitPoints("make", 2).ToArray()); // ma|ke
        }

        [Test]
        public void OverForcingDegradesToOneCharGroups()
        {
            // a 3-letter word forced to 5 syllables can only produce 3 groups.
            var splits = Syllabifier.SplitPoints("cat", 5);
            Assert.AreEqual(new[] { 1, 2 }, splits.ToArray());
        }

        [Test]
        public void ForcedInvariantsHoldAcrossTheCorpus()
        {
            string[] corpus =
            {
                "a", "the", "fire", "cat", "probably", "beautiful", "yesterday", "everything",
                "people", "goodbye", "don't", "b2b", "abc123def", "&", "hmm", "quiet", "little",
            };

            foreach (string word in corpus)
            {
                for (int forced = 0; forced <= word.Length + 2; forced++)
                {
                    var splits = Syllabifier.SplitPoints(word, forced);

                    // strictly ascending, all interior, no duplicates.
                    Assert.IsTrue(splits.SequenceEqual(splits.Distinct().OrderBy(x => x)), $"{word} forced {forced}");
                    Assert.IsTrue(splits.All(x => x >= 1 && x <= word.Length - 1), $"{word} forced {forced}");

                    // group count is exactly min(max(forced, 1), length).
                    int expectedGroups = System.Math.Min(System.Math.Max(forced, 1), word.Length);
                    Assert.AreEqual(expectedGroups, splits.Count + 1, $"{word} forced {forced}");
                }
            }
        }

        [Test]
        public void NaturalInvariantsHoldForArbitraryJunk()
        {
            string[] junk = { "&", "''", "!?", "a&b", "'ere", "rock'n'roll", "y'all", "1,000", "Mr.", "co2" };

            foreach (string word in junk)
            {
                var splits = Syllabifier.SplitPoints(word);

                Assert.IsTrue(splits.SequenceEqual(splits.Distinct().OrderBy(x => x)), word);
                Assert.IsTrue(splits.All(x => x >= 1 && x <= word.Length - 1), word);
                Assert.GreaterOrEqual(Syllabifier.CountSyllables(word), 1, word);
            }
        }
    }
}
