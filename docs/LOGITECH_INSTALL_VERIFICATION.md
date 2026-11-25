# Cursivis 1.5.4 Logitech installation verification

## Finding

On November 25, 2026, the Windows 11 workstation ran Logi Options+ 2.9.1013853 and Logi Plugin Service 6.4.2.3414. The global LogiPluginTool 6.1.4.22672 recognized the package and printed:

```text
Plugin 'Cursivis' installation started
ERROR: Plugin installation cannot start
Operation failed in 5.3 seconds.
```

The process returned **0**. At 17:29:34 IST no installed `Plugins\Cursivis` directory appeared, no Cursivis service load was logged, and Application, System, and Code Integrity event queries found no related warnings or errors. The failure occurred before the service began installation or extracted the plugin. It was not a Cursivis initialization error.

The same archive (`7A322A638541144B8B4C6F3F6BE11125138A023570272A1F533C8106809D0193`), at the same path on the same host, installed with [LogiPluginTool 6.4.2.3599](https://www.nuget.org/packages/LogiPluginTool/6.4.2.3599). No settings, manifest, dependency, or logging changes were made between the two attempts. The newer CLI reported `Plugin installation has started` and `Plugin was successfully installed` in 1.4 seconds. At 17:31:13 the service logged 1.5.4 loading from its actual installation directory, seven actions registering, and the haptic channel connecting. The older CLI reproduced the failure with the rebuilt package as well.

The Windows package identifies `bin/CursivisPlugin.dll`. There are no Cursivis overrides of `Install()` or `InstallAdmin()`. The real host loaded the DLL and its dependencies unchanged. `PluginApi.dll` is provided by the service and the project reference remains `Private=false`. The archived DLL matches the built and installed DLL. These results establish an installer CLI/service handoff incompatibility on the tested host. They do not identify Logitech's internal protocol error or establish compatibility with every older service version. [Logitech installation documentation](https://logitech.github.io/actions-sdk-docs/csharp/plugin-features/install-and-uninstall/)

## Release workflow

`scripts/build-logitech-plugin.ps1` now accepts `-LogiPluginToolPath` for the packer/verifier and `-LogiPluginInstallerPath` for a separate installer when using `-InstallPackage`. It prints the vendor output and rejects reported failures even when the process exits zero. Installation must also report success explicitly.

The 6.4.2 packer added the legacy `HasHapticMapping` alias to the packaged YAML even though the source contains only `HasHapticsMapping`. The archive guard rejected that control package. The final candidate was rebuilt with the official 6.1.4 packer and verified with both versions. Use 6.4.2 for installation on this host. Do not edit the packed YAML or add the SDK DLL to work around installation.

## Final artifact and test results

- `artifacts/logitech-marketplace/Cursivis_1_5_4.lplug4`, SHA-256 `396C027533254B40CECA5EAFBC1F2CC6402D27B9E867F8DC10BAC029C7C3F681`.
- Release build passed with no warnings or errors. All 15 payload files are byte-identical to the original archive that first installed successfully. The final archive has no `PluginApi.dll`, PDBs, or legacy haptics alias.
- The 1.5.3 predecessor was byte-identical to the repository's tracked package. Using only the official 6.4.2 installer, the service loaded 1.5.3 at 17:39:59 and the exact final 1.5.4 at 17:40:00 from `%LOCALAPPDATA%\Logi\LogiPluginService\Plugins\Cursivis`. All installed 1.5.4 files matched the final archive. No developer link or manually copied plugin files were used. Companion preferences and the runtime profile were unchanged.
- The seven-page attached older QA report describes Companion availability, haptics retries, and action behavior in 1.5.0. Its proposals are historical context, not changes to make during this installer fix. The universal plugin and security implementation remain unchanged.

## Remaining gate

The 1.5.4 package installed and upgraded on this existing Windows 11 profile. A clean Windows machine or VM is still needed to run a fresh install of the exact final archive, then a separate 1.5.3-to-1.5.4 upgrade starting from clean state. This Windows Home machine does not support Windows Sandbox. [Microsoft Windows Sandbox requirements](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/#windows-edition-and-licensing-requirements)

After that gate, verify a physical Actions Ring press and the Live Mode hotkeys. Confirm signing with Joanna before preparing the coordinated release and submitting to Logitech. There has been no submission, publication, or public asset change.
