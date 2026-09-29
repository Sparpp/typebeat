# type!beat

o/

Fork of [osu!lazer](https://github.com/ppy/osu) (PLEASE CHECK THEM OUT!!!)

Instead of circle clicking, you type out the lyrics synced up with the song

[![Website](https://img.shields.io/badge/website-typebeat.mingda.sh-blue)](https://typebeat.mingda.sh/)
[![Discord](https://img.shields.io/badge/discord-join-5865F2?logo=discord&logoColor=white)](https://discord.gg/yAR2PDPgBB)
[![YouTube](https://img.shields.io/badge/youtube-@typebeatgame-FF0000?logo=youtube&logoColor=white)](https://www.youtube.com/@typebeatgame)

## Building

Requires the [.NET SDK](https://dotnet.microsoft.com/download); see
[`global.json`](global.json) for the pinned version.

```
git clone https://github.com/Sparpp/typebeat
cd typebeat
dotnet run --project typebeat.Desktop
```

Or open `typebeat.sln` (`typebeat.Desktop.slnf` for the desktop-only subset) in
your IDE.

The game's art, audio and fonts come from the
[`typebeat.Game.Resources`](https://www.nuget.org/packages/typebeat.Game.Resources)
NuGet package. You don't need to download it yourself: the first build (or
`dotnet restore`) fetches it from nuget.org along with the other dependencies,
at the version in
[`typebeat.Game/typebeat.Game.csproj`](typebeat.Game/typebeat.Game.csproj).

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
