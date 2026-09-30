// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Opens synchronous headless Realm operations without NUnit's synchronization context.
    /// That context posts notifications to the thread pool rather than the Realm's owning thread,
    /// allowing a native notification to race with a write or disposal. A null context selects
    /// Realm's synchronous worker-thread behavior. Restore the runner's context after closing Realm.
    /// </summary>
    internal sealed class SynchronousRealmTestContext : IDisposable
    {
        private readonly SynchronizationContext? previous = SynchronizationContext.Current;

        public SynchronousRealmTestContext() => SynchronizationContext.SetSynchronizationContext(null);

        public void Dispose() => SynchronizationContext.SetSynchronizationContext(previous);
    }
}
