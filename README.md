# type!beat

A lyric-typing rhythm game: the lyrics *are* the notes. Words drop with the
music and you keep up on your keyboard.

Built as a fork of [osu!lazer](https://github.com/ppy/osu) (MIT): the
circle-clicking gameplay is replaced by a typing ruleset, plus an in-app lyric
editor and a lightweight online backend.

## Building

Requires the [.NET SDK](https://dotnet.microsoft.com/download); see
[`global.json`](global.json) for the pinned version.

```
dotnet run --project typebeat.Desktop
```

Or open `typebeat.sln` (`typebeat.Desktop.slnf` for the desktop-only subset) in
your IDE.

> **Resources package.** The game's art/audio/fonts ship in a separate
> `typebeat.Game.Resources` NuGet package that is not published publicly (see
> [`nuget.config`](nuget.config)). Without it the game logic still compiles, but
> resource-dependent projects won't restore; point `nuget.config` at your own
> local package feed if you're building outside the original setup.

## Layout

| Path | What |
|---|---|
| `typebeat.Game` | Game shell, menus, editor, and online client (shared osu!-framework layer) |
| `typebeat.Game.Rulesets.TypeBeat` | The typing ruleset: scoring, lyric stage, timing engine |
| `typebeat.Desktop` | Desktop entry point and packaging |
| `lyriclab/` | Standalone Python tool that auto-aligns lyrics to audio into word/syllable timing |

## Licence

type!beat is MIT-licensed; see [LICENCE](LICENCE). It is a derivative work of
osu!lazer, © ppy Pty Ltd, also under the MIT Licence; that copyright notice is
retained as required.

The **"osu!" name and logo are trademarks of ppy Pty Ltd** and are *not* covered
by the MIT Licence.
