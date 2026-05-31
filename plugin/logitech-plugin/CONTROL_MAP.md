# Cursivis Logitech Control Map

This file captures the intended Logitech UX for Cursivis across MX Creative Console, MX Master 4, and Actions Ring.

It should be read as the product interaction model, not just a shortcut map.

## Design goals

- keep the primary interaction extremely fast: select -> trigger -> review -> act
- make Logitech hardware feel like an intent layer, not a macro layer
- keep the mouse-side interaction clean and low-friction
- use haptics to confirm state changes instead of adding visual clutter

## MX Creative Console default layout

### Keypad

Suggested default assignments:

1. `Cursivis Trigger`
   - runs Smart Mode on the current selection
2. `Prompt Optimizer`
   - turns rough prompt text, notes, or idea dumps into structured LLM-ready prompts
3. `Hold to Talk`
   - starts voice capture for contextual commands
4. `Image Selection`
   - starts lasso image capture
5. `Take Action`
   - executes the current browser or UI action
6. `More Options`
   - re-runs the latest selection through the improved action menu
7. `Undo`
   - reverts the last replace or browser action when possible
8. `Copy`
   - copies the current Cursivis result
9. `Insert`
   - inserts or replaces with the current result

### Dialpad

Suggested default assignments:

- `Rotate`
  - moves the Cursivis action selection
- `Press`
  - executes the currently highlighted action
- `Hold`
  - optional advanced mode for voice-first execution

## MX Master 4 reference layout

Suggested mouse-side setup:

1. map a programmable hardware button as the instant Cursivis trigger
2. use the gesture-button profile or another custom button assignment in Logi Options+
3. keep Actions Ring as the nested secondary command layer
4. use thumb wheel or wheel input to navigate Guided mode without moving pointer focus

This gives the mouse two clear roles:

- one-press primary trigger
- anywhere secondary action surface

## Actions Ring reference layout

Recommended top-level behavior:

1. create one primary bubble named `Cursivis`
2. place the core actions inside that bubble

Recommended nested bubbles:

1. `Trigger`
2. `Prompt Optimizer`
3. `Hold to Talk`
4. `Take Action`
5. `Image Selection`
6. `Settings`

This keeps the main ring visually clean while still exposing the full Cursivis system anywhere.

Secondary ring candidates outside the Cursivis folder:

1. `More Options`
2. `Undo`
3. `Copy`
4. `Insert`

## Guided mode navigation

Guided mode works especially well with Logitech hardware:

- dial rotation can move through options on MX Creative Console
- thumb wheel or wheel input can move through options on MX Master 4
- pausing on an option can auto-confirm it for a low-friction flow

## Haptic intent

Current event semantics already wired through the plugin:

- `action_change`
  - light confirmation when the selected AI action changes
- `action_execute`
  - medium confirmation when a trigger is executed
- `processing_start`
  - light pulse when Cursivis begins work
- `processing_complete`
  - strong confirmation when a result is ready

## Validation path before hardware

Without MX devices, we validate:

- plugin builds and packages
- plugin loads in Logi Plugin Service
- synthetic trigger events reach the companion
- haptic events come back into the plugin log

What still requires real devices:

- final button ergonomics
- dial sensitivity and tactile feel
- Actions Ring discoverability
- real haptic quality

## First hardware-day checklist

When MX hardware arrives:

1. test keypad trigger latency
2. test dial tick sensitivity and action clarity
3. test hold-to-talk comfort and cancellation behavior
4. test Actions Ring discoverability on the MX mouse
5. tune default layout based on real-hand usage, not assumptions
