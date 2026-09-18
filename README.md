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

## Star Rating Sandbox

Experiment with the difficulty formula and compare map ratings live in the
[local Star Rating Sandbox](tools/star-rating-sandbox/README.md).

```sh
tools/run-labs.sh
```

That starts both local labs: the Star Rating Sandbox on http://127.0.0.1:8614 and
the [Typability Lab](tools/typability-app/README.md) on http://127.0.0.1:8615.
The sandbox scores each map's lyrics with The Typability Index and lets you dial
how much that measured typing difficulty moves the rating. Scores come from a
keystroke-free variant of the index — the authors' regression refit without its
`minStrokes` term — so lyrics are not charged for keystrokes the envelope model
already counts. Import type!beat `.osu` or lyriclab timing JSON maps as usual.
It also carries an experimental **Rhythmic Complexity** bonus: an independent
slider that adds stars when keeping perfect timing means changing typing pace,
measured against the engine's real judgement intervals and weighted toward the
map's demanding sections.

To serve just the sandbox (bundled typability scores, no live R service):

```sh
python3 -m http.server 8614 --bind 127.0.0.1 --directory tools/star-rating-sandbox
```

## Licence

type!beat is MIT-licensed; see [LICENCE](LICENCE). It is a derivative work of
osu!lazer, © ppy Pty Ltd, also under the MIT Licence; that copyright notice is
retained as required.

The **"osu!" name and logo are trademarks of ppy Pty Ltd** and are *not* covered
by the MIT Licence.
