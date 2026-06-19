# Cursivis Privacy Policy

Effective date: June 13, 2026

This policy explains what Cursivis processes when you use the Cursivis Companion app, Logitech plugin actions, Cursivis Live Mode, browser workflow tools, API LLM mode, Local LLM mode, and future Hosted Cursivis AI mode.

## Summary

Cursivis is designed to act on the content you intentionally select or trigger. It does not need a general cloud account to run the local companion workflow, and it does not include the developer's private AI API keys.

## Information Processed

Cursivis may process:

- Text, code, form content, page context, image snippets, or audio commands that you intentionally select, capture, or trigger.
- Configuration settings such as selected AI backend, local model name, backend URLs, and feature toggles.
- API keys or access tokens that you choose to save for API LLM mode or future hosted mode.
- Local runtime status such as whether Ollama is reachable or whether a selected local model is installed.
- Live Mode settings, permission preferences, local workflows, to-do items, and a capped local action history.

## API LLM Mode

When API LLM mode is enabled, selected content and prompts are sent to the AI provider configured by the user, such as Google Gemini or another compatible provider. The provider's own privacy policy and terms apply to that processing.

User-saved API keys are stored locally on Windows with DPAPI protection for the current Windows user. Cursivis does not intentionally log API keys.

## Local LLM Mode

When Local LLM mode is enabled, selected content is sent to a local Ollama-compatible server on the user's computer. Local model inference is intended to stay on the device unless the user has configured a remote Ollama-compatible endpoint.

Local model weights are not bundled inside the Logitech plugin package. Models are downloaded only after the user chooses local setup.

## Voice And Audio

Voice commands may be transcribed before being sent as text to the selected AI backend. In Local LLM mode, raw audio transcription may still require a configured API transcriber unless a local transcription engine is added later. After transcription, the resulting text can be used by Local LLM mode.

## Cursivis Live Mode

Live Mode is an optional Gemini Live voice session. When the user starts Live Mode, microphone audio is streamed directly to Google's Gemini Live API using a Gemini API key saved by the user. Gemini may return spoken responses and structured requests for supported computer actions.

Live Mode is off until the user starts it and stops capturing microphone audio when the session ends. Cursivis does not intentionally save raw microphone audio. Input and output transcripts may appear temporarily in the on-screen status UI but are not written to the Live Mode diagnostic log.

Live Mode stores its preferences, workflows, to-do items, intent memory explicitly requested by the user, and recent action outcomes locally under the current Windows account. API keys remain in the existing DPAPI-protected Cursivis runtime profile and are not duplicated into Live Mode settings.

The default Live Mode setting auto-executes routine actions. Users can switch to Require Confirmation in Settings. Screen inspection and camera capture are one-shot actions initiated by an explicit user request and are visibly reported; irreversible system actions require a fresh confirmation. Users can stop Live Mode at any time by pressing the Live Mode action again.

## Browser Actions

For browser execution workflows, Cursivis may read current-tab context and generate structured browser action plans. It should only use this context for the action the user requested.

## Logs

Cursivis may write local diagnostic logs needed to troubleshoot runtime health, plugin actions, Live Mode, or connection failures. Cursivis does not intentionally log API keys, access tokens, raw microphone audio, or full Live Mode transcripts.

## Data Sharing

Cursivis sends user-selected content only to the backend the user has selected:

- API LLM mode: the user's configured cloud API provider.
- Local LLM mode: the user's configured local Ollama-compatible endpoint.
- Hosted Cursivis AI: the future Cursivis-hosted service, when enabled.
- Cursivis Live Mode: Google's Gemini Live API while the user has an active Live Mode session.

## User Control

Users can change AI backend mode, replace or remove saved API keys, switch local models, change Live Mode permissions, and stop using local model downloads from the Cursivis Companion settings.

## Contact

For support or privacy questions, use the support link provided on the Cursivis Marketplace listing.
