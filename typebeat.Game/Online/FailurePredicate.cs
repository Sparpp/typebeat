// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace typebeat.Game.Online
{
    /// <summary>
    /// One retry or failover decision, as two sets of <see cref="FailureClass"/>es.
    /// </summary>
    /// <param name="Decisive">
    /// The classes this decision stops at. The exception chain is walked outermost first, and the first link in this
    /// set decides; every other link is looked past. Two predicates can therefore disagree on one chain by
    /// stopping at different links.
    /// </param>
    /// <param name="Matching">The decisive classes that answer yes.</param>
    public readonly record struct FailurePredicate(FailureClass Decisive, FailureClass Matching)
    {
        public bool Matches(Exception? exception) => (FailureClassifier.Classify(exception, Decisive) & Matching) != 0;
    }
}
