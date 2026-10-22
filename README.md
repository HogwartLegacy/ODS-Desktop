# ODS for Windows: native desktop app

The native Windows edition of ODS: a WPF desktop app and `ods-cli.exe` over a
shared engine that runs ODS's core local AI without Docker or WSL. Users read
[WINDOWS-NATIVE-DESKTOP.md](docs/WINDOWS-NATIVE-DESKTOP.md); the
roadmap and design decisions are in
[WINDOWS-NATIVE-MIGRATION-PLAN.md](docs/WINDOWS-NATIVE-MIGRATION-PLAN.md).
This page is for contributors.

## Quick start

Requirements: Windows 10 or 11 and the .NET 10 SDK (`global.json`). Nothing
else; `vpk` is restored as a local tool.

```powershell
# from the repository root

# Unit tests, including parity with scripts/select-model.py
dotnet test --project tests\Ods.Core.Tests\Ods.Core.Tests.csproj

# Run the app against a throwaway data folder (setup starts from scratch)
$env:ODS_DESKTOP_HOME = "$env:TEMP\ods-dev"
dotnet run --project src\Ods.Desktop

# End-to-end check without the UI: runtime, model, server, generation, chat
dotnet run --project src\Ods.Cli -- smoke

# Self-contained build and Velopack installer in artifacts\releases\win-x64
.\build.ps1 -Pack
```

`ODS_DESKTOP_HOME` keeps development away from the real data folder
(`%LOCALAPPDATA%\ODS\Desktop`). The app and `ods-cli` share a data folder and
take an exclusive lock on it, so quit the app (`ODS.exe --quit`) before running
`ods-cli serve` or `smoke` against the same folder.

## Layout

```
ODS-Desktop/
├── upstream/                     Files vendored from Osmantic/ODS (catalog, backend pins, fixtures, icon)
├── docs/                         User guide and migration plan
├── runtime-pins.json             llama.cpp Windows builds: tag, asset, SHA-256, size, required files
├── windows-native-services.json  How each ODS service runs natively (Extensions page, migration plan)
├── build.ps1                     Test, publish (self-contained), package with Velopack
├── src/
│   ├── Ods.Core/                 Engine; no UI
│   │   ├── Catalog/              model_memory.py + model_selection.py + select-model.py ports, catalogs
│   │   ├── Hardware/             DXGI, nvidia-smi, detection.ps1 heuristics, tier map
│   │   ├── Runtime/              Pins, acceleration plan, installer, qualification, arguments, server host
│   │   ├── Downloads/            Verified, resumable HTTPS downloads
│   │   ├── Models/               Model store, verified-download index, data removal
│   │   ├── Chat/                 Streaming chat client, SSE reader, conversation store
│   │   ├── Platform/             Job objects, owner-only files, ports, Run key, engine lock, other editions
│   │   ├── Diagnostics/          Log, support bundle
│   │   └── OdsEngine*.cs         The façade the app and CLI use
│   ├── Ods.Desktop/              WPF app (WPF-UI Fluent), view models, Velopack hooks, tray
│   └── Ods.Cli/                  ods-cli.exe
└── tests/
    ├── Ods.Core.Tests/           xUnit v3 (Microsoft.Testing.Platform)
    └── clean-machine.ps1         Install-to-uninstall acceptance test
```

## How the engine works

`OdsEngine.Create` takes the data folder's lock, loads settings, the catalog
and the pins, ensures the API key and stops any llama-server a crashed session
left behind. Then:

1. **`ProbeAsync`** reads the hardware (`HardwareProbe`) and plans the
   llama.cpp builds to try (`AccelerationPlanner`): NVIDIA → CUDA 13.1, CUDA
   12.4, Vulkan, CPU; AMD → Vulkan, CPU; otherwise CPU.
2. **`EnsureRuntimeAsync`** reuses the saved runtime after a `pin.json` check,
   or installs and qualifies each planned build in order (`RuntimeInstaller`,
   `LlamaQualifier`). A build that cannot use the GPU explains why before the
   next is tried.
3. **`Recommend`** and **`PlanFor`** run the ODS selection policy for the
   qualified build's hardware envelope, including runtime profiles.
4. **`StartServerAsync`** launches `llama-server.exe` with the ODS native
   arguments (`LlamaArguments`) in a kill-on-close job object and proves it
   ready (`LlamaServerHost`). **`SwitchModelAsync`** restores the previous
   model when a new one fails.

The view models never block the UI thread: engine events arrive on thread-pool
threads and are marshalled with `AppHost.OnUi`. `ShellViewModel.RunAsync`
serializes start, stop, switch and repair so they cannot overlap.

## ODS contracts this app follows

| Desktop code | Follows | Enforced by |
|---|---|---|
| `Catalog/ModelMemory.cs`, `ModelSelection.cs`, `ModelRecommender.cs` | `extensions/services/dashboard-api/model_memory.py`, `model_selection.py`, `scripts/select-model.py` | `ModelSelectionGoldenTests` (47 envelopes in `tests/fixtures`), `ModelMemoryTests` |
| `runtime-pins.json` (Vulkan, CUDA ARM64) | `config/backends/amd.json`, `nvidia.json` | `RuntimePinsTests` |
| llama.cpp release tag, ODS version | `installers/windows/lib/constants.ps1` | `RuntimePinsTests` |
| Runtime install, `pin.json`, qualification, launch, readiness proof | `installers/windows/lib/native-llama-runtime.ps1` | `StorageTests`, `LlamaLaunchTests` |
| AMD and NVIDIA detection | `installers/windows/lib/detection.ps1` | `GpuClassifierTests` |
| Tier map | `installers/windows/lib/tier-map.ps1` (`ConvertTo-TierFromGpu`) | `AccelerationPlannerTests` |
| Owner-only secrets | `installers/windows/lib/private-file.ps1` | `StorageTests` |
| Bootstrap model | `installers/lib/bootstrap-model.sh` | — |
| Ports 8080 / 18080 / 28080 | the Windows native runtime | — |

When those upstream files change, run the tests here; a drift fails them.

## Changing pins and data

- **New llama.cpp release:** take the asset names, sizes and digests from
  `https://api.github.com/repos/ggml-org/llama.cpp/releases/tags/<tag>`, update
  `runtime-pins.json` (and `config/backends/amd.json` and `constants.ps1` for
  the Vulkan build so the installers move too), then run the tests. Check that
  `required_files` still match the archives.
- **Catalog changes** need no code change; the app ships
  `config/model-library.json` as is.
- **Native service status** lives in `windows-native-services.json`; keep the
  migration plan's table in step.

## Packaging and releases

`build.ps1` publishes `ODS.exe` and `ods-cli.exe` self-contained into one
folder and, with `-Pack`, runs `vpk pack` (package id `ODSDesktop`, channel
`win` or `win-arm64`). The version is `OdsVersion-DesktopPrerelease` from
`Directory.Build.props`; `-Prerelease preview.2` overrides the label and
`-Prerelease ''` builds a final version.

| Option | Use |
|---|---|
| `-Runtime win-arm64` | Windows on ARM build |
| `-AzureTrustedSignFile signing.json` or `-SignParams '...'` | Sign every binary and the installer |
| `-Msi` | Add a machine-wide MSI bootstrapper for managed fleets |

A release with delta updates:

```powershell
dotnet vpk download github --repoUrl https://github.com/Osmantic/ODS --channel win --pre --outputDir artifacts\releases\win-x64
.\build.ps1 -Pack -AzureTrustedSignFile .\signing.json
dotnet vpk upload github --repoUrl https://github.com/Osmantic/ODS --channel win --outputDir artifacts\releases\win-x64 `
    --token $env:GITHUB_TOKEN --tag desktop-v3.0.0-preview.1 --releaseName "ODS for Windows 3.0.0 preview 1" --pre
```

The app's update feed defaults to `https://github.com/Osmantic/ODS`
(`settings.json` → `updates.feedUrl`); an HTTPS folder or a file share with a
Velopack feed also works.

## Testing

| Test | Command |
|---|---|
| Unit | `dotnet test --project tests\Ods.Core.Tests\Ods.Core.Tests.csproj` |
| Smoke (real runtime, model and server) | `dotnet run --project src\Ods.Cli -- smoke --json-out smoke.json` |
| Clean machine (installer to uninstall) | `.\tests\clean-machine.ps1 -Setup artifacts\releases\win-x64\ODSDesktop-win-Setup.exe` |

`.github/workflows/windows-desktop.yml` runs the unit tests and packaging on
every relevant change, then the clean-machine script on a fresh Windows Server
2025 VM. The smoke test downloads the 1.2 GB bootstrap model once and caches it.

## Conventions

ODS's design rules apply (see [ARCHITECTURE.md](https://github.com/Osmantic/ODS/blob/main/ARCHITECTURE.md), Design Principles): errors
surface with what to do next, catches stay narrow and at I/O boundaries, and
behavior comes from data (catalog, pins, service table) rather than code. The
one deliberate retry is the downloader's resume after a stalled connection,
which continues only while each attempt makes progress and reports every
resume. Keep files focused, prefer pure functions for policy, and add a test
with every ported rule.

## Relationship to ODS

This is a companion to [Osmantic/ODS](https://github.com/Osmantic/ODS). The ODS files the app reads are vendored in [upstream/](upstream/README.md) and refreshed with `sync-upstream.ps1`. Plan and rationale: [docs/WINDOWS-NATIVE-MIGRATION-PLAN.md](docs/WINDOWS-NATIVE-MIGRATION-PLAN.md).
