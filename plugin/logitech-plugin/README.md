# Logitech Plugin (C#)

## Purpose

Capture Logitech hardware intent and forward it to the local companion app.

## Scope

- Trigger types: `tap`, `prompt_optimizer`, `long_press`, `long_press_start`, `long_press_end`, `live_mode`, `dial_press`, `dial_tick`
- IPC sender client for trigger payloads
- Optional haptic feedback mapping

## Input

- Logitech device event from Logi Actions SDK

## Output

- `TriggerEvent` JSON payload to companion IPC endpoint

## Status

`src/Cursivis.Logitech.Bridge` is implemented as a functional trigger bridge that sends Logitech-style trigger events over local WebSocket to the companion app.

It now also subscribes to companion haptic events over:

- `ws://127.0.0.1:48712/cursivis-haptics/`

and prints/beeps haptic mappings for demo feedback.

This enables full trigger-path testing now, before binding to the official Logi Actions SDK plugin package.

`src/CursivisPlugin` is generated with `LogiPluginTool` and contains a real Logi Actions SDK C# plugin project with Cursivis actions:

- `Cursivis Trigger` -> sends `tap`
- `Cursivis Prompt Optimizer` -> sends `prompt_optimizer`
- `Cursivis Long Press` -> sends `long_press`
- `Cursivis Long Press Start` -> sends `long_press_start`
- `Cursivis Long Press End` -> sends `long_press_end`
- `Cursivis Live Mode` -> starts or stops the permission-aware Gemini Live voice session
- `Cursivis Dial` adjustment -> sends `dial_tick` and `dial_press`

These actions forward events to `ws://127.0.0.1:48711/cursivis-trigger/`.

## Current readiness

The real Logitech plugin path is now working on a machine with Logi Options+ installed:

- `src/CursivisPlugin` builds against the current Logi Actions SDK runtime
- the Debug/Release build hot-loads into Logi Plugin Service through the generated `.link` file
- dynamic Cursivis actions are discovered by the Logitech host
- companion haptic events are translated into Logitech plugin events
- the plugin packages and verifies successfully as `Cursivis.lplug4`

What still needs real MX hardware later:

- Actions Ring ergonomics validation
- MX-specific haptic feel validation
- final physical profile tuning for MX Creative Console / supported MX devices

## Real plugin build / package workflow

From the repo root:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-logitech-plugin.ps1 -Configuration Release
```

Optional install step:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-logitech-plugin.ps1 -Configuration Release -InstallPackage
```

This workflow:

- builds the real Logitech plugin
- packs it into `plugin/logitech-plugin/dist/Cursivis.lplug4`
- verifies the package with `LogiPluginTool`
- prints the plugin log path for quick inspection

Prerequisites:

- Logi Options+ installed
- `PluginApi.dll` available under `C:\Program Files\Logi\LogiPluginService\` for compilation only. It is supplied by Logi Plugin Service at runtime and must never be bundled in the plugin package.
- `logiplugintool.exe` available under `%USERPROFILE%\.dotnet\tools\`

Useful runtime paths:

- plugin log:
  - `%LOCALAPPDATA%\Logi\LogiPluginService\Logs\plugin_logs\Cursivis.log`
- debug `.link` file:
  - `%LOCALAPPDATA%\Logi\LogiPluginService\Plugins\CursivisPlugin.link`

## Planned default Logitech UX

The current hardware-ready control design is documented in:

- `plugin/logitech-plugin/CONTROL_MAP.md`

This gives us a stable target for MX Creative Console and Actions Ring behavior before the real devices arrive.

Current reference interaction goals:

- one programmable MX Master 4 button as the instant Cursivis trigger
- one nested Actions Ring folder for `Trigger`, `Prompt Optimizer`, `Talk`, `Take Action`, `Snip-it`, and `Settings`
- thumb wheel or wheel-driven navigation for Guided mode
- haptic confirmation for selection changes and execution state

## Bridge Run

```powershell
cd plugin/logitech-plugin/src/Cursivis.Logitech.Bridge
dotnet run
```

Controls:

- `T` = tap trigger
- `L` = long press (single event)
- `S` = long press start (press-and-hold begin)
- `E` = long press end (press-and-hold release)
- `P` = dial press
- `V` = Cursivis Live Mode
- `A` = dial tick -1
- `D` = dial tick +1
- `Q` = quit

## Logi SDK Plugin Notes

Logi SDK plugin project path:

- `plugin/logitech-plugin/src/CursivisPlugin/src`

Build this project on a machine with Logi Plugin Service installed (for `PluginApi.dll` reference).

## Virtual validation before hardware

Without MX hardware, the most useful validation path is:

1. run the Cursivis companion stack
2. build/package the real plugin with `build-logitech-plugin.ps1`
3. confirm the plugin loads in Logi Plugin Service
4. drive synthetic trigger events through the local trigger WebSocket
5. confirm `action_change`, `processing_start`, and `processing_complete` appear in `Cursivis.log`

Example synthetic trigger:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\send-logitech-trigger.ps1 -PressType dial_tick -DialDelta 1
```

Note:

- an old Logitech M170 is not a meaningful Actions Ring validation device
- final MX mouse / MX Creative Console validation still needs supported hardware
