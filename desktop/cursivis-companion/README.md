# Cursivis Companion (WPF)

## Purpose

Act as the central runtime for the Logitech-first Cursivis workflow:

- capture current context
- render the orb and result UI
- orchestrate Smart, Guided, Talk, Snip-it, and Action
- execute browser-integrated follow-through

## Responsibilities

- selection detection
- image region capture
- voice capture and transcription handoff
- Smart + Guided orchestration
- result panel handling
- browser action preview, execution, and undo
- Logitech trigger parity across tap / long press / dial input

## Current Status

Implemented runnable companion app in `src/Cursivis.Companion`.

## Run

1. Ensure backend is running at `http://127.0.0.1:8080` or set `CURSIVIS_BACKEND_URL`
2. Ensure browser action agent is running at `http://127.0.0.1:48820` for managed-browser fallback
3. If you want real current-tab `Take Action`, load the unpacked extension from `desktop/browser-extension-chromium/README.md`
4. Start the companion:

```powershell
cd desktop/cursivis-companion/src/Cursivis.Companion
dotnet run
```

## Demo Flow

- Select text in an external app
- Press `Trigger`
- Orb shows processing
- Result appears in the result panel and clipboard
- Hold `Talk` to refine with voice
- Use Guided mode for orb-based action choice
- Use `Take Action` to execute the result in the browser

## Logitech Story

This companion is the software layer that turns Logitech control input into workflow action.  
The final intended experience is:

- MX Creative Console as the visible control pad
- MX Master 4 / Actions Ring as the anywhere interaction layer
- the orb as the lightweight software mirror of that system
