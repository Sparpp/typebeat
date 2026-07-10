// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using typebeat.Game.Rulesets.Edit.Checks.Components;

namespace typebeat.Game.Rulesets.Edit
{
    /// <summary>
    /// A class which can run against a beatmap and surface issues to the user which could go against known criteria or hinder gameplay.
    /// </summary>
    public interface IBeatmapVerifier
    {
        IEnumerable<Issue> Run(BeatmapVerifierContext context);
    }
}
