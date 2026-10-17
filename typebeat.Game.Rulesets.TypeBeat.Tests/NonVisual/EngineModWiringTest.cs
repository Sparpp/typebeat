// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Every mod's engine settings are written in ONE place (IApplicableToTypingEngine.ApplyToEngine),
// and every engine factory calls it: the live DrawableTypeBeatRuleset.createEngine, the replay
// scorer's CreateEngine, and through the scorer the Puppeteer transform's scratch engine. This file
// pins the property that buys: for every mod the ruleset offers, alone and in every legal pair, and
// for the rate x Easy / Hard Rock stacks, the engine a live play builds and the engine a rescore of
// that play's replay builds agree on EVERY public flag and number, compared by reflection rather
// than from a hand-written list, so a new engine property or a new mod is covered the day it lands.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using osu.Framework.Localisation;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.ControlPoints;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Rulesets.UI;
using typebeat.Game.Utils;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class EngineModWiringTest
    {
        /// <summary>A non-default rate, so a factory that read a hardcoded 1.50 / 0.75 is caught.</summary>
        private const double odd_speed_up = 1.37;

        private const double odd_slow_down = 0.62;

        [TestCaseSource(nameof(stacks))]
        public void LiveAndRescoredEnginesAgreeOnEveryPublicProperty(Func<Mod[]> stack)
        {
            var mismatches = compare(stack());

            Assert.That(mismatches, Is.Empty);
        }

        /// <summary>
        /// The same stacks with NO header fed to the rescore, which is what a bare or autoplay
        /// stream re-derives under. Every property no era bit carries still agrees with the live
        /// engine (the mods are the whole of it); every property an era bit carries is the engine's
        /// documented default, except where a mod writes it first, and Gatekeeper's wrong-input
        /// model is the only such write that differs from the default (Hard Rock's point targets
        /// and Fletcher's pinned caret ARE the defaults).
        /// </summary>
        [TestCaseSource(nameof(stacks))]
        public void AHeaderlessRescoreTakesTheModsAndTheEraDefaults(Func<Mod[]> stack)
        {
            var mods = stack();
            var (live, _) = build(mods);
            var map = cakeMap();
            var headerless = TypeBeatReplayScorer.CreateEngine(map, lineObjects(map), mods, RateWindowRule.ScaledByRate);
            var bare = TypeBeatReplayScorer.CreateEngine(map, lineObjects(map), Array.Empty<Mod>(), RateWindowRule.ScaledByRate);
            var eraCarried = eraCarriedProperties();
            var mismatches = new List<string>();

            foreach (var property in comparedProperties())
            {
                object? actual = property.GetValue(headerless);
                object? expected;

                if (!eraCarried.Contains(property.Name))
                    expected = property.GetValue(live);
                else if (property.Name == nameof(TypingEngine.AllowWrongInput) && mods.Any(m => m is TypeBeatModGatekeeper))
                    expected = false;
                else
                    expected = property.GetValue(bare);

                if (!EqualityComparer<object?>.Default.Equals(expected, actual))
                    mismatches.Add($"{property.Name}: expected {expected} vs headerless {actual}");
            }

            Assert.That(mismatches, Is.Empty);
        }

        /// <summary>
        /// The headerless rule, pinned on its own because it is a choice: the mods write first and a
        /// CONFIG frame overwrites the eras it carries, so a Gatekeeper stream with no CONFIG frame
        /// (bare, or opening on a lone extended header the way autoplay's does) keeps the mod's
        /// strict wrong-input model rather than the engine default, and one with a CONFIG frame takes
        /// the frame's.
        /// </summary>
        [Test]
        public void AGatekeeperStreamWithNoConfigFrameKeepsTheModsWrongInputModel()
        {
            var map = cakeMap();
            var gatekeeper = new Mod[] { new TypeBeatModGatekeeper() };

            Assert.That(TypeBeatReplayScorer.CreateEngine(map, lineObjects(map), Array.Empty<Mod>(), RateWindowRule.ScaledByRate).AllowWrongInput, Is.True,
                "the engine default the mod overrides");

            var engine = TypeBeatReplayScorer.CreateEngine(map, lineObjects(map), gatekeeper, RateWindowRule.ScaledByRate);

            Assert.That(engine.AllowWrongInput, Is.False, "bare stream");

            ReplayEngineFeed.Apply(engine, TypeBeatReplayFrame.CreateExtendedConfigFrame(0, inputEra2: true));
            Assert.That(engine.AllowWrongInput, Is.False, "lone extended header");

            ReplayEngineFeed.Apply(engine, TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true));
            Assert.That(engine.AllowWrongInput, Is.True, "a CONFIG frame decides it for a recorded run");
        }

        /// <summary>
        /// The comparison is not vacuous: it reads the properties every engine-applicable mod writes,
        /// and the stacks really do move them.
        /// </summary>
        [Test]
        public void TheComparisonCoversTheModWrittenProperties()
        {
            var names = comparedProperties().Select(p => p.Name).ToList();

            foreach (string name in new[]
                     {
                         nameof(TypingEngine.WindowScale), nameof(TypingEngine.WordShelter), nameof(TypingEngine.HardRockFromMod),
                         nameof(TypingEngine.RushCapExempt), nameof(TypingEngine.MashingEnabled), nameof(TypingEngine.AnyOrderWithinWord),
                         nameof(TypingEngine.FlexibleCaretFromMod), nameof(TypingEngine.FletcherEnabled), nameof(TypingEngine.FlexibleLineSnap),
                         nameof(TypingEngine.AllowWrongInput), nameof(TypingEngine.SyllableTiming), nameof(TypingEngine.UnhalvedHardRockWindows),
                     })
            {
                Assert.That(names, Does.Contain(name));
            }

            var (plainLive, _) = build(Array.Empty<Mod>());
            var (easyLive, easyRescored) = build(new Mod[] { new TypeBeatModEasy(), new TypeBeatModDoubleTime { SpeedChange = { Value = odd_speed_up } } });

            Assert.That(plainLive.WindowScale, Is.EqualTo(1));
            Assert.That(easyLive.WindowScale, Is.EqualTo(TypeBeatModEasy.WINDOW_SCALE * odd_speed_up).Within(1e-12));
            Assert.That(easyRescored.WindowScale, Is.EqualTo(easyLive.WindowScale));
            Assert.That(easyRescored.WordShelter, Is.True);
        }

        /// <summary>
        /// The failure this guards against, staged: a mod that writes the engine from the drawable
        /// ruleset seam alone reaches a live play and never a rescore, and the comparison says so.
        /// </summary>
        [Test]
        public void AModThatWritesTheEngineOnlyFromTheDrawableSeamIsCaught()
        {
            var mismatches = compare(new Mod[] { new DrawableSeamOnlyMod() });

            Assert.That(mismatches, Has.Some.Contains(nameof(TypingEngine.MashingEnabled)));
        }

        /// <summary>
        /// Every rate mod rides the scorer's pre-150 era switch, so each has to be a
        /// <see cref="ModRateAdjust"/> as well as an engine mod; and the switch skips exactly them.
        /// </summary>
        [Test]
        public void TheUnscaledRateEraSkipsTheRateModsAndNothingElse()
        {
            var mods = new Mod[] { new TypeBeatModEasy(), new TypeBeatModDoubleTime { SpeedChange = { Value = odd_speed_up } } };
            var map = cakeMap();

            var scaled = TypeBeatReplayScorer.CreateEngine(map, lineObjects(map), mods, RateWindowRule.ScaledByRate);
            var unscaled = TypeBeatReplayScorer.CreateEngine(map, lineObjects(map), mods, RateWindowRule.Unscaled);

            Assert.That(scaled.WindowScale, Is.EqualTo(TypeBeatModEasy.WINDOW_SCALE * odd_speed_up).Within(1e-12));
            Assert.That(unscaled.WindowScale, Is.EqualTo(TypeBeatModEasy.WINDOW_SCALE));
            Assert.That(unscaled.WordShelter, Is.True);
        }

        #region Harness

        private static IEnumerable<TestCaseData> stacks()
        {
            var all = allMods();

            yield return named(Array.Empty<Mod>);

            for (int i = 0; i < all.Count; i++)
            {
                int a = i;
                yield return named(() => new[] { allMods()[a] });
            }

            for (int i = 0; i < all.Count; i++)
            {
                for (int j = i + 1; j < all.Count; j++)
                {
                    if (!ModUtils.CheckCompatibleSet(new[] { all[i], all[j] }) || neverPlayedLive(all[i], all[j]))
                        continue;

                    int a = i, b = j;
                    yield return named(() =>
                    {
                        var fresh = allMods();
                        return new[] { fresh[a], fresh[b] };
                    });
                }
            }

            // The rate x window stacks at rates nobody hardcodes, alone and with every third mod
            // that can join them.
            Func<Mod>[] rates =
            {
                () => new TypeBeatModDoubleTime { SpeedChange = { Value = odd_speed_up } },
                () => new TypeBeatModNightcore { SpeedChange = { Value = odd_speed_up } },
                () => new TypeBeatModHalfTime { SpeedChange = { Value = odd_slow_down } },
                () => new TypeBeatModDaycore { SpeedChange = { Value = odd_slow_down } },
            };

            Func<Mod>[] windows = { () => new TypeBeatModEasy(), () => new TypeBeatModHardRock() };

            foreach (var rate in rates)
            {
                foreach (var window in windows)
                {
                    var r = rate;
                    var w = window;

                    yield return named(() => new[] { r(), w() });

                    for (int k = 0; k < all.Count; k++)
                    {
                        if (!ModUtils.CheckCompatibleSet(new[] { r(), w(), all[k] }))
                            continue;

                        int c = k;
                        yield return named(() => new[] { r(), w(), allMods()[c] });
                    }
                }
            }
        }

        /// <summary>
        /// Fletcher (FC) with the retired FT: two opposite carets, and a stack no play can hold. FT
        /// is a System mod nobody can select, and FC was introduced when FT was retired, so no stored
        /// score carries both; a rescore of it would take the caret from FT's mod fact (OR-ed in by
        /// the CONFIG frame) while a live engine took FC's pin.
        /// </summary>
        private static bool neverPlayedLive(Mod a, Mod b)
            => (a is TypeBeatModFletcher && b is TypeBeatModLegacyFletcher) || (a is TypeBeatModLegacyFletcher && b is TypeBeatModFletcher);

        /// <summary>
        /// Every value-like engine property some era bit moves, read off the bit table by writing
        /// each bit both ways on a fresh engine: the flags themselves and whatever is derived from
        /// them.
        /// </summary>
        private static HashSet<string> eraCarriedProperties()
        {
            var map = cakeMap();
            var carried = new HashSet<string>();

            foreach (var bit in ReplayEraBits.ALL.Where(b => b.IsEngineFlag))
            {
                var off = TypeBeatReplayScorer.CreateEngine(map, lineObjects(map), Array.Empty<Mod>(), RateWindowRule.ScaledByRate);
                var on = TypeBeatReplayScorer.CreateEngine(map, lineObjects(map), Array.Empty<Mod>(), RateWindowRule.ScaledByRate);

                bit.Write(off, false);
                bit.Write(on, true);

                foreach (var property in comparedProperties())
                {
                    if (!EqualityComparer<object?>.Default.Equals(property.GetValue(off), property.GetValue(on)))
                        carried.Add(property.Name);
                }
            }

            return carried;
        }

        private static TestCaseData named(Func<Mod[]> stack)
        {
            var sample = stack();
            string label = sample.Length == 0
                ? "NoMod"
                : string.Join("+", sample.Select(m => m is ModRateAdjust rate ? $"{m.Acronym}{rate.SpeedChange.Value:0.00}" : m.Acronym));

            return new TestCaseData(stack).SetArgDisplayNames(label);
        }

        private static List<Mod> allMods() => new TypeBeatRuleset().CreateAllMods().ToList();

        /// <summary>
        /// Build both engines and list every public property they disagree on, as "name: live vs
        /// rescored".
        /// </summary>
        private static List<string> compare(Mod[] mods)
        {
            var (live, rescored) = build(mods);
            var mismatches = new List<string>();

            foreach (var property in comparedProperties())
            {
                object? expected = property.GetValue(live);
                object? actual = property.GetValue(rescored);

                if (!EqualityComparer<object?>.Default.Equals(expected, actual))
                    mismatches.Add($"{property.Name}: live {expected} vs rescored {actual}");
            }

            return mismatches;
        }

        /// <summary>
        /// The live engine exactly as gameplay builds it (the drawable ruleset over the beatmap and
        /// mods, then handed the mods the framework's <c>applyRulesetMods</c> would hand it), and the
        /// engine a rescore of that play builds: the scorer's factory, fed the two header frames the
        /// live recorder would have written for it.
        /// </summary>
        private static (TypingEngine Live, TypingEngine Rescored) build(Mod[] mods)
        {
            var map = cakeMap();

            using var drawable = new DrawableTypeBeatRuleset(new TypeBeatRuleset(), map, mods);

            foreach (var mod in mods.OfType<IApplicableToDrawableRuleset<TypeBeatHitObject>>())
                mod.ApplyToDrawableRuleset(drawable);

            var live = drawable.Engine;

            var rescored = TypeBeatReplayScorer.CreateEngine(map, lineObjects(map), mods, RateWindowRule.ScaledByRate);
            var (config, extended) = TypeBeatReplayRecorder.HeaderFramesFor(live, 0, wallClockFrames: false);

            ReplayEngineFeed.Apply(rescored, config);
            ReplayEngineFeed.Apply(rescored, extended);

            return (live, rescored);
        }

        /// <summary>
        /// Every public instance property of the engine whose value is a flag, a number or an enum.
        /// Read off the type, so a property added tomorrow is compared without anyone listing it.
        /// </summary>
        private static IEnumerable<PropertyInfo> comparedProperties() =>
            typeof(TypingEngine).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && isValueLike(p.PropertyType))
                                .OrderBy(p => p.Name);

        private static bool isValueLike(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            return type == typeof(bool) || type == typeof(double) || type == typeof(int) || type.IsEnum;
        }

        private static List<TypeBeatHitObject> lineObjects(TypeBeatBeatmap map)
            => map.HitObjects.OfType<TypeBeatHitObject>().OrderBy(h => h.LineIndex).ToList();

        /// <summary>Two lines, one subdivided word each, so syllable and word rules have something to read.</summary>
        private static TypeBeatBeatmap cakeMap()
        {
            var map = new TypeBeatBeatmap();

            map.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = 0,
                LineIndex = 0,
                Granularity = TimingGranularity.Line,
                Line = new LyricLine
                {
                    RawText = "cake day",
                    StartTime = 0,
                    EndTime = 6000,
                    SingEndTime = 5000,
                    Units = new[]
                    {
                        new TimedUnit { Text = "cake", StartTime = 1000, EndTime = 3000 },
                        new TimedUnit { Text = "day", StartTime = 3000, EndTime = 5000 },
                    },
                },
            });

            map.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = 7000,
                LineIndex = 1,
                Granularity = TimingGranularity.Line,
                Line = new LyricLine
                {
                    RawText = "sing",
                    StartTime = 7000,
                    EndTime = 12000,
                    SingEndTime = 10000,
                    Units = new[] { new TimedUnit { Text = "sing", StartTime = 8000, EndTime = 10000 } },
                },
            });

            foreach (var hitObject in map.HitObjects)
                hitObject.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty(), CancellationToken.None);

            return map;
        }

        /// <summary>The staged mistake: an engine write on the drawable seam with no <see cref="IApplicableToTypingEngine"/>.</summary>
        private class DrawableSeamOnlyMod : Mod, IApplicableToDrawableRuleset<TypeBeatHitObject>
        {
            public override string Name => "Drawable seam only";

            public override string Acronym => "ZZ";

            public override LocalisableString Description => "Test mod.";

            public void ApplyToDrawableRuleset(DrawableRuleset<TypeBeatHitObject> drawableRuleset)
                => ((DrawableTypeBeatRuleset)drawableRuleset).Engine.MashingEnabled = true;
        }

        #endregion
    }
}
