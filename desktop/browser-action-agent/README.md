# Cursivis Browser Action Agent

Local browser executor used by the companion’s `Take Action` flow.

## Purpose

- maintain a managed browser session for fallback browser tasks
- inspect live page context for browser planning
- execute structured steps when the current-tab path is unavailable

## Why It Matters

This is what turns Cursivis from “generate an answer” into “finish the workflow.”

For Logitech, that is important because the real product value is not just triggering intelligence. It is turning hardware-driven intent into completed work.

## Endpoints

- `GET /health`
- `POST /ensure-browser`
- `GET /page-context`
- `POST /execute-plan`

## Local Run

```powershell
cd desktop/browser-action-agent
npm install
npm start
```

Default port:

- `48820`
