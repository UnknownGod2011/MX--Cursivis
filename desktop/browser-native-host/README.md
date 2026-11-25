# Cursivis Browser Native Host

Companion starts the local, authenticated browser bridge in `src/host.js`.
Chromium launches `Cursivis.BrowserNativeHost.exe` over Native Messaging for
the registered Cursivis extension. The extension has no local IPC credential.

Companion registers the extension shipped in the runtime automatically. To use
an unpacked development copy, load it, copy its ID from the browser's extensions
page, and use `scripts/install-browser-bridge.ps1 -ExtensionId <ID>`.
