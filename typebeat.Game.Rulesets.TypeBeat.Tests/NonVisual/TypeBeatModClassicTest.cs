// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using typebeat.Game.Graphics;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The synthetic "CL" Classic SYSTEM MARK (backlog 398): it must RESOLVE (so a stored marked
    /// score renders "Classic" rather than UnknownMod), it must NOT be selectable, it must stay
    /// RANKED (a marked play keeps its place, only paid less), and it must price pp at 0.95x so the
    /// client's re-derived reading matches the server's stored number.
    /// </summary>
    [TestFixture]
    public class TypeBeatModClassicTest
    {
        [Test]
        public void ResolvesToClassicWithTheBaseIcon()
        {
            var ruleset = new TypeBeatRuleset();

            // What every stored score flows through when its acronym is read.
            Mod? resolved = ruleset.CreateModFromAcronym("CL");

            Assert.That(resolved, Is.InstanceOf<TypeBeatModClassic>());

            var mod = (TypeBeatModClassic)resolved!;

            Assert.Multiple(() =>
            {
                Assert.That(mod.Name, Is.EqualTo("Classic"));
                Assert.That(mod.Acronym, Is.EqualTo("CL"));
                Assert.That(mod.Icon, Is.EqualTo(OsuIcon.ModClassic), "the same icon osu's own Classic badge uses");
                Assert.That(mod.Ranked, Is.True, "a marked play keeps its place; it is only paid less");
            });
        }

        [Test]
        public void IsASystemMark_NotPlayerSelectable()
        {
            var mod = new TypeBeatModClassic();

            Assert.Multiple(() =>
            {
                // ModSelectOverlay builds columns for the five player-facing types only and marks
                // every System mod invalid for selection; UserPlayable is false on top of that.
                Assert.That(mod.Type, Is.EqualTo(ModType.System));
                Assert.That(mod.UserPlayable, Is.False);
            });

            // And it is absent from every player-facing column the overlay does build.
            var ruleset = new TypeBeatRuleset();

            var selectable =
                from type in new[] { ModType.DifficultyReduction, ModType.DifficultyIncrease, ModType.Conversion, ModType.Automation, ModType.Fun }
                from candidate in ruleset.GetModsFor(type)
                select candidate.Acronym;

            Assert.That(selectable, Does.Not.Contain("CL"), "CL must never appear in the mod picker");
        }

        [Test]
        public void PricesPpAtPointNineFive()
        {
            var mod = new TypeBeatModClassic();

            Assert.That(PerformancePoints.ModMultiplier(new Mod[] { mod }, 500), Is.EqualTo(0.95).Within(1e-12));

            // It stacks multiplicatively like every other arm, and the server's two tables carry the
            // same 0.95 (Scoring/PerformancePoints.cs and Scoring/ModMultiplier.cs).
            var pair = new Mod[] { mod, new TypeBeatModFletcher() };

            Assert.That(PerformancePoints.ModMultiplier(pair, 500), Is.EqualTo(0.95 * 1.02).Within(1e-12));
        }
    }
}
