# TerraBase release build on hosted runners

2026-11-07. Where the TerraBase push-to-image time goes on GitHub's free hosted runners, and what each change bought.
All payloads are `dotnet publish src/TerraBase -c Release -r linux-arm64 --self-contained` (single file, ReadyToRun)
from main `7f43011fa` plus the change under test. Throwaway-branch runs: 42420609440, 42421084423, 42421560457,
42421912526 (measurement only; never merged).

## Publish step

| Runner | CPU | ReadyToRun | Analyzers | Publish (s) | of which R2R (s) | CoreCompile, all projects (s) | Payload |
| --- | --- | --- | --- | ---: | ---: | ---: | ---: |
| ubuntu-latest | EPYC 9V45 | composite (SDK default) | on | 140 | 87.0 | 59.0 | 283 MB |
| ubuntu-latest | EPYC 7763 | composite (SDK default) | on | 236 | 139.3 | 102.8 | 283 MB |
| ubuntu-24.04-arm | Neoverse-N2 | composite (SDK default) | on | 185, 198, 206 | 115.8-122.3 | 89.1-92.2 | 283 MB |
| ubuntu-24.04-arm | Neoverse-N2 | composite (SDK default) | off | 152 | 114.4 | 40.4 | 283 MB |
| ubuntu-latest | EPYC 9V45 | per assembly | on | 65, 70 | 17.1, 18.4 | 53.2, 57.1 | 198 MB |
| ubuntu-24.04-arm | Neoverse-N2 | per assembly | on | 101, 106, 107 | 21.3-21.7 | 89.3-93.3 | 198 MB |
| ubuntu-latest | EPYC 9V45 | per assembly | off | 43 | 16.6 | 27.4 | 198 MB |
| ubuntu-latest | EPYC 7763 | per assembly | off | 70 | 26.4 | 47.5 | 198 MB |

- SDK 11 sets `PublishReadyToRunComposite=true` for single-file ReadyToRun publishes, so every release recompiled
  the whole shared framework (756 crossgen inputs) into one composite image. Per-assembly ReadyToRun compiles only
  TerraBase and its packages (64 crossgen calls); the framework keeps the runtime pack's own ReadyToRun code. ts-auth,
  not single-file, was already per-assembly.
- `ubuntu-latest` lands on two CPU types; EPYC 7763 needs about 1.7x the time of EPYC 9V45 for the same publish.
- The free arm64 runner is consistent but slower than either x64 type for this build; release jobs stay on x64
  (arm64 output needs no emulation there either: the image only copies the payload).
- Analyzers are about half of compile time. They stay on (the "off" rows only size them).
- A cold TerraBase-only locked restore from nuget.org took 8-12 s on every runner; the monorepo GHA package cache it
  replaced took 25 s to download (1.8 GB) plus 7 s for the whole-solution restore.

## Cold start (linux-arm64, Neoverse-N2)

Development mode, synthetic master key, fresh data directory, process start to the first `200` from `/healthz`.

| ReadyToRun | Runs (ms) |
| --- | --- |
| composite | 626, 512, 511, 514, 511, 512 |
| per assembly | 785, 731, 730, 784, 783, 735 |

Per-assembly code costs about 240 ms per cold start against 70-130 s per release build and 85 MB less for every region
to pull.

## Real main push runs

Push to image pushed. The rest of the path moved too: `checks` (legacy scan in the background, path-filter fixtures)
runs beside `changes`, and release jobs restore only their own project without the GHA package cache.

| Run | Commit | Release job start | TerraBase publish | TerraBase image | ts-auth image | ts-auth released |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| 42418535998 (before) | `7f43011fa` | 70 s | 233 s | 371 s | 174 s | 228 s |
| 42425979354 | `8741019f2` | 38 s | 76 s | 140 s | 151 s | 204 s |
| 42426957542 | `3d23b5484` | 36 s | 115 s | 186 s | 120 s | 169 s |

Rejected: gzip level 1 for the image layer (no faster export in 3 repeats, 11-33 s noise either way; 12% larger
layers).
