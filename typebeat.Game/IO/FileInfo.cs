// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using typebeat.Game.Database;

namespace typebeat.Game.IO
{
    public class FileInfo : IHasPrimaryKey, IFileInfo
    {
        public int ID { get; set; }

        public bool IsManaged => ID > 0;

        public string Hash { get; set; } = string.Empty;

        public int ReferenceCount { get; set; }
    }
}
