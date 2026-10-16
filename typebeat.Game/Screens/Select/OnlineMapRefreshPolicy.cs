// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;

namespace typebeat.Game.Screens.Select
{
    /// <summary>Navigation-driven checks only. Expiry of a failure backoff never schedules a request.</summary>
    internal class OnlineMapRefreshPolicy
    {
        private int selectedSet = -1;
        private readonly Dictionary<int, (int Count, double RetryAt)> failures = new Dictionary<int, (int, double)>();

        public bool ShouldRefresh(int setId, bool force, double now)
        {
            bool changed = selectedSet != setId;
            selectedSet = setId;
            return setId > 0 && (changed || force) && (!failures.TryGetValue(setId, out var failure) || now >= failure.RetryAt);
        }

        public void RecordFailure(int setId, double now)
        {
            failures.TryGetValue(setId, out var previous);
            // Bound memory even during a long session with an unavailable server.
            if (failures.Count >= 64)
                failures.Clear();
            int count = Math.Min(previous.Count + 1, 5);
            failures[setId] = (count, now + Math.Min(5 * Math.Pow(2, count - 1), 60));
        }

        public void RecordSuccess(int setId) => failures.Remove(setId);

        public void ResetSelection() => selectedSet = -1;
    }
}
