# Cursivis Live Mode Marketplace Review Notes

## Overview

Cursivis Live Mode is an optional Actions Ring command that starts or stops a spoken Gemini Live session inside the existing Cursivis Companion process.

It is not a second application. It does not install another startup entry, background service, hotkey, settings store, or API-key store.

## Reused Stable Components

The implementation reuses mature components from the local `keyboard.wtf` project:

- Gemini Live WebSocket session handling.
- NAudio microphone capture and spoken-response playback.
- Structured Windows tool declarations.
- Permission-aware action execution and fresh spoken confirmations.
- Local workflows, notes, to-do items, timers, and intent memory.
- Fuzzy app and safe file resolution, learned aliases, and preferred-browser routing.
- Explicit one-shot screen guidance, webcam capture, virtual-desktop shortcuts, and Windows recording shortcuts.
- Recovery and user-facing session status patterns.

The original standalone tray app, startup entry, installer, hotkeys, and duplicate API-key configuration were intentionally excluded.

## Unified Cursivis Boundary

Live Mode shares:

- The Cursivis Companion process and Windows startup registration.
- The existing DPAPI-protected Gemini API-key pool.
- Companion Settings and onboarding.
- The Logitech trigger WebSocket.
- The keyboard.wtf-style Live Mode overlay. The existing orb remains available only for non-Live Cursivis workflows.
- The existing runtime installer and update-preservation behavior.

## Permission Model

The default is `AutoExecute`.

- Read-only context tools can run without confirmation.
- Routine actions run immediately in `AutoExecute` mode.
- Users can switch to `AlwaysAsk` (`Require Confirmation`) from Settings.
- One-shot camera, screenshot, and screen-guidance tools require an explicit user request and visibly report their activity.
- Shutdown, restart, sleep, lock-screen, and Wi-Fi disable requests require a fresh confirmation.
- Closing the current virtual desktop requires a fresh confirmation.
- Emails and messages are prepared as drafts for manual review; Live Mode does not press Send.
- Unsupported authenticated or bulk browser actions are refused.

The preference is stored locally and survives restarts, updates, and installer repairs.

## Data And Privacy

- Microphone audio is streamed to Google's Gemini Live API only while Live Mode is active.
- Raw audio is not intentionally saved.
- API keys stay in the existing Windows DPAPI-protected runtime profile.
- Live Mode logs connection and action outcomes without API keys or full transcripts.
- Preferences, workflows, to-do items, and explicitly requested intent memory are stored locally under `%LOCALAPPDATA%\Cursivis`.

## Runtime And Performance

- Live Mode is idle until the user starts it.
- It adds no separate background process.
- NAudio is compiled into the self-contained Companion runtime.
- Microphone input is sent in 40 ms PCM chunks for responsive streaming.
- Stopping the session releases microphone, speaker, WebSocket, and cancellation resources.

## Known Limitations

- Live Mode currently requires a user-provided Gemini API key and internet access.
- Full webpage DOM reading and reliable signed-in form automation remain dependent on Cursivis browser integration.
- Spotify direct playback is not enabled without Spotify OAuth.
- Discord message sending and bulk account actions are intentionally unsupported.
- A Windows microphone and permission to access it are required.

## Reviewer Test Flow

1. Install the Cursivis plugin and Companion runtime.
2. Open Cursivis Settings and add a Gemini API key.
3. Keep `Auto Execute (Recommended)` selected, or choose `Require Confirmation` to test approval prompts.
4. Assign `Cursivis Live Mode` to Actions Ring.
5. Press it once and ask a normal question.
6. Ask Cursivis to open Notepad. In Require Confirmation mode, confirm when prompted.
7. Press the action again to stop the session.

No separate `keyboard.wtf` installation or configuration is required.
