// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace typebeat.Game.Online
{
    /// <summary>
    /// A caller's own wait on a request expired without the request reporting anything: nothing answered and nothing
    /// was refused, so this is transport-class (<see cref="FailureClass.Stalled"/>).
    /// </summary>
    public class RequestStalledException : InvalidOperationException
    {
        public RequestStalledException(string message)
            : base(message)
        {
        }
    }
}
