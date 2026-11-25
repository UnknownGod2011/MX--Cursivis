# Cursivis local IPC security review

**Baseline:** Git `ea92218d003923c1e8408f7e32ed963d78201cd4`, the public source for the 1.5.3 universal Windows plugin. The changes in this review are for the 1.5.4 candidate.

## Architecture

| Feature | Current communication path |
| --- | --- |
| Universal Logitech Actions Ring and Console actions, global hotkeys, Trigger Launcher, and second-instance Settings | Native client to Companion trigger WebSocket, then the existing UI dispatcher. Go, Take Action, Snip, Talk, Prompt Optimizer, Settings, Live Mode, and internal dial/press messages use this path. |
| Logitech haptics | Companion publishes events over a separate read-only WebSocket to the plugin. |
| Normal text/image/voice and provider settings | Companion to its bundled Node AI service over local HTTP. The service calls Gemini, Ollama, or the configured external provider. Optional streaming Talk transcription uses that same service's WebSocket. |
| Live Mode | The Companion starts its own Gemini Live session over TLS and controls Windows microphone capture. The Cursivis trigger may start or stop the session. The local Talk gateway does not itself open a microphone. |
| Browser Take Action and context | Companion calls the managed Playwright agent or the current-tab bridge. The user-enabled Chromium extension exchanges requests and replies over Native Messaging with the registered Windows host. The native host relays those messages to Companion's local bridge. |
| Activation and repair | Setup verifies a version-pinned runtime archive before switching it in. Companion starts bundled Node services. Setup and Diagnostics verify those services and the trigger and haptic channels. |

## Confirmed findings and changes

| ID | Severity | Baseline evidence and impact | Remediation |
| --- | --- | --- | --- |
| IPC-01 | High | The old `TriggerIpcServer.HandleContextAsync` accepted a WebSocket upgrade based solely on `IsWebSocketRequest`. `ReceiveLoopAsync` raised the trigger event from the JSON payload. `App.TriggerIpcServerOnTriggerReceived` dispatched Live Mode to `ToggleAsync`, reaching Gemini and microphone startup in `GeminiLiveConversationService`. The same boundary covered other triggers. Browser exposure depended on browser network policy. The application supplied no authorization barrier. | Require native context and a valid, fresh proof before upgrading either Companion WebSocket. The trigger parser bounds size, requires the protocol envelope, and accepts only known commands. Haptics stays read-only. |
| IPC-02 | High | The old AI app's `/runtime/api-key`, provider/local model operations, analysis, and transcription routes could be reached by an unauthorized local process. `attachLiveGateway` also accepted a local upgrade before creating a Gemini session. | Protect every production HTTP route and gate the streaming voice upgrade before creating a provider session. Bound WebSocket frames and close a provider session if the client disconnects during initialization. |
| IPC-03 | High | The old browser bridge's `routeRequest` accepted current-tab context, plan execution, and extension polling/replies without credentials. Its JSON writer also added wildcard CORS. An unauthorized local caller could act through or impersonate the bridge. The separate Playwright agent exposed context and plan routes without authentication. | Authenticate both browser services before handling requests. Split desktop and relay credentials. Chromium grants access only to registered extension IDs through Native Messaging. Request IDs are random and replies must match the browser session that received the command. The updated extension never polls HTTP or receives an IPC credential. |
| IPC-04 | Medium | The older Companion and browser bridge could accumulate unbounded message bodies. The browser bridge could leave timed-out actions in its queue. | Limit messages, bound sessions and pending requests, and discard abandoned or expired work. Keep browser commands serialized per extension session. |
| IPC-05 | Medium | The old health probes treated any successful response on a port as trusted. A different local process occupying a Node port could receive private request bodies. | Require server proof on the actual TCP connection before sending HTTP bodies and check each response proof. WebSocket clients verify the upgrade response before sending data. IPC clients bypass proxies and HTTP redirects. Setup and Diagnostics use authenticated checks. |
| DATA-01 | Medium | The prior demo script saved provider keys as plain text and put the key pool in a child process command line. The runtime builder could include a developer's managed-browser profile if present. Setup and a few Live Mode operations used relative names for Windows utilities. | Use the existing DPAPI format for demo and legacy migrated provider keys. Pass provider settings in a child environment. Exclude browser profiles from release ZIPs. Launch Windows utilities using system paths and redact provider credentials in new history entries. |
| DATA-02 | Medium | `VoiceCommandPromptService.PersistVoiceDebugArtifactAsync` saved full Talk audio, transcripts, returned commands and error text in a temporary diagnostics directory, even for successful sessions. | Retain only non-content voice metadata and exception categories in the per-user log directory. Old diagnostics are not removed automatically. |
| DEP-01 | High in the dependency advisories | npm reported 11 dependency findings in the AI agent and three in the managed browser agent, including request parsing, URI parsing and image library advisories. The `proxy-addr` advisory was rated critical by npm. This audit found no enabled proxy-trusting configuration in the desktop listeners. Advisory counts are not confirmed product exploits. | Refresh the existing dependency lockfiles without forced major upgrades. Update the bundled Node 22 release to the official security patch and verify the downloaded archive's digest in the builder. |

The release candidate retains the universal Logitech classification and existing action list. The patch leaves the AI router, permission settings, providers, hotkeys, UI workflows, model preferences, and runtime installation model in place.

The second review found an independent streaming Talk bug. The gateway passed a Node `Blob` to the Gemini SDK, which needs base64 `data` and `mimeType`. The gateway now sends that shape and signals readiness after provider setup. The existing buffered Talk fallback remains available, and the separate Gemini Live Mode implementation is unchanged.

## Credential lifecycle and browser boundary

- Native clients share a random 32-byte per-Windows-user installation credential. Windows DPAPI `CurrentUser` protects the registry value at rest. A named mutex serializes initial creation and repair. Reinstallation and upgrades retain the value.
- The plaintext root stays in native processes. HMAC derives separate service keys for triggers, haptics, AI, the managed browser, desktop bridge and browser relay. Node receives only its relevant derived keys in process-local environment variables and removes those variables on startup. No key is in a query parameter, package, browser resource, or normal log.
- A proof covers the method, numeric loopback authority, path, timestamp and a random nonce. Services compare it in constant time, enforce freshness and reject replays. Native IPC accepts only the loopback peer and exact Host. A browser Origin, including `null`, or browser fetch metadata is rejected before privileged dispatch. There is no anonymous fallback.
- Chromium's per-user registration checks extension identity. Companion registers the bundled extension at its installed path. An already registered development copy retains its approved ID. A copy loaded from a different path with no prior native registration needs explicit registration for that ID. Chromium still requires the user to enable the extension.
- A missing or invalid protected value is replaced. If it is removed while services are running, their old keys stop matching. Restart the Cursivis runtime with Setup to activate the replacement. For a suspected compromise, close the affected runtime and browser host, remove only `CredentialV1` under `HKCU\Software\Cursivis\LocalIpc`, then run the matching Setup and reconnect. This leaves provider keys and preferences intact. The protection assumes a trusted Windows account and OS. A process already executing unrestricted code as that account can act as the user.

## Verification on Windows

| Area | Observed result |
| --- | --- |
| Automated tests | 111 Node tests and 11 .NET tests passed. Coverage includes absent and invalid proofs, expired and replayed proofs, browser-origin and rebinding-style Host rejection, separation of relay and desktop authority, authenticated voice and haptics, and provider regressions. The original vulnerable build was examined in source, not exercised with unauthorized actions. |
| Service identity | A local test listener without the service key received only the empty HEAD probe. The Companion HTTP client rejected the peer and withheld the private request body. |
| Bundled extension | The extension loaded from the installed runtime in an isolated Chromium profile and connected to the packaged Windows Native Messaging host. It read the active tab, filled a form field and clicked a button on a local fixture. Ordinary web-page requests were denied. Closing Chromium disconnected the bridge session. |
| Setup and repair | The loopback QA installer upgraded 1.5.3 to 1.5.4 and preserved saved settings and the protected provider profile byte-for-byte. The final 1.5.4 archive was then reinstalled using a Windows system-only PATH. Setup authenticated all five service channels. Installed file hashes matched staging, there was one Companion and one Hotkey Host, and no staging or backup directory remained. Diagnostics verified the healthy services without restarting them. |
| Providers | A real Gemini text request succeeded. The matching Release clients used the installed backend to transcribe synthetic speech over both buffered HTTP and the streaming voice WebSocket. An authenticated provider test generated text with a real Ollama process and an isolated test model without changing the saved provider. |
| Logitech and Live Mode | Logi Plugin Service loaded the candidate DLL through official package installation and the 1.5.3-to-1.5.4 upgrade, registered seven user-facing actions and connected haptics. In the earlier runtime QA the candidate plugin's trigger client started Live Mode, Gemini connected, and Windows microphone capture started. The packaged Trigger Launcher stopped the session and microphone. The second-instance Settings path also connected. |
| Build and package | Debug and Release builds passed, the official Logitech pack and verify succeeded, and npm and NuGet dependency checks reported no known vulnerabilities at the time of the scan. The plugin archive contains 15 files and the runtime ZIP contains 3,225 files. The latter includes portable Node, production dependencies and the native host without bundling npm. The Setup publish directory contains only the EXE. Archive inspection and credential-pattern scans found no forbidden package assets or matching credentials. |

Final local candidate SHA-256 values:

| Artifact | SHA-256 |
| --- | --- |
| `Cursivis_1_5_4.lplug4` | `396C027533254B40CECA5EAFBC1F2CC6402D27B9E867F8DC10BAC029C7C3F681` |
| `CursivisRuntime_1_5_4.zip` | `832100523A67432940046B45DB20CF862509A06EEB41824E1254073B0CBAACC8` |
| `CursivisCompanionSetup_1_5_4.exe` (production URL) | `D4F1454CDD95BD88EC4941442DB818F584EC9510CECDE13701AEF0D70E266026` |

## Remaining Release Checks

- The `Plugin installation cannot start` failure was specific to the old 6.1.4 installer CLI on the tested 6.4.2 service. The official 6.4.2 CLI installed the final package and upgraded 1.5.3 on this workstation. A clean Windows test and a physical Actions Ring or Creative Console press are still pending. See [the installation verification report](LOGITECH_INSTALL_VERIFICATION.md).
- Windows did not grant registration for Ctrl+Alt+Q or Ctrl+Alt+X in this session. Confirm both on an uncontended Windows profile. A clean-machine install, a full reboot and a forced rollback were not exercised in this pass.
- The production installer pins the matching version-specific GitHub Releases asset. That asset is unpublished. The loopback QA installer tested the same archive and hash, so a public installation has not been certified. No changes were pushed, published or submitted.
- The historical cloud demo deployment is outside the Windows release path and was not tested. Do not deploy the local Node listener as a public API or bypass its guard.

## Marketplace

- Use the matching Windows plugin, runtime ZIP and Companion Setup from this candidate. Old plugins and launchers cannot talk to the secured listener. Update or reload the packaged extension at the same time. Paired delivery avoids an insecure compatibility mode.
- Verify the package with the official packer. Keep `PluginApi.dll` excluded, the plugin universal, and the supported hardware and user-facing action list documented. Include the existing EULA/privacy policy and explain when a deliberate Live Mode action sends microphone audio to Gemini.
- Logitech's review email sets **December 15, 2026** as the signing deadline. The public guidelines and SDK distribution page do not specify the required signing party, approved certificate type, or which of the DLL, package, or Companion binaries must be signed. The candidate plugin DLL is unsigned. The installed packer verified package integrity. Confirm the publisher procedure with Logitech before signing or resubmission.

References

- [Logi Plugin Approval Guidelines](https://logitech.github.io/actions-sdk-docs/plugin-approval-guidelines/)
- [Logi C# Plugin Distribution](https://logitech.github.io/actions-sdk-docs/csharp/plugin-development/distributing-the-plugin/)
- [Chromium Native Messaging](https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging)
