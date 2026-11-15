// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Globalization;
using osu.Framework.Bindables;
using osu.Framework.Graphics;

namespace typebeat.Game.Configuration.Presets
{
    /// <summary>
    /// One setting Gameplay presets cover, attached to the live config it is stored in: read and written
    /// as the domain-shaped values <see cref="PresetDomain"/> describes, whatever the stored type is.
    /// Holds (strongly) a bound copy of the config's bindable, so it sees every change from anywhere (the settings
    /// panel, a gameplay hotkey, the in-play visual settings) and writes straight through to the config.
    /// </summary>
    public abstract class LiveGameplaySetting
    {
        public string Key { get; }

        /// <summary>The domain the LATEST schema gives this setting: what a decoded value is snapped into before it is written.</summary>
        public PresetDomain Domain { get; }

        /// <summary>Raised on every change of the stored value.</summary>
        public event Action? Changed;

        protected LiveGameplaySetting(string key, PresetDomain domain)
        {
            Key = key;
            Domain = domain;
        }

        /// <summary>The current value, as a domain value (see <see cref="PresetDomain"/>).</summary>
        public abstract object Value { get; }

        /// <summary>The setting's default, as a domain value: what Minimal holds.</summary>
        public abstract object DefaultValue { get; }

        /// <summary>
        /// Whether the config currently greys this setting out (another setting gates it). Writable so a
        /// preset can lift the gate for one write, exactly as "Reset all settings" does.
        /// </summary>
        public abstract bool Disabled { get; set; }

        /// <summary>Writes a domain value. The caller has made sure the setting is not disabled.</summary>
        public abstract void SetValue(object value);

        /// <summary>Whether the stored value is exactly what writing <paramref name="value"/> would store.</summary>
        public abstract bool Holds(object value);

        /// <summary>The digit the current value encodes as (the default's when the value is unreadable).</summary>
        public int CurrentDigit => Domain.DigitOf(Value) ?? DefaultDigit;

        public int DefaultDigit => Domain.DigitOf(DefaultValue) ?? 0;

        protected void OnChanged() => Changed?.Invoke();

        /// <summary>
        /// Attaches <paramref name="bindable"/> as the setting <paramref name="key"/> in <paramref name="domain"/>,
        /// or returns null when the stored type cannot hold that kind of domain.
        /// </summary>
        /// <param name="key">The setting's schema key.</param>
        /// <param name="domain">The latest schema's domain for it.</param>
        /// <param name="bindable">
        /// A bound copy of the config's bindable that this setting may keep (what
        /// <see cref="INamedSettingSource.FindSetting"/> returns). It is held as is, NOT copied again: a
        /// config links to its copies weakly, so a copy of a copy that nothing else holds would be
        /// collected and silently cut this setting off from the config.
        /// </param>
        public static LiveGameplaySetting? Create(string key, IBindable bindable, PresetDomain domain)
        {
            Type? valueType = valueTypeOf(bindable.GetType());

            if (valueType == null)
                return null;

            switch (domain.Kind)
            {
                case PresetDomainKind.Bool when valueType == typeof(bool):
                    return new BoolSetting(key, domain, (Bindable<bool>)bindable);

                case PresetDomainKind.Enum when valueType.IsEnum:
                    return (LiveGameplaySetting)Activator.CreateInstance(typeof(EnumSetting<>).MakeGenericType(valueType), key, domain, bindable)!;

                case PresetDomainKind.Range when valueType == typeof(double) || valueType == typeof(float) || valueType == typeof(int):
                    return (LiveGameplaySetting)Activator.CreateInstance(typeof(NumberSetting<>).MakeGenericType(valueType), key, domain, bindable)!;

                case PresetDomainKind.Colour when valueType == typeof(string):
                    return new ColourStringSetting(key, domain, (Bindable<string>)bindable);

                case PresetDomainKind.Colour when valueType == typeof(Colour4):
                    return new Colour4Setting(key, domain, (Bindable<Colour4>)bindable);

                default:
                    return null;
            }
        }

        private static Type? valueTypeOf(Type? type)
        {
            for (; type != null; type = type.BaseType)
            {
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Bindable<>))
                    return type.GetGenericArguments()[0];
            }

            return null;
        }

        /// <summary>A stored <c>#rrggbb</c> colour as <c>0xRRGGBB</c>, or null when it is not exactly that.</summary>
        public static int? ParseColour(string? text)
        {
            string value = text?.Trim() ?? string.Empty;

            if (value.Length != 7 || value[0] != '#')
                return null;

            for (int i = 1; i < 7; i++)
            {
                if (!Uri.IsHexDigit(value[i]))
                    return null;
            }

            return int.Parse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        /// <summary><c>0xRRGGBB</c> as the stored spelling: <c>#rrggbb</c>, lowercase.</summary>
        public static string FormatColour(int rgb) => FormattableString.Invariant($"#{rgb & 0xffffff:x6}");

        private abstract class Typed<T> : LiveGameplaySetting
        {
            protected readonly Bindable<T> Bindable;

            protected Typed(string key, PresetDomain domain, Bindable<T> bindable)
                : base(key, domain)
            {
                Bindable = bindable;
                Bindable.ValueChanged += _ => OnChanged();
            }

            public override bool Disabled
            {
                get => Bindable.Disabled;
                set => Bindable.Disabled = value;
            }

            /// <summary>What writing the domain value <paramref name="value"/> stores.</summary>
            protected abstract T ToStored(object value);

            public override void SetValue(object value) => Bindable.Value = ToStored(value);

            public override bool Holds(object value) => EqualityComparer<T>.Default.Equals(Bindable.Value, ToStored(value));
        }

        private sealed class BoolSetting : Typed<bool>
        {
            public BoolSetting(string key, PresetDomain domain, Bindable<bool> bindable)
                : base(key, domain, bindable)
            {
            }

            public override object Value => Bindable.Value;

            public override object DefaultValue => Bindable.Default;

            protected override bool ToStored(object value) => (bool)value;
        }

        private sealed class EnumSetting<T> : Typed<T>
            where T : struct, Enum
        {
            // Activator.CreateInstance hands the bindable over untyped.
            public EnumSetting(string key, PresetDomain domain, IBindable bindable)
                : base(key, domain, (Bindable<T>)bindable)
            {
            }

            public override object Value => Bindable.Value.ToString();

            public override object DefaultValue => Bindable.Default.ToString();

            // Enum.TryParse would also take "3" or "A, B"; only a declared member's own name is a value,
            // and anything else is the default.
            protected override T ToStored(object value) =>
                Array.IndexOf(Enum.GetNames<T>(), (string)value) >= 0 ? Enum.Parse<T>((string)value) : Bindable.Default;
        }

        private sealed class NumberSetting<T> : Typed<T>
            where T : struct, IConvertible
        {
            public NumberSetting(string key, PresetDomain domain, IBindable bindable)
                : base(key, domain, (Bindable<T>)bindable)
            {
            }

            // Through Convert, not a generic cast: float converts at its own 7 significant digits and
            // double at 15, so 0.4 and 0.01 come out as 0.4 and 0.01, never their binary expansions.
            public override object Value => Convert.ToDecimal(Bindable.Value, CultureInfo.InvariantCulture);

            public override object DefaultValue => Convert.ToDecimal(Bindable.Default, CultureInfo.InvariantCulture);

            protected override T ToStored(object value) => (T)Convert.ChangeType((decimal)value, typeof(T), CultureInfo.InvariantCulture);
        }

        private sealed class ColourStringSetting : Typed<string>
        {
            public ColourStringSetting(string key, PresetDomain domain, Bindable<string> bindable)
                : base(key, domain, bindable)
            {
            }

            // A malformed stored colour already renders as the default, so that is what it reads as.
            public override object Value => ParseColour(Bindable.Value) ?? (int)DefaultValue;

            public override object DefaultValue => ParseColour(Bindable.Default) ?? 0;

            protected override string ToStored(object value) => FormatColour((int)value);
        }

        private sealed class Colour4Setting : Typed<Colour4>
        {
            public Colour4Setting(string key, PresetDomain domain, Bindable<Colour4> bindable)
                : base(key, domain, bindable)
            {
            }

            public override object Value => toRgb(Bindable.Value);

            public override object DefaultValue => toRgb(Bindable.Default);

            protected override Colour4 ToStored(object value)
            {
                int rgb = (int)value;
                return new Colour4((byte)(rgb >> 16), (byte)((rgb >> 8) & 0xff), (byte)(rgb & 0xff), (byte)255);
            }

            private static int toRgb(Colour4 colour) => (channel(colour.R) << 16) | (channel(colour.G) << 8) | channel(colour.B);

            private static int channel(float value) => (int)Math.Round(Math.Clamp(value, 0, 1) * 255);
        }
    }
}
