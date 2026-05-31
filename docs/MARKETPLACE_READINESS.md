# Cursivis Marketplace Readiness

This note captures the current publishing path for Logitech Marketplace and the AI provider strategy needed before a public release.

## Logitech Marketplace Requirements

Official Logi Actions SDK docs confirm:

- Plugins are distributed through Logitech Marketplace / Loupedeck Marketplace as `.lplug4` packages.
- A `.lplug4` package is effectively a zip package with a required directory structure.
- `metadata/LoupedeckPackage.yaml` is required.
- The plugin icon must be in `metadata/Icon256x256.png`.
- Packages should be packed and verified with `logiplugintool pack` and `logiplugintool verify`.
- Logitech manually reviews new digital products and updates before publication.
- If the product collects or sends personal data, a privacy policy and adequate privacy agreements are required.
- Open-source software included in the product must comply with Logitech's accepted open-source requirements.

Sources:

- https://logitech.github.io/actions-sdk-docs/marketplace-approval-guidelines/
- https://logitech.github.io/actions-sdk-docs/csharp/plugin-development/distributing-the-plugin/
- https://logitech.github.io/actions-sdk-docs/csharp/icons/plugin-icon/
- https://logitech.github.io/actions-sdk-docs/csharp/plugin-features/managing-plugin-settings/
- https://logitech.github.io/actions-sdk-docs/csharp/plugin-features/install-and-uninstall/

## Current Architecture Status

Gemini is not deeply coupled into the desktop/plugin layers. The direct Gemini calls are concentrated in:

- `backend/gemini-agent/src/geminiService.js`
- `backend/gemini-agent/src/apiKeyPool.js`

The companion currently talks to the local backend through `GeminiClient.cs`, but that class is an HTTP client wrapper, not a direct Gemini SDK dependency. The naming is provider-specific, but the architecture can support other providers.

## Provider Layer Added

The backend now has a provider factory:

- `gemini`: current default, unchanged behavior.
- `openai_compatible`: OpenAI-compatible chat completions endpoint for OpenAI, OpenRouter-style gateways, LM Studio local server, or other compatible services.
- `hosted_cursivis`: paid hosted Cursivis AI proxy. This is where the product should route paid users so the real upstream Gemini/OpenAI key stays server-side.
- `local_ollama`: local Ollama-style inference for beta/local mode.

Configuration examples:

```powershell
$env:CURSIVIS_AI_PROVIDER="gemini"
$env:GOOGLE_API_KEY="<USER_GEMINI_KEY>"
```

```powershell
$env:CURSIVIS_AI_PROVIDER="openai"
$env:CURSIVIS_OPENAI_API_KEY="<USER_OPENAI_OR_COMPATIBLE_KEY>"
$env:CURSIVIS_OPENAI_MODEL="gpt-4.1-mini"
```

```powershell
$env:CURSIVIS_AI_PROVIDER="hosted"
$env:CURSIVIS_HOSTED_API_URL="https://api.cursivis.example"
$env:CURSIVIS_HOSTED_TOKEN="<USER_LICENSE_OR_ACCESS_TOKEN>"
```

```powershell
$env:CURSIVIS_AI_PROVIDER="ollama"
$env:CURSIVIS_OLLAMA_URL="http://127.0.0.1:11434"
$env:CURSIVIS_LOCAL_MODEL="gemma4:e2b"
```

## Recommended Public Launch Strategy

Do not ship a raw Gemini API key in the plugin, desktop client, browser extension, or backend package.

Recommended v1:

- User-owned API key mode.
- Paid hosted Cursivis AI mode through your own backend, license token, quotas, and rate limits.

Recommended beta:

- Local model mode through Ollama / LM Studio / OpenAI-compatible localhost servers.

Reason: local model files are large, hardware-dependent, and have separate license/runtime/update concerns. Logitech docs allow plugin install hooks and embedded files, but they do not make a giant hidden model installer a safe marketplace assumption. A guided, opt-in local model setup is safer than bundling large weights into `.lplug4`.

## Local LLM Direction

The safest current local runtime target is Ollama on Windows:

- It exposes a stable local HTTP API on `http://127.0.0.1:11434`.
- It supports model listing, model pulling, chat generation, and image payloads for compatible multimodal models.
- It can be checked and guided from the Cursivis companion without bundling model weights into the Logitech plugin package.

Recommended local model choices for Cursivis onboarding:

- `granite3.2-vision:2b` - current default/recommended local mode for average laptops; fastest multimodal option for the first-run setup.
- `gemma3:4b` - balanced multimodal option for users with more RAM.
- `gemma4:e2b` - advanced local option for stronger everyday text and image workflows.
- `gemma4:e4b` - better quality if the user's machine has more memory; not the default on 8 GB RAM laptops.
- `gemma4:26b` - workstation tier, not recommended for default marketplace setup.
- `gemma4:31b` - workstation only; not recommended for default marketplace setup.

Current local verification on the development laptop:

- Ollama for Windows is installed and running.
- Installed multimodal models:
  - `granite3.2-vision:2b`.
  - `gemma3:4b`.
  - `gemma4:e2b`, 7.2 GB, ID `7fbdbf8f5e45`.
  - `gemma4:e4b`, 9.6 GB, ID `c6eb396dbd59`.
- Cursivis backend provider switching has been verified with `local_ollama`.
- `granite3.2-vision:2b` passed a real provider test through `/runtime/ai-provider/test`.
- `granite3.2-vision:2b` passed a real `/analyze` text workflow through the backend.
- API LLM mode was restored after local tests and remains the final active backend.
- Switching local models unloads the previous Ollama model, so local mode is mostly idle until called and does not keep a model resident forever.
- The local provider uses balanced Ollama defaults: thinking is disabled for clean final answers, context/output are capped for everyday workflows, CPU threads are limited by default, and keep-alive is short so memory is released soon after use.
- In Settings, `Use Local` checks Ollama, downloads the selected model automatically when missing, then switches the backend after the model is ready. `Download & Use` shows progress and supports cancellation.

The companion should describe local mode as optimized for fast responses and mostly idle until called. The backend sets Ollama keep-alive to a short warm window, so the model does not need to stay loaded forever.

Do not bundle these models directly inside `.lplug4` unless Logitech explicitly approves the package size, license disclosure, update flow, and review impact. Prefer guided model download from the settings/onboarding flow.

Local runtime/model sources:

- https://docs.ollama.com/windows
- https://docs.ollama.com/api/introduction
- https://docs.ollama.com/capabilities/vision
- https://ollama.com/library/gemma4:e2b
- https://ai.google.dev/gemma/docs/core/model_card_4

## Security Notes

- Newly saved runtime API keys are protected with Windows DPAPI under the current Windows user.
- Existing plaintext runtime profiles are still readable for backwards compatibility and will be protected the next time the profile is saved.
- Hosted paid mode must never expose the upstream AI provider key to the client.
- Text selected by users may be personal data if sent to cloud providers, so public release needs a clear privacy policy and EULA.

## Runtime Installer Status

The Logitech plugin package alone is not enough for a true first-run Cursivis experience because the Companion app, backend service, browser action agent, and secure runtime profile must exist on the user's PC. The current release artifacts now include a separate Windows runtime setup package:

- `artifacts/cursivis-runtime/CursivisRuntime_1_4.zip`
- `scripts/install-cursivis-runtime.ps1`
- `scripts/install-cursivis-runtime.cmd`

The installer:

- Copies the Companion, hotkey host, trigger launcher, backend, browser action agent, and browser bridge files.
- Downloads portable Node.js only if it is not already present in the Cursivis runtime folder.
- Runs production dependency installation for the backend and browser action agent.
- Writes a blank runtime profile with API LLM as the default backend, no bundled private keys, and `granite3.2-vision:2b` as the default local model.
- Registers Cursivis Companion at Windows startup unless disabled.
- Registers the hidden Cursivis hotkey host at Windows startup so the MX gesture-button shortcut can wake the Companion after restart.
- Launches the Companion after setup unless disabled.

Current runtime installer verification:

- Fresh temporary install passed.
- Companion executable, backend source, browser action agent dependencies, backend dependencies, portable Node.js, packaged `.lplug4`, and runtime profile creation were all present after install.
- Production npm install reported `0` vulnerabilities for both Node services.
- The runtime zip does not include a private runtime profile, private API keys, or local model weights.

## MX Gesture Button Shortcut

Actions Ring assignments can call plugin actions directly. The MX gesture button path works differently: users map the gesture button in Logi Options+ to emit a keyboard shortcut, and Cursivis listens for that shortcut.

Current default:

- Cursivis Go: `Ctrl+Alt+Space`
- Take Action: `Ctrl+Alt+A`
- Talk/Text Trigger: `Ctrl+Alt+V`

The Companion Settings UI now includes "Assign Cursivis Go Trigger to a Shortcut". Users can press a shortcut, connect it, and then map their MX gesture button to the same shortcut in Logi Options+. The setting is saved locally and read by both Companion and the hidden startup hotkey host.

## Remaining Work Before Marketplace Submission

- Host the prepared privacy policy and Developer EULA from `docs/PRIVACY_POLICY.md` and `docs/EULA.md` at public URLs.
- Use `docs/MARKETPLACE_SUBMISSION_CHECKLIST.md` during the final upload.
- Add final public support URL, homepage URL, and accurate OS/device support metadata in the Marketplace form.
- Confirm with Logitech during review whether the runtime setup zip should be linked as a companion installer, uploaded as supplemental material, or documented as a required companion download.
- In the Marketplace listing/setup copy, tell MX mouse users to map the gesture button to the same shortcut shown in Cursivis Settings. Cursivis cannot safely rewrite Logi Options+ device assignments automatically.
- Create the hosted Cursivis AI backend with auth, quota, rate limiting, logging redaction, and billing/license verification before enabling the paid hosted option.
- Optionally rename remaining developer-facing `GeminiClient`/Gemini labels in code later; the user-facing Settings section now presents API LLM, Local LLM, and Hosted Cursivis AI.

Current package note:

- `plugin/logitech-plugin/dist/Cursivis.lplug4` and `artifacts/logitech-marketplace/Cursivis_1_4_1.lplug4` have been rebuilt after the latest Marketplace metadata alignment and verify successfully with `logiplugintool verify`.
- The existing package contains `metadata/LoupedeckPackage.yaml`, `metadata/Icon256x256.png`, and `bin/CursivisPlugin.dll`.
- The `.lplug4` package is only the Logitech plugin package; it does not bundle Ollama/Gemma model weights. Local model setup remains a guided Companion flow.
- The matching Windows runtime package is `artifacts/cursivis-runtime/CursivisRuntime_1_4.zip`.
