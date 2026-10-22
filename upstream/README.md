# Vendored from Osmantic/ODS

These files are copied unmodified from [Osmantic/ODS](https://github.com/Osmantic/ODS)
(Apache-2.0), commit `2854d92ff7a8`, under its `ods/` folder layout. The desktop app and its
tests read them as their source of truth, so the app picks the same models as the ODS
installers and keeps the same llama.cpp pins.

| File | Used for |
|---|---|
| `config/model-library.json` | Curated model catalog, shipped with the app |
| `config/extensions-catalog.json` | Service names and descriptions on the Extensions page |
| `config/backends/amd.json`, `nvidia.json` | Tests keep `runtime-pins.json` equal to ODS's Windows pins |
| `tests/fixtures/model-selection-*.json` | Golden output of `scripts/select-model.py` for selection parity tests |
| `installers/windows/lib/constants.ps1` | Tests keep the llama.cpp tag and ODS version in step with the installer |
| `extensions/services/dashboard/public/ods*.{ico,png}` | ODS mark for the app and installer (see the dashboard's ASSET-NOTICES.md upstream) |

Refresh them from a checkout with `../sync-upstream.ps1 -OdsPath <ODS checkout>\ods` and run
the tests; a failure shows exactly what drifted.
