# Cursivis Architecture Diagram

Primary diagram asset:

- `ARCHITECTURE_DIAGRAM.svg`

Preview image:

- `ARCHITECTURE_DIAGRAM_PREVIEW.png`

The diagram is now meant to communicate the Logitech product story clearly:

- MX Creative Console, MX Master 4, and Actions Ring as the control layer
- the Windows companion as the orchestration layer
- Cursivis orb + result UI as the interaction layer
- the AI backend as the reasoning layer
- real-browser and managed-browser action paths as the execution layer

The intended reading of the system is:

1. user selects context
2. Logitech hardware or the orb triggers intent
3. companion captures selection, voice, and image context
4. backend decides or ranks actions
5. Cursivis returns the result
6. `Take Action` executes the workflow in the browser when appropriate

This diagram should be used to present Cursivis as a Logitech ecosystem product and premium workflow concept.
