// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Audio.Sample;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.UI
{
    /// <summary>
    /// KEYBOARD HITSOUNDS: a keyboard click once per syllable (on the key that types its first
    /// character) and on every hold tick held through (<see cref="KeyboardHitsoundCues"/>), live and in
    /// replays, while the "Keyboard hitsounds" setting is on (off by default).
    ///
    /// <para>Each key has its one sample (<see cref="SampleFor"/>), chosen by where the key sits on a
    /// keyboard: the letter rows a different click each, the number row the thinnest, punctuation the
    /// snappy accent, the space bar the deep thunk, Backspace the soft thud and Enter the soft tick.
    /// <c>key-invalid</c> is the one sample of the set no key uses.</para>
    ///
    /// <para>One gesture can be several keys at once (Ctrl+Backspace erases a word one Backspace at a
    /// time; an IME commit types several characters), so a sample already started within
    /// <see cref="REPEAT_GAP_MS"/> is not started again: it would only stack into one loud click.</para>
    /// </summary>
    public partial class KeyboardHitsounds : Component
    {
        /// <summary>The shortest gap between two starts of the same sample.</summary>
        public const double REPEAT_GAP_MS = 25;

        private const string top_row = "qwertyuiop";
        private const string home_row = "asdfghjkl";
        private const string bottom_row = "zxcvbnm";

        private readonly KeyboardHitsoundCues cues;
        private readonly Bindable<bool> enabled = new Bindable<bool>();
        private readonly Dictionary<string, Sample?> samples = new Dictionary<string, Sample?>();
        private readonly Dictionary<string, double> lastStarted = new Dictionary<string, double>();

        /// <summary>The sample last started (test support).</summary>
        internal string? LastPlayed { get; private set; }

        /// <summary>How many samples this has started (test support).</summary>
        internal int PlayCount { get; private set; }

        public KeyboardHitsounds(TypingEngine engine)
        {
            cues = new KeyboardHitsoundCues(engine);
        }

        /// <summary>The sample a gameplay key plays, by where the key sits on a keyboard. Pure.</summary>
        public static string SampleFor(TypedKey key)
        {
            switch (key.Kind)
            {
                case TypedKeyKind.Backspace:
                    return "Keyboard/key-delete";

                case TypedKeyKind.Enter:
                    return "Keyboard/key-movement";
            }

            char c = char.ToLowerInvariant(key.Character);

            if (c == ' ')
                return "Keyboard/key-confirm";

            if (top_row.Contains(c))
                return "Keyboard/key-press-1";

            if (home_row.Contains(c))
                return "Keyboard/key-press-2";

            if (bottom_row.Contains(c))
                return "Keyboard/key-press-3";

            if (c >= '0' && c <= '9')
                return "Keyboard/key-press-4";

            // A letter off the QWERTY rows (a Polyglot key such as a hangul jamo) clicks like a plain
            // letter; everything else is punctuation or a symbol.
            return char.IsLetter(c) ? "Keyboard/key-press-1" : "Keyboard/key-caps";
        }

        [BackgroundDependencyLoader(true)]
        private void load(AudioManager? audio, TypeBeatRulesetConfigManager? config)
        {
            config?.BindWith(TypeBeatRulesetSetting.KeyboardHitsounds, enabled);

            if (audio == null)
                return;

            foreach (string name in new[]
                     {
                         "Keyboard/key-press-1", "Keyboard/key-press-2", "Keyboard/key-press-3", "Keyboard/key-press-4",
                         "Keyboard/key-caps", "Keyboard/key-confirm", "Keyboard/key-delete", "Keyboard/key-movement",
                     })
                samples[name] = audio.Samples.Get(name);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            cues.Cued += onCued;
        }

        private void onCued(string name)
        {
            if (!enabled.Value)
                return;

            double now = Time.Current;

            if (lastStarted.TryGetValue(name, out double last) && Math.Abs(now - last) < REPEAT_GAP_MS)
                return;

            lastStarted[name] = now;
            LastPlayed = name;
            PlayCount++;

            if (samples.TryGetValue(name, out var sample))
                sample?.Play();
        }

        protected override void Dispose(bool isDisposing)
        {
            cues.Dispose();
            base.Dispose(isDisposing);
        }
    }
}
