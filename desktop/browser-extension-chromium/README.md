# Cursivis Chromium Extension

This unpacked extension lets Cursivis act inside the browser tab you already use and are already logged into.

## What it does

- exposes active-tab DOM context to the local Cursivis stack
- executes browser action plans in the current tab
- supports Chrome-family browsers first:
  - Chrome
  - Edge
  - Brave
  - Opera
  - Vivaldi
  - Arc (best-effort, depending on local native host registration)

## Load it

1. Open your browser extension page.
2. Enable developer mode.
3. Load unpacked extension from this folder.
4. Keep the extension enabled.
5. Refresh the target Gmail / Google Form / site tab once after loading.

For the current demo build, the extension talks to the local Cursivis bridge directly over `http://127.0.0.1:48830`, so the old native-host installer step is not required for the standard setup.
