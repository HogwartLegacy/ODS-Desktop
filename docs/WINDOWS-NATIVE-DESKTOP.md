# ODS for Windows (native desktop app, preview)

> **Status: preview.** The native app runs ODS's core local AI (model
> selection, llama.cpp inference, chat and the local API) directly on
> Windows. It needs no Docker, no WSL and no administrator rights. The rest of
> the ODS stack moves over service by service; see the
> [migration plan](WINDOWS-NATIVE-MIGRATION-PLAN.md). For the full stack today,
> use the [WSL2 edition](https://github.com/Osmantic/ODS/blob/main/ods/docs/WINDOWS-QUICKSTART.md).

## What you get

- **Guided setup** that checks this PC and picks a model with the same policy
  as the ODS Linux, macOS and Windows installers (`scripts/select-model.py`).
- **Local inference** with the official llama.cpp builds for your hardware
  (CUDA, Vulkan or CPU), pinned by SHA-256 and checked before every start.
- **Chat**, with history kept on this PC.
- **An OpenAI-compatible API** on `127.0.0.1`, protected by a key only you have.
- **Model management**: the curated ODS catalog, fit estimates for this PC,
  verified and resumable downloads, context size, your own GGUF files.
- **Tray, sign-in start, in-app updates, logs and a support bundle.**

## Requirements

| | Minimum | Notes |
|---|---|---|
| Windows | Windows 10 22H2 or Windows 11, x64 or ARM64 | Per-user install, no administrator rights |
| Memory | 8 GB RAM | 16 GB or more for 4B to 9B models on the CPU |
| Disk | 2 GB for the app and runtime, plus the model | Models are 1.2 GB to 50 GB; the models folder can live on any drive |
| GPU (optional) | NVIDIA with a current driver, or AMD | Intel GPUs run on the CPU for now |

## Install

1. Download `ODSDesktop-win-Setup.exe` from the ODS release.
2. Run it. It installs for your Windows account only, into
   `%LOCALAPPDATA%\ODSDesktop`, and adds ODS to the Start menu and the desktop.
   Nothing asks for administrator rights.
3. ODS opens and starts setup.

> Preview builds may be unsigned. If Windows SmartScreen shows **Windows
> protected your PC**, choose **More info > Run anyway**. Signed releases do
> not show this.

## First run

Setup takes a few minutes, mostly downloading:

1. **Check this PC.** ODS reads the processor, memory and graphics adapters
   (DXGI, and `nvidia-smi` for NVIDIA) and decides how to run models:

   | Hardware | llama.cpp build | Notes |
   |---|---|---|
   | NVIDIA Turing or newer, driver 580.88+ | CUDA 13.1 | Fastest path for RTX 20/30/40/50 series |
   | NVIDIA Maxwell to Hopper, driver 551.61+ | CUDA 12.4 | Covers older cards and older drivers |
   | NVIDIA with an older driver | Vulkan | ODS explains how to update the driver |
   | AMD Radeon (discrete or Ryzen AI / Strix Halo) | Vulkan | Same build ODS's Windows path already uses |
   | No supported GPU, Intel graphics | CPU | Uses AVX2/AVX-512 where available |
   | Windows on ARM | CPU (ARM64) | |

2. **Choose a model.** ODS recommends the model its installers would pick for
   this hardware, with two alternatives. You can change the models folder here.
   With **Start chatting right away**, ODS first downloads a small model
   (Qwen 3.5 2B, 1.2 GB) and fetches the recommended one in the background,
   then switches to it when you are not chatting.
3. **Install.** ODS downloads llama.cpp from GitHub and the model from Hugging
   Face. Each file is checked against the SHA-256 pinned in ODS. Interrupted
   downloads resume where they stopped.
4. **Prove it works.** ODS starts the model and asks it "What is 2+2?". The
   answer appears on the last screen.

If a GPU build cannot use the GPU, ODS says why and tries the next build. It
never falls back to the CPU silently.

## Everyday use

### Chat

**Chat** sends messages to the local model and renders replies with code
blocks and tables. **Stop** ends a reply early. Chats are saved as files in the
data folder (below) and never leave this PC.

### Use the local API from other apps

**Home > Local API** shows the base URL (normally `http://127.0.0.1:8080/v1`),
the API key and **Copy a sample request**. Any OpenAI-compatible app works:

```powershell
$key = Get-Content "$env:LOCALAPPDATA\ODS\Desktop\secrets\llama-api-key"
Invoke-RestMethod http://127.0.0.1:8080/v1/chat/completions -Method Post `
  -Headers @{ Authorization = "Bearer $key" } -ContentType 'application/json' `
  -Body '{"messages":[{"role":"user","content":"Hello"}]}'
```

The server listens on this PC only. If port 8080 is taken, ODS uses 18080 or
28080, as ODS's Windows installer does; Home always shows the current address.

### Models

**Models** lists the recommendation (with **Why this model?**), the models on
this PC and the catalog models that fit. **Use** switches models, and the
dropdown beside it sets the context window. If a model fails to start, ODS
starts the previous one again. To use your own GGUF, copy the file into the
models folder; it appears under **On this PC** as **Your file**.

### Tray and sign-in

Closing the window keeps ODS in the notification area so the API stays
available; right-click the ODS icon and choose **Quit ODS** to stop it.
**Settings > Start ODS when I sign in** starts ODS in the tray at sign-in and
loads your model. Turning it off in **Task Manager > Startup apps** is
respected, and Settings shows it.

### Updates

ODS checks for updates at start (Settings can turn this off) and downloads
them in the background, usually as a small delta. **Restart to update** stops
the model server, installs the new version and opens ODS again. Your models,
chats and settings are untouched.

## Where ODS keeps things

| What | Where |
|---|---|
| The app | `%LOCALAPPDATA%\ODSDesktop` (replaced by updates, removed by uninstall) |
| Settings, chats, logs, llama.cpp | `%LOCALAPPDATA%\ODS\Desktop` |
| Models | `%LOCALAPPDATA%\ODS\Desktop\models`, or the folder you chose |
| API key | `%LOCALAPPDATA%\ODS\Desktop\secrets\llama-api-key`, readable only by your account |
| Logs | `%LOCALAPPDATA%\ODS\Desktop\logs` (`ods-desktop-*.log`, `llama-server-*.log`) |

Set the `ODS_DESKTOP_HOME` environment variable to use another data folder,
for example on a second drive or for testing.

## When something goes wrong

ODS shows what failed on **Home** with the actions that fix it:

| Message | Usual cause | What to do |
|---|---|---|
| Not enough GPU memory / system memory | Model or context too large | **Use a smaller context** or **Choose another model** |
| The model server's port is taken | Another program on 8080 | **Use a free port** |
| The GPU is not available to llama.cpp | Old or broken graphics driver | **Get the latest graphics driver**, restart Windows |
| A llama.cpp runtime file is missing | Damaged or blocked runtime files | **Repair llama.cpp** (downloads and verifies it again) |
| The model file could not be loaded | Incomplete or unsupported file | Delete and download it again, or choose another model |
| Download stalled | Network or CDN hiccup | ODS resumes automatically; if it keeps failing, use **Resume** later |

**Logs > Export support bundle** saves a zip with logs and system details for
a bug report. It never includes your chats, models or API key.

Other ODS editions: if ODS also runs in WSL2, or the older Docker-based
installer is in `%USERPROFILE%\ods`, setup and Home say so. Stop the other
edition first so the two do not load two models at once.

llama.cpp cannot open paths with non-English characters directly. ODS uses
the folder's short (8.3) name; if a drive has 8.3 names turned off, choose a
models folder with an English name.

## Uninstall

Use **Settings > Apps > ODS > Uninstall** in Windows, or **Settings > Uninstall
ODS** in the app. Uninstalling stops ODS and its model server and removes the
app, its shortcuts and its sign-in entry. Like ODS's other uninstallers, it
keeps your models, chats and settings so a reinstall can reuse them. To remove
them as well, choose **Uninstall and remove data** in the app, or use
**Settings > Remove all ODS data** first. In a models folder you chose
yourself, ODS deletes only the models it downloaded.

## Command line

`ods-cli.exe` is installed beside the app (`%LOCALAPPDATA%\ODSDesktop\current`):

| Command | Does |
|---|---|
| `ods-cli status` | Runtime, active model and model server state |
| `ods-cli hardware` | What ODS detected and which llama.cpp builds it would try |
| `ods-cli recommend [--json]` | The model ODS picks for this PC; `--backend/--vram-mb/--ram-gb` simulate other hardware |
| `ods-cli models [download\|delete <id>]` | Catalog models, their state and fit |
| `ods-cli serve [<id>]` | Run the model server in the foreground |
| `ods-cli smoke` | End-to-end check: runtime, model, server, generation, chat |
| `ods-cli diagnostics [<zip>]` | Write a support bundle |

`ODS.exe --background` starts in the tray; `ODS.exe --quit` stops a running ODS.

## Privacy

ODS sends no telemetry. It goes online to download llama.cpp (GitHub) and
models (Hugging Face), and to check the update feed. Prompts, chats and files
stay on this PC.

## Known limits of the preview

- CUDA and Vulkan paths follow ODS's tested Windows runtime contract but still
  need validation across the hardware fleet; the x64 CPU path is validated
  end to end. ARM64 builds are produced but not yet tested on hardware.
- Open WebUI, voice, image generation, workflows, RAG and the Portal agent are
  not in the native app yet. The **Extensions** page and the
  [migration plan](WINDOWS-NATIVE-MIGRATION-PLAN.md) show the order.
- The model server binds to `127.0.0.1` only; LAN access is not offered yet.
