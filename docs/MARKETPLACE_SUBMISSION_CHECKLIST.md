# Cursivis Marketplace Submission Checklist

Use this checklist immediately before uploading Cursivis to Logitech Marketplace.

## Final Artifacts

- `artifacts/logitech-marketplace/Cursivis_1_5_2.lplug4`
- `artifacts/cursivis-runtime/CursivisRuntime_1_5_2.zip`
- `artifacts/qa-candidate/CursivisCompanionSetup_1_5_2.exe`

## Verified Locally

- `.lplug4` rebuilt with `LogiPluginTool`.
- `.lplug4` passed `logiplugintool verify`.
- Runtime zip rebuilt after final Companion/settings changes.
- Fresh runtime installer smoke test passed.
- Runtime installer registers Companion startup and the hidden Cursivis hotkey host startup.
- Setup executable downloads the matching runtime ZIP from the versioned GitHub Release and verifies its pinned SHA-256 before extraction.
- Runtime package contains the Chromium extension files; current-tab automation is documented as an optional browser approval step.
- Cursivis Live Mode starts and stops from the plugin action without a second process or startup entry.
- Live Mode uses the existing DPAPI-protected Gemini API-key pool.
- Clean installs default Live Mode to Auto Execute for routine actions. Require Confirmation remains available, and privacy-sensitive or irreversible actions still require approval.
- Backend tests passed.
- Release build passed with 0 warnings and 0 errors.
- Production npm install reported 0 vulnerabilities for backend and browser action agent.
- Source/package secret scan found no likely Gemini/OpenAI/GitHub/Bearer keys.
- Final live backend mode restored to API LLM.
- Local model is not left loaded in Ollama after switching back to API mode.

## Marketplace Form Inputs

- Product name: Cursivis
- Version: 1.5.2
- Plugin package: `Cursivis_1_5_2.lplug4`
- Companion setup URL: `https://mxcursivis.vercel.app/`
- Homepage URL: `https://mxcursivis.vercel.app/`
- Support URL: `https://github.com/UnknownGod2011/MX--Cursivis/issues`
- Privacy policy URL: publish `docs/PRIVACY_POLICY.md` and use its public URL.
- Developer EULA URL: publish `docs/EULA.md` and use its public URL.

## Review Notes To Disclose

- Cursivis sends selected content to the user-selected backend.
- API LLM mode requires the user's own API key.
- Local LLM mode uses Ollama-compatible local inference and downloads models only after the user chooses that setup.
- Hosted Cursivis AI is marked coming soon and should not be represented as live until the production service exists.
- The `.lplug4` package does not bundle private API keys or large local model weights.
- Live Mode streams microphone audio to Google's Gemini Live API only while the user has started a session.
- Live Mode is permission-aware, prepares messages as drafts, and does not silently execute privacy-sensitive or irreversible actions.
- Live Mode is compiled into Companion and does not install the standalone `keyboard.wtf` app.
- MX gesture-button users should map their gesture button in Logi Options+ to the shortcut shown under "Assign Cursivis Go Trigger to a Shortcut"; default is `Ctrl+Alt+Space`.

## Manual Steps

- Log in to Logitech Marketplace contribution portal.
- Accept the Marketplace Developer Agreement if prompted.
- Upload the `.lplug4`.
- Link users to `https://mxcursivis.vercel.app/` for the single Companion Setup download.
- Add privacy policy, EULA, homepage, and support links.
- Stop before final submission if Logitech asks for irreversible confirmation, payment, identity verification, or 2FA.
