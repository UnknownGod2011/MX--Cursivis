# Cursivis Privacy Policy

Effective date: May 27, 2026

This policy explains what Cursivis processes when you use the Cursivis Companion app, Logitech plugin actions, browser workflow tools, API LLM mode, Local LLM mode, and future Hosted Cursivis AI mode.

## Summary

Cursivis is designed to act on the content you intentionally select or trigger. It does not need a general cloud account to run the local companion workflow, and it does not include the developer's private AI API keys.

## Information Processed

Cursivis may process:

- Text, code, form content, page context, image snippets, or audio commands that you intentionally select, capture, or trigger.
- Configuration settings such as selected AI backend, local model name, backend URLs, and feature toggles.
- API keys or access tokens that you choose to save for API LLM mode or future hosted mode.
- Local runtime status such as whether Ollama is reachable or whether a selected local model is installed.

## API LLM Mode

When API LLM mode is enabled, selected content and prompts are sent to the AI provider configured by the user, such as Google Gemini or another compatible provider. The provider's own privacy policy and terms apply to that processing.

User-saved API keys are stored locally on Windows with DPAPI protection for the current Windows user. Cursivis does not intentionally log API keys.

## Local LLM Mode

When Local LLM mode is enabled, selected content is sent to a local Ollama-compatible server on the user's computer. Local model inference is intended to stay on the device unless the user has configured a remote Ollama-compatible endpoint.

Local model weights are not bundled inside the Logitech plugin package. Models are downloaded only after the user chooses local setup.

## Voice And Audio

Voice commands may be transcribed before being sent as text to the selected AI backend. In Local LLM mode, raw audio transcription may still require a configured API transcriber unless a local transcription engine is added later. After transcription, the resulting text can be used by Local LLM mode.

## Browser Actions

For browser execution workflows, Cursivis may read current-tab context and generate structured browser action plans. It should only use this context for the action the user requested.

## Logs

Cursivis may write local diagnostic logs needed to troubleshoot runtime health, plugin actions, or connection failures. The application should avoid logging API keys, access tokens, and full sensitive prompts.

## Data Sharing

Cursivis sends user-selected content only to the backend the user has selected:

- API LLM mode: the user's configured cloud API provider.
- Local LLM mode: the user's configured local Ollama-compatible endpoint.
- Hosted Cursivis AI: the future Cursivis-hosted service, when enabled.

## User Control

Users can change AI backend mode, replace or remove saved API keys, switch local models, and stop using local model downloads from the Cursivis Companion settings.

## Contact

For support or privacy questions, use the support link provided on the Cursivis Marketplace listing.
