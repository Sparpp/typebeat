# Metal frame pacing repair

The Metal backend in `ppy.Veldrid 4.9.69-ga405fe8484` resets its frame-ended event and waits for `CVDisplayLink` **on every frame**, regardless of `SyncToVerticalBlank`. Selecting 2×, 4×, 8× or Unlimited therefore still caps drawing at the display's refresh rate and can miss a refresh when the two pacing mechanisms interfere.

The source-equivalent repair is at the beginning of `MtlGraphicsDevice.WaitForNextFrameReadyCore`:

```csharp
if (MetalFeatures.IsMacOS && !SyncToVerticalBlank)
    return;
```

Both the event reset and display-link wait must be skipped. Resetting the event without its matching wait would block the display-link callback. The existing VSync handshake and iOS drawable availability path stay intact. The setting is read each frame, so changing frame limiters takes effect without restarting.

Upstream source: [MtlGraphicsDevice at a405fe8484](https://github.com/ppy/veldrid/blob/a405fe8484fa5384dfbd1faff58c74f717f46651/src/Veldrid/MTL/MTLGraphicsDevice.cs). Apple also specifies separate presentation pacing for [VSync on and off](https://github.com/apple/game-porting-toolkit/blob/main/game-porting-skills/skills/presenting-metal-drawables/references/frame-pacing.md).

## Build integration

The upstream device is internal to a transitive NuGet dependency and has no application override. A small build tool uses Mono.Cecil to prepend this guard to a **separate copy** in `obj/.../metal/`. It checks the exact upstream SHA-256 before making changes; an upstream upgrade fails the build until the workaround is reviewed. No NuGet cache files are changed, and no patching or reflection runs in the shipped game.

`MetalRendererPatch.targets` replaces the build's copy-local asset and the separately resolved publish asset. Desktop builds and the test runner import it. The compiler still references the original public API. The patcher and Mono.Cecil are build dependencies and are not shipped. All rendering continues through the actual Metal backend; OpenGL and other backend implementations are unchanged.

To compare the upstream version, rebuild with `-p:ApplyMetalFramePacingFix=false`. Rebuild again without that property to restore the fix. Do not disable it for releases.

## Verification

`MetalFramePacingTest` executes the patched dependency's real wait method with managed events and simulated devices. It covers non-VSync drawing, VSync waiting and its frame-ended handshake, switching VSync at runtime, and preserving the iOS drawable request. Native device creation is bypassed so these checks run on every CI platform.

Run:

```sh
dotnet test typebeat.Game.Rulesets.TypeBeat.Tests -c Release --filter FullyQualifiedName~MetalFramePacingTest
dotnet publish typebeat.Desktop -c Release -r osx-arm64 --self-contained -o artifacts/metal-publish
```

The publish output's `ppy.Veldrid.dll` must match the patched build output, not the package binary. Fullscreen is the useful comparison for drawing above refresh rate. In a composited macOS window, `CAMetalLayer.nextDrawable()` can still throttle presentation even with VSync disabled; this repair removes the extra software wait but does not bypass WindowServer.

### Reproducible performance probe

`build/RendererBenchmark` uses the same framework version and build patch as the desktop game. It renders 96 animated text rows, warms up for five seconds, then samples actual renderer frame counts over eight seconds and exits. It uses a separate profile and reports focus, frame limit, backend and drawable resolution. Compare foreground runs at the same resolution; this is a synthetic renderer comparison, not a gameplay FPS guarantee.

```sh
# Fixed Metal, fullscreen, 4× refresh-rate limiter:
OSU_GRAPHICS_RENDERER=veldrid OSU_GRAPHICS_SURFACE=metal dotnet run --project build/RendererBenchmark -c Release -- Limit4x Fullscreen

# Upstream Metal. Rebuild to ensure the copied runtime dependency is replaced:
dotnet build build/RendererBenchmark -c Release -t:Rebuild -p:ApplyMetalFramePacingFix=false
OSU_GRAPHICS_RENDERER=veldrid OSU_GRAPHICS_SURFACE=metal dotnet run --project build/RendererBenchmark -c Release --no-build -- Limit4x Fullscreen

# Restore the fix before subsequent runs:
dotnet build build/RendererBenchmark -c Release -t:Rebuild

# Compare OpenGL or test VSync / Limit2x / Limit8x / Unlimited:
OSU_GRAPHICS_RENDERER=gl OSU_GRAPHICS_SURFACE=opengl dotnet run --project build/RendererBenchmark -c Release --no-build -- Limit4x Fullscreen
```

### Local results, October 7, 2026

Apple M4 Pro, 120 Hz built-in panel, 3456×2234 fullscreen drawable, Release build, 96 animated text rows. Each result averages eight one-second samples after warmup; these short runs are subject to system load.

| Renderer | Frame limiter | Average rendered FPS |
| --- | --- | ---: |
| Upstream Metal | 4× | 120.8 |
| Fixed Metal, initial comparison | 4× | 453.9 |
| Fixed Metal, reproducible probe | 4× | 478.2 |
| OpenGL | 4× | 445.6 |
| Fixed Metal | 2× | 239.4 |
| Fixed Metal | VSync | 116.8 |

Windowed runs at 2560×1440 remained constrained by drawable/compositor availability (roughly 120–160 FPS with the fix), so fullscreen throughput should not be assumed for composited windows. This is a renderer probe, not a measured change to gameplay input latency.

Debug and Release desktop builds, the desktop solution filter, all four frame-pacing regression tests, and self-contained `osx-arm64` publishing passed. Build and publish copies of `ppy.Veldrid.dll` had matching SHA-256 hashes; the upstream NuGet copy stayed unchanged. An unexpected input binary was rejected without producing an output.
