// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>Bounded, shared text lookups. Factories run outside the lock used by editor and difficulty workers.</summary>
    internal sealed class JapaneseTextCache<TKey, TValue> where TKey : notnull
    {
        private readonly int capacity;
        private readonly object gate = new object();
        private readonly Dictionary<TKey, LinkedListNode<(TKey Key, Lazy<TValue> Value)>> entries = new Dictionary<TKey, LinkedListNode<(TKey, Lazy<TValue>)>>();
        private readonly LinkedList<(TKey Key, Lazy<TValue> Value)> recent = new LinkedList<(TKey, Lazy<TValue>)>();

        internal JapaneseTextCache(int capacity) => this.capacity = capacity;

        internal TValue GetOrAdd(TKey key, Func<TKey, TValue> factory)
        {
            Lazy<TValue> value;
            lock (gate)
            {
                if (entries.TryGetValue(key, out var found))
                {
                    recent.Remove(found);
                    recent.AddFirst(found);
                    value = found.Value.Value;
                }
                else
                {
                    value = create(key, factory);
                    entries.Add(key, recent.AddFirst((key, value)));
                    if (entries.Count > capacity)
                    {
                        entries.Remove(recent.Last!.Value.Key);
                        recent.RemoveLast();
                    }
                }
            }
            // Concurrent callers share the same computation without blocking unrelated keys.
            return value.Value;
        }

        private static Lazy<TValue> create(TKey key, Func<TKey, TValue> factory) => new Lazy<TValue>(() => factory(key));
    }
}
