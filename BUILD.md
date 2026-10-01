# Windows source build

Use a Windows x64 machine and PowerShell 7. The repository excludes binary dependencies, personal profiles, local test output and installer build artifacts. The dependency versions, download locations and expected hashes are in `dependencies.lock.json`.

## Obtain dependencies

1. Download the pinned .NET SDK ZIP from `buildSdk.source`. Compare its SHA-512 with `buildSdk.sha512` using `Get-FileHash -Algorithm SHA512`. After it matches, extract the ZIP into `tools/dotnet` so that `tools/dotnet/dotnet.exe` exists. The published preview uses SDK 8.0.425 and bundled runtime 8.0.31. This historical dependency set is not a promise of ongoing security support.
2. Download `simplex-desktop-windows-x86_64.msi` from the [SimpleX 7.0.3 release](https://github.com/simplex-chat/simplex-chat/releases/tag/v7.0.3). Compare its SHA-256 with `simplex.sha256` before extracting. Extract the MSI administratively into `vendor/simplex-extracted`; the two required files must end up in `vendor/simplex-extracted/SimpleX/app/resources/`. Compare each DLL with the `simplex.nativeFiles` hashes. The build script rejects mismatches.
3. Download `tor.source` to `vendor/tor-expert.tar.gz`, and compare its SHA-256 with `tor.sha256`. Verify its detached signature with the Tor signing key according to the [official verification guide](https://support.torproject.org/tor-browser/getting-started/verifying-tor-browser/). The pinned signing fingerprints are recorded in the lock file. The build extracts the verified archive into the output on every run.

Example MSI extraction after hash verification (run from the repository root; this extracts files rather than installing the upstream client):

```powershell
$msi = (Resolve-Path ./vendor/simplex-desktop-windows-x86_64.msi).Path
$target = Join-Path (Get-Location) 'vendor/simplex-extracted'
$process = Start-Process msiexec.exe -ArgumentList @('/a', ('"'+$msi+'"'), '/qn', ('TARGETDIR="'+$target+'"')) -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0) { throw 'MSI extraction failed' }
```

The unmodified SimpleX native library and its protocol originate from [the complete 7.0.3 source](https://github.com/simplex-chat/simplex-chat/tree/v7.0.3). Its native build instructions are maintained upstream. Tor bundle source provenance and component licenses are included in its `docs` directory. The application, installer and website sources are in this repository; there are no hidden application modules or external NuGet packages.

## Build and test the application

```powershell
./tools/build.ps1 -OutputDirectory dist-v0.8.1 -SelfContained
./tools/build.ps1 -OutputDirectory tests/security-runtime -EnableQa
$env:PRIVATECHAT_QA_WORKER = (Resolve-Path ./dist-v0.8.1/PrivateChat.exe).Path
./tools/dotnet/dotnet.exe ./tests/security-runtime/PrivateChat.dll --ui-security-test
./tools/dotnet/dotnet.exe ./tests/security-runtime/PrivateChat.dll --self-test --network --public-relays
Remove-Item Env:PRIVATECHAT_QA_WORKER
```

The environment override is compiled only into QA builds. Never distribute the QA build. Tests create synthetic profiles and require working Tor connectivity. A timeout is incomplete evidence, not a network pass. Read `tests/security-runtime/ui-security-qa.json` and `qa-result.json`; do not copy an older release's results onto a new binary.

## Package a reviewed build

Install the [official Inno Setup compiler](https://jrsoftware.org/isdl.php) into `tools/inno-setup` after verifying its publisher signature; respect its licensing terms. The preview installer was built with 7.1.0 x64. Its Chinese language file is included by that compiler.

`tools/package-installer.ps1` is a release gate: it expects a local `dist-v0.8.1/security-qa.json` recording version, `exeSha256`, `assemblySha256`, and the actual passed `nativeAndNetwork` and `ui` reports. Assemble that record only after reviewing the new run, along with the additional lifecycle and release-build checks described in `installer/TESTING.md`. Local reports can contain machine paths and must not be committed or uploaded.

```powershell
./tools/package-installer.ps1
./tests/installer-test.ps1
./tools/build-download-page.ps1
node ./tools/preview-download.mjs
```

The installer test refuses to run when the product is already installed. It installs into an isolated directory, temporarily creates a real desktop shortcut and per-user uninstall registration, then removes those through the actual uninstaller. Use a clean test account if needed. It verifies silent installation, bundled runtime/native core, in-use blocking, same-version reinstall and preservation of synthetic data on uninstall; it does not replace testing the interactive wizard on another PC.

Only publish the installer, `SHA256SUMS.txt` and reviewed release notes. `package-local.json`, `installer-qa-local.json`, compiler logs and staging directories are local evidence. `docs` contains the GitHub Pages site; it points to a versioned release so a beta is never silently presented as a stable build. The local preview binds only to loopback and displays an unpublished banner.

