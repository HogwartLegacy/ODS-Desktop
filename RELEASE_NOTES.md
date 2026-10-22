# ODS for Windows 3.0.0 preview 1

The first native Windows build of ODS: no Docker, no WSL, no administrator rights.

- Guided setup checks this PC and picks a model with the same policy as the ODS installers.
- Local inference with official llama.cpp builds (CUDA, Vulkan, CPU, ARM64), pinned and verified.
- Chat, an OpenAI-compatible local API, model management, logs and a support bundle.
- Starts with Windows in the tray if you want it to, and updates itself.

This is a preview: NVIDIA and AMD acceleration need wider hardware validation, and
services beyond local inference (Open WebUI, voice, image generation, workflows) are
on the roadmap shown on the Extensions page.
