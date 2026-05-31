# Cursivis IPC Protocol

This directory defines transport-agnostic JSON contracts used between:

- Logitech plugin (or mock trigger) -> companion app
- Companion app -> backend
- Backend -> companion app

Transport can be Local WebSocket or Named Pipe. Payloads stay identical.

## Schemas

- `schema/trigger-event.schema.json`
- `schema/agent-request.schema.json`
- `schema/agent-response.schema.json`
- `schema/intent-memory.schema.json`

Trigger event notes:

- `pressType` supports `tap`, `long_press`, `dial_press`, `dial_tick`
- `dialDelta` is required when `pressType` is `dial_tick`

## Versioning

Every payload should include a `protocolVersion` field with semver.
Current: `1.0.0`
