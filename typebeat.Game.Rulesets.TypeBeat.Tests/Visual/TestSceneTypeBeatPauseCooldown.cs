// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Audio;
using osu.Framework.Bindables;
using osu.Framework.Input.Events;
using osu.Framework.Input.States;
using osu.Framework.Testing;
using osu.Framework.Utils;
using typebeat.Game.Beatmaps;
using typebeat.Game.Input.Bindings;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Play;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// The 1000 ms anti-spam pause cooldown (<see cref="Player.PauseCooldownDuration"/>) gates a HUMAN
    /// HAND, so it is measured on REAL time, not on gameplay time (backlog 267).
    ///
    /// <para>The report: under the Conductor (<see cref="TypeBeatModPuppeteer"/>) the gameplay rate parks
    /// at its floor <see cref="TypeBeatModPuppeteer.V_EPSILON"/> (1/512, never exactly zero by
    /// construction, since a stopped track flushes audibly). A cooldown measured on the gameplay clock
    /// then takes 512 real seconds to expire, and since every pause, quit and escape funnels through
    /// <c>PerformExitWithConfirmation</c> which calls <see cref="Player.Pause"/>, the whole lot is
    /// silently inert for those 512 seconds. The mod is only the discovery vehicle: the tests below pin
    /// the rate directly through <see cref="GameplayClockContainer.AdjustmentsFromMods"/>, which is
    /// exactly what the mod writes into, so no mod is loaded and the repro is deterministic.</para>
    /// </summary>
    public partial class TestSceneTypeBeatPauseCooldown : PlayerTestScene
    {
        /// <summary>
        /// Slack past <see cref="Player.PauseCooldownDuration"/> so the wait cannot end on the boundary.
        /// </summary>
        private const double wait_slack_ms = 150;

        protected override Ruleset CreatePlayerRuleset() => new TypeBeatRuleset();

        protected override TestPlayer CreatePlayer(Ruleset ruleset) => new ExposedPlayer();

        private ExposedPlayer exposedPlayer => (ExposedPlayer)Player;

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            // One very long line, so gameplay neither completes nor seals for the duration of a test.
            var line = new LyricLine
            {
                RawText = "ab",
                StartTime = 0,
                EndTime = 600000,
                SingEndTime = 300000,
                Units = new[] { new TimedUnit { Text = "ab", StartTime = 0, EndTime = 300000 } },
            };

            var beatmap = new Beatmap
            {
                HitObjects = new List<Rulesets.Objects.HitObject>
                {
                    new TypeBeatHitObject
                    {
                        StartTime = 0,
                        LineIndex = 0,
                        Line = line,
                        Granularity = TimingGranularity.Line,
                    },
                },
            };

            beatmap.BeatmapInfo.Ruleset = ruleset;
            return beatmap;
        }

        /// <summary>
        /// The bug itself. With the gameplay rate parked at the Conductor's floor, one second of REAL
        /// time must retire the cooldown, even though the gameplay clock has advanced by about two
        /// milliseconds over that same second.
        /// </summary>
        [Test]
        public void TestCooldownExpiresOnRealTimeAtConductorFloorRate()
        {
            double realTimeAtResume = 0;
            double gameplayTimeAtResume = 0;

            waitForPlaying();
            parkRateAtConductorFloor();

            AddAssert("first pause taken", () => Player.Pause());
            AddUntilStep("gameplay clock stopped", () => !Player.GameplayClockContainer.IsRunning);

            AddStep("resume", () =>
            {
                Player.Resume();
                realTimeAtResume = exposedPlayer.RealTime;
                gameplayTimeAtResume = Player.GameplayClockContainer.CurrentTime;
            });

            waitForPlaying();

            AddUntilStep("wait out the cooldown in real time",
                () => exposedPlayer.RealTime > realTimeAtResume + exposedPlayer.CooldownDuration + wait_slack_ms);

            // Non-vacuity: over that same wall second the gameplay clock advanced by roughly
            // 1150 / 512 ms, so a cooldown measured on the gameplay clock would still be active here
            // and the two assertions below would both fail (they did, before the fix).
            AddAssert("gameplay clock barely advanced",
                () => Player.GameplayClockContainer.CurrentTime - gameplayTimeAtResume < exposedPlayer.CooldownDuration);

            AddAssert("cooldown expired", () => !exposedPlayer.PauseCooldownActive);
            AddAssert("second pause taken", () => Player.Pause());
        }

        /// <summary>
        /// The inverse guard: at rate 1.0 the cooldown still BITES immediately after a resume, so moving
        /// it onto the real-time axis did not delete the anti-spam.
        /// </summary>
        /// <remarks>
        /// The resume and the second pause attempt share one step deliberately. A test step here costs a
        /// few hundred milliseconds of REAL time, so spreading them over separate steps would race the
        /// (correctly) real-time cooldown rather than test it.
        /// </remarks>
        [Test]
        public void TestCooldownStillBitesImmediatelyAfterResume()
        {
            bool cooldownActive = false;
            bool secondPauseRefused = false;

            waitForPlaying();

            AddAssert("first pause taken", () => Player.Pause());
            AddUntilStep("gameplay clock stopped", () => !Player.GameplayClockContainer.IsRunning);

            AddStep("resume, then immediately re-pause", () =>
            {
                Player.Resume();
                cooldownActive = exposedPlayer.PauseCooldownActive;
                secondPauseRefused = !Player.Pause();
            });

            AddAssert("cooldown was active on resuming", () => cooldownActive);
            AddAssert("second pause was refused", () => secondPauseRefused);
            AddUntilStep("gameplay still running", () => Player.GameplayClockContainer.IsRunning);
        }

        /// <summary>
        /// Cheap insurance from the same audit: the quick-exit hold runs on the ambient clock, not the
        /// gameplay one, so it still completes within its 200 ms activation delay at the floor rate.
        /// The overlay's <c>Action</c> is swapped for a flag so the assertion is about the hold
        /// completing, not about the screen tearing down.
        /// </summary>
        [Test]
        public void TestQuickExitHoldStillCompletesAtConductorFloorRate()
        {
            bool fired = false;
            HotkeyExitOverlay exitOverlay = null!;

            waitForPlaying();
            parkRateAtConductorFloor();

            AddStep("hook the quick exit hold", () =>
            {
                fired = false;
                exitOverlay = Player.ChildrenOfType<HotkeyExitOverlay>().Single();
                exitOverlay.Action = () => fired = true;
            });

            AddStep("begin the hold", () => exitOverlay.OnPressed(new KeyBindingPressEvent<GlobalAction>(new InputState(), GlobalAction.QuickExit)));

            AddUntilStep("hold completed", () => fired);
        }

        private void waitForPlaying()
            => AddUntilStep("gameplay playing", () => Player.GameplayClockContainer.IsRunning
                                                      && Player.PlayingState.Value == LocalUserPlayingState.Playing);

        private void parkRateAtConductorFloor()
        {
            AddStep("park the rate at the Conductor floor", () => Player.GameplayClockContainer.AdjustmentsFromMods
                                                                        .AddAdjustment(AdjustableProperty.Frequency, new BindableDouble(TypeBeatModPuppeteer.V_EPSILON)));

            AddAssert("true gameplay rate is the floor",
                () => Precision.AlmostEquals(Player.GameplayClockContainer.GetTrueGameplayRate(), TypeBeatModPuppeteer.V_EPSILON, 1e-9));
        }

        private partial class ExposedPlayer : TestPlayer
        {
            public ExposedPlayer()
                : base(allowPause: true, showResults: false)
            {
            }

            /// <summary>
            /// The very clock the cooldown is measured on: this screen's own, which is the game-wide
            /// framed clock and is not touched by <see cref="GameplayClockContainer.AdjustmentsFromMods"/>.
            /// </summary>
            public double RealTime => Time.Current;

            public double CooldownDuration => PauseCooldownDuration;
        }
    }
}
