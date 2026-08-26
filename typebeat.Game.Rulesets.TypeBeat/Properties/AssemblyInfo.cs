// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Runtime.CompilerServices;

// The ruleset's own test assembly, mirroring the grant typebeat.Game already makes to it.
//
// TypingCell's constructor and its State setter are internal on purpose: only the engine may bring a
// cell into being or move it between states. That also means a test cannot hand-build a cell list in
// a chosen combination of states, which is exactly what pinning a PURE display rule over cells needs
// (LyricLineDisplay.ComputeSpaceErrorDots, backlog 197: the rule reads Expected, IsTypeable and
// State, and the interesting cases are combinations no single play produces in one line). Driving a
// real engine to produce each combination would pin the engine as much as the rule and leave the
// pure function itself untested.
[assembly: InternalsVisibleTo("typebeat.Game.Rulesets.TypeBeat.Tests")]
