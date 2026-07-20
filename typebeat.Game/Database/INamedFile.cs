// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Models;

namespace typebeat.Game.Database
{
    /// <summary>
    /// Represents a join model which gives a filename and scope to a <see cref="File"/>.
    /// </summary>
    public interface INamedFile
    {
        string Filename { get; set; }

        RealmFile File { get; set; }
    }
}
