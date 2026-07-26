// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Audio;
using osu.Framework.Bindables;
using typebeat.Game.Rulesets.Mods;

namespace typebeat.Game.Rulesets.TypeBeat.Mods
{
    /// <summary>
    /// Muted: the song plays at zero volume, so the only thing left to type against is the caret.
    /// A flex mod, not a difficulty knob. Nothing about the map, the engine or the judgement changes;
    /// the mod adds a single volume adjustment to the gameplay track and otherwise does nothing, which
    /// is why it is ranked at a flat 1.0x (it is simply absent from
    /// <see cref="Scoring.TypeBeatScoreMultiplierCalculator"/>). The point is that "MU" rides along on
    /// the score's mod list, so results screens and leaderboards show you did it deaf.
    ///
    /// Deliberately NOT lazer's <c>ModMuted&lt;T&gt;</c>: that one ramps volume with combo, adds a
    /// metronome overlay and hooks the score processor. The user asked for a mod that "just mutes the
    /// song", and hooking the score processor is exactly what the scoring-fidelity invariant (the JS
    /// mirror of <see cref="Gameplay.TypingEngine"/> in the web repo) says to stay away from. So this
    /// derives from the plain abstract <see cref="ModMuted"/> for its shipped metadata (name, "MU"
    /// acronym, icon, Fun type, ranked) and implements <see cref="IApplicableToTrack"/> alone.
    ///
    /// type!beat gameplay has no hitsounds or key samples (the only samples in the ruleset are the
    /// editor's timing ticks, and the editor never applies mods), so muting the track mutes gameplay
    /// entirely; there is no second audio layer to silence.
    ///
    /// Scope of the mute: the adjustment is handed to whatever audio component the host screen offers.
    /// In gameplay that is <c>GameplayClockContainer.AdjustmentsFromMods</c>, which is unbound from the
    /// track when the player screen dies, so menu music returns on exit with no cleanup of our own.
    /// Song select also applies mod track adjustments, so the preview clip is silent while the mod is
    /// selected; that is the same plumbing that makes DT speed up previews, and it is an honest preview
    /// of what you signed up for.
    /// </summary>
    public class TypeBeatModMuted : ModMuted, IApplicableToTrack
    {
        /// <summary>
        /// Multiplies the track's aggregate volume by zero. Held as a field (not a fresh bindable per
        /// call) so the same instance can be applied to several components, for example the song-select
        /// preview and then the gameplay clock, and removed from any of them by reference.
        /// </summary>
        private readonly BindableNumber<double> muteVolume = new BindableDouble();

        public void ApplyToTrack(IAdjustableAudioComponent track) =>
            track.AddAdjustment(AdjustableProperty.Volume, muteVolume);

        /// <summary>
        /// Test/host hook: undoes <see cref="ApplyToTrack"/> on a component. Gameplay never needs this
        /// (the component itself is discarded with the screen), it exists so the round trip is pinnable.
        /// </summary>
        public void RemoveFromTrack(IAdjustableAudioComponent track) =>
            track.RemoveAdjustment(AdjustableProperty.Volume, muteVolume);
    }
}
