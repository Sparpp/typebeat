// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Runtime.CompilerServices;

// We publish our internal attributes to other sub-projects of the framework.
// Note, that we omit visual tests as they are meant to test the framework
// behavior "in the wild".

[assembly: InternalsVisibleTo("typebeat.Game.Tests")]
[assembly: InternalsVisibleTo("typebeat.Game.Tests.Dynamic")]
[assembly: InternalsVisibleTo("typebeat.Game.Tests.iOS")]
[assembly: InternalsVisibleTo("typebeat.Game.Tests.Android")]
[assembly: InternalsVisibleTo("typebeat.Game.Tournament.Tests")]

// intended for Moq usage
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
