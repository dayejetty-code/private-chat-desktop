# Windows source build

For 0.10.4 and later, also read `NETWORK-ISOLATION.md`. BFE/Windows Firewall must be running and all firewall profiles enabled. The program creates its own per-user AppContainer without changing global firewall rules.

## Current 0.10.12 stable parser backport

The dependency lock selects `7.0.3+privatechat.parser1`, never the beta candidate. Build with `tools/build.ps1 -OutputDirectory dist-v0.10.12 -SelfContained`. Obtain or rebuild the exact native dependency as described under "Obtain dependencies" below. The release also supplies `SimpleX-7.0.3-privatechat-parser1-source.zip`: stable upstream source, patched parser/tests, and recorded build steps. Paths in the recorded native scripts must be reviewed for the local build machine.

The accepted production assembly is documented in `SECURITY-PARSER-BACKPORT-0.10.12.md`. `tools/package-parser-backport.ps1 -RunDirectory <accepted-run>` verifies the original 3,183-assertion evidence and exact production/runtime hashes, refuses source drift, checks that production rejects 18 QA flags, and packages those same binaries. Local test evidence and chat profiles must not be published. This separate development-preview path does not claim the historical full-release gate's new ETW, soak, sleep or all-bridge delivery coverage. Verify the installed payload and restricted native worker with `tests/maintenance-install-smoke.ps1 -Version 0.10.12 -InstallPath <path> -ReportPath <report> -BrokerPath <matching-probe>`.

## Historical Windows-user-bound maintenance checks (0.10.11)

Use the current pinned SDK in `tools/dotnet10` and `tools/build.ps1`. Publish production to `dist-v0.10.11` and the separate self-contained QA runner to `tests/hardening-qa`; set `PRIVATECHAT_QA_WORKER` to that exact production executable. The production target is `net10.0-windows`. Do not replace binaries while a capture or test is running.

Run the same thirteen suites listed below, plus `--text-file-test`, `--cache-test` and offline `--file-test`. Current privacy checks additionally cover DPAPI CurrentUser round-trip, anonymous-context rejection, restored original-context access, tamper rejection, oversized envelopes and migration from PCKEY002. Anonymous impersonation is not a second-PC or second-Windows-account test. Keep actual worker hashes, report times, real-profile hashes and known-fixture archive cleanup results; never apply the migration to a real profile from a test.

The current ETW and stability suite is `tests/network-stability-0.10.11`. Its README describes capture, controls, fault injection, bridges, a 60-minute native-core soak and production UI power tests. Completed current reports are required; neither earlier-version evidence nor a running collector is a network pass. Local candidate evidence does not constitute installer acceptance or a public release. Desktop 0.10.10 remains the daily-use version during testing.

## Historical no-backup and memory-hard key checks (0.10.10)

Build production to `dist-v0.10.10` and QA to `tests/privacy-qa` with the same build switches below. Set `PRIVATECHAT_QA_WORKER` to the final production executable. Run `--privacy-test`, `--privacy-ui-test`, `--key-test`, `--erase-test`, `--deletion-test`, `--deletion-ui-test`, `--backup-test`, `--self-test`, `--ui-security-test`, `--file-ui-test`, `--conversation-test`, `--conversation-ui-test`, then `--privacy-network-test`. Launch via `Start-Process -WindowStyle Hidden -Wait -PassThru` and inspect every exit code and JSON status. Archive-related QA only verifies legacy fixtures; it does not make backups available in production. The former backup UI test is retired.

The privacy suite checks the RFC 7914 scrypt vector, a separately computed Python hashlib fixture at production work factors, production metadata absence of archive code and backup entrypoints, both key-format compatibility and tamper rejection, fresh-key migration, preserved encrypted contents, retained preexisting archives/history, and restart completion of an interrupted committed cleanup. UI tests exercise the actual upgrade handler with an existing 12-character password, the new 16-character creation requirement, absent backup controls, no network during migration and narrow/wide layouts. Existing key tests cover uncommitted transaction rollback states.

The network test establishes two synthetic contacts on this PC through Tor, exchanges and verifies a message, stops both endpoints and Tor, upgrades one existing profile, then checks its safety code, history and real bidirectional messages. It is not cross-device acceptance or a new ETW leak audit. There is no SSD forensic or physical power-loss claim.

Record current production/QA hashes and unchanged real-profile hashes in `privacy-validation.json`. Package 0.10.10 replaces the retired backup acceptance gates with current-worker `privacy`, `privacyUi` and `privacyNetwork`, while retaining key/erasure and other release gates. Local candidate evidence is not full installer acceptance or publication approval.

## Verified file erasure checks (0.10.9)

Build the current candidate and a separate QA runner. Destruction tests use synthetic data under the QA output, never the user's real profile.

```powershell
./tools/build.ps1 -OutputDirectory dist-v0.10.9 -SelfContained
./tools/build.ps1 -OutputDirectory tests/erase-qa -EnableQa -SelfContained
$env:PRIVATECHAT_QA_WORKER = (Resolve-Path ./dist-v0.10.9/PrivateChat.exe).Path
$suites = '--erase-test','--deletion-test','--deletion-ui-test','--key-test','--backup-test','--backup-ui-test','--self-test','--ui-security-test','--file-ui-test','--conversation-test','--conversation-ui-test'
foreach ($suite in $suites) {
    $test = Start-Process ./tests/erase-qa/PrivateChat.exe -ArgumentList $suite -WindowStyle Hidden -Wait -PassThru
    if ($test.ExitCode -ne 0) { throw "Failed: $suite" }
}
Remove-Item Env:PRIVATECHAT_QA_WORKER
```

Inspect exit codes and current JSON results. The new `erase-qa.json` verifies multi-chunk and empty files, independently reads the overwritten file after cancellation before unlink, checks occupied/read-only/replaced paths, refuses synthetic alternate-stream and sparse files without mutation, continues other keys/files after one key failure, honors cancellation after all key attempts and resumes only from a new review. Deletion UI tests verify successful identity cleanup closes the app and selected-backup deletion does not. Compression/EFS refusal is implemented but has no dedicated fixture.

Record final DLL hashes, actual suite counts and unchanged real-profile hashes in `erasure-validation.json`. This local candidate record is not full installer acceptance. Package 0.10.9 additionally requires a passed current-worker `erasure` report, retaining all earlier gates including the real Tor backup/restore network suite. Offline regression does not establish new network capture, cross-device acceptance, hardware power-loss behavior or physical SSD/RAM sanitization.

## Random-key migration and destruction checks (0.10.8)

```powershell
./tools/build.ps1 -OutputDirectory dist-v0.10.8 -SelfContained
./tools/build.ps1 -OutputDirectory tests/key-qa -EnableQa -SelfContained
$env:PRIVATECHAT_QA_WORKER = (Resolve-Path ./dist-v0.10.8/PrivateChat.exe).Path
Start-Process ./tests/key-qa/PrivateChat.exe -ArgumentList '--key-test' -WindowStyle Hidden -Wait
Start-Process ./tests/key-qa/PrivateChat.exe -ArgumentList '--deletion-test' -WindowStyle Hidden -Wait
Start-Process ./tests/key-qa/PrivateChat.exe -ArgumentList '--deletion-ui-test' -WindowStyle Hidden -Wait
Start-Process ./tests/key-qa/PrivateChat.exe -ArgumentList '--backup-test' -WindowStyle Hidden -Wait
Start-Process ./tests/key-qa/PrivateChat.exe -ArgumentList '--backup-network-test' -WindowStyle Hidden -Wait
Remove-Item Env:PRIVATECHAT_QA_WORKER
```

Inspect exit codes and JSON results. `key-qa.json` covers native SQLCipher encrypted export with Unicode/space-containing legacy passwords, migration failure/cancellation, authenticated envelopes, independent backup/restore keys, legacy archive compatibility, key-first partial deletion, ciphertext-only recovery failure, preserved manual backups and exports, and consistent database/key recovery at simulated transaction interruption points. The UI suite also runs actual key migration and destruction handlers against synthetic data. The network suite establishes a real synthetic Tor conversation, restores to the new key format and checks safety codes, history and bidirectional sends.

Re-run backup, conversation, file UI and security regressions after changing shared profile handling. Package 0.10.8 requires current-worker `keys`, `deletion`, `deletionUi` and `backupNetwork` reports along with all earlier release gates. `key-validation.json` is a local candidate record, not `security-qa.json` or full installer acceptance. No test establishes SSD erasure, TPM revocation, physical power-loss behavior or independent security certification.

## Local destruction build and checks (0.10.7)

Build the self-contained candidate and a separate QA runner. All deletion targets are synthetic profiles and selected test archives inside the QA output, never the real user profile.

```powershell
./tools/build.ps1 -OutputDirectory dist-v0.10.7 -SelfContained
./tools/build.ps1 -OutputDirectory tests/deletion-qa -EnableQa -SelfContained
$env:PRIVATECHAT_QA_WORKER = (Resolve-Path ./dist-v0.10.7/PrivateChat.exe).Path
Start-Process ./tests/deletion-qa/PrivateChat.exe -ArgumentList '--deletion-test' -WindowStyle Hidden -Wait
Start-Process ./tests/deletion-qa/PrivateChat.exe -ArgumentList '--deletion-ui-test' -WindowStyle Hidden -Wait
Remove-Item Env:PRIVATECHAT_QA_WORKER
```

Inspect `deletion-qa.json` and `deletion-ui-qa.json`; do not infer success from process launch. Coverage includes offline password validation, all managed data removed, retained manual backups/exports, wrong confirmation, interrupted cleanup marker, fresh identity, changed files, occupied files, hard links and junction rejection, and actual UI handler execution on synthetic data. SQL message fixtures are not real relay messages. Tests do not establish physical power-loss resilience or media sanitization.

Packaging 0.10.7 also requires `deletion` and `deletionUi` passed reports with the current production worker DLL hash in the release QA record. All existing network/bridge/backup/isolation/conversation gates remain required. `deletion-validation.json` documents a local candidate only and must not be renamed to release `security-qa.json`.

## Local backup build and checks (0.10.5)

Build the local candidate and a separate QA runner. The runner uses only synthetic profiles and exercises the production worker via an environment override that is absent from production builds.

```powershell
./tools/build.ps1 -OutputDirectory dist-v0.10.5 -SelfContained
./tools/build.ps1 -OutputDirectory tests/backup-qa -EnableQa -SelfContained
$env:PRIVATECHAT_QA_WORKER = (Resolve-Path ./dist-v0.10.5/PrivateChat.exe).Path
# Run these sequentially and inspect the corresponding *-qa.json reports.
Start-Process ./tests/backup-qa/PrivateChat.exe -ArgumentList '--backup-test' -Wait
Start-Process ./tests/backup-qa/PrivateChat.exe -ArgumentList '--backup-ui-test' -Wait
Start-Process ./tests/backup-qa/PrivateChat.exe -ArgumentList '--backup-network-test' -Wait
```

`backup-qa.json` covers encrypted round-trip, wrong passwords, corruption/truncation/reordering, path rejection, cancellation, attachment relocation and interrupted-restore journal states. `backup-ui-qa.json` covers locked-profile restore, layout and lock cancellation. `backup-network-qa.json` establishes a real synthetic conversation over Tor, stops both identities and Tor for backup/restore, then checks the restored identity's safety code, history and bidirectional delivery. This is not physical cross-PC or hardware power-loss testing. Keep reports and synthetic databases local.

Packaging 0.10.5 additionally requires these three passed reports as `backup`, `backupUi`, and `backupNetwork` in the release QA record. Existing file/network/bridge/isolation release gates remain required. A local backup candidate alone is not an installer-release pass; do not relabel older 0.10.4 evidence as a new hash's validation.

The independent OS-boundary runner is `tests/network-isolation/Probe.csproj`. Publish it self-contained to `tests/network-isolation/probe`, copy the production `runtime` directory and `runtime-manifest.json` there, then run its `Probe.dll` with the absolute production `PrivateChat.exe` path and one of `direct`, `obfs4`, `snowflake`. Save `isolation-qa.json` separately after each successful run. The three reports must contain the final production DLL hash. `tests/network-isolation/Legacy-Upgrade.ps1` additionally checks a synthetic 0.10.3 store created outside AppContainer can reopen in 0.10.4. Neither test opens the real user's profile.

Run `tests/network-isolation/Run-Qa.ps1` for `network`, `files`, `ui`, `file-ui`, `text`, and `cache`, sequentially: the network suite deliberately tampers with its QA runtime to test integrity checks. Use a separate directory for bridge probes and retain incomplete attempts. The release report must include `networkIsolation.direct`, `.obfs4`, and `.snowflake` in addition to the checks below. Build the OS-boundary probe before running the installer test: it provides the test-only broker for the installed restricted worker and is not part of the release payload.

Use a Windows x64 machine and PowerShell 7. The repository excludes binary dependencies, personal profiles, local test output and installer build artifacts. The dependency versions, download locations and expected hashes are in `dependencies.lock.json`.

## Obtain dependencies

1. Download the pinned .NET SDK ZIP from `buildSdk.source`. Compare its SHA-512 with `buildSdk.sha512` using `Get-FileHash -Algorithm SHA512`. After it matches, extract the ZIP into `tools/dotnet10` so that `tools/dotnet10/dotnet.exe` exists. The current candidate uses SDK 10.0.401 and bundled runtime 10.0.12. The older SDK 8 commands below document historical test suites; they are not the current production build instructions.
2. The default native core is now a local stable backport, not the original MSI DLL. Use the verified `libsimplex.dll` in `simplex.nativeDirectory`, matching `simplex.nativeFiles`, or rebuild from stable commit `b3907c9de7a2c596075762bf05ba3e1d2532aff1` plus `tests/security-fixes-0.10.12/native-backport/parser-fix.patch`. The upstream unpatched DLL is not an acceptable substitute. See the native sequence below and `SECURITY-PARSER-BACKPORT-0.10.12.md`.
3. Download `tor.source` to `vendor/tor-expert.tar.gz`, and compare its SHA-256 with `tor.sha256`. Verify its detached signature with the Tor signing key according to the [official verification guide](https://support.torproject.org/tor-browser/getting-started/verifying-tor-browser/). The pinned signing fingerprints are recorded in the lock file. The build extracts the verified archive into the output on every run.
4. Download the FireDaemon OpenSSL 3.5.9 package from `openssl.package`, verify `openssl.packageSha256`, and extract it under `vendor/openssl-3.5.9`. The library is `x64/bin/libcrypto-3-x64.dll` and the license is `LICENSE.txt`. Verify the pinned library hash and publisher signature; copy the same DLL beside the backported core to satisfy the native input lock. The build bundles the separately pinned library and `OPENSSL-LICENSE.txt`. The older OpenSSL used for the upstream native build recipe is not the application runtime.

Native build sequence used for this candidate:

```powershell
# Use a fresh isolated checkout and the verified toolchain/build-input records.
# The recorded scripts target C:/Users/Lenovo/pc-native; review paths before reuse.
./test-parser.ps1 -Stage baseline  # requires the unmodified stable Protocol.hs
git apply /path/to/native-backport/parser-fix.patch
./test-parser.ps1 -Stage patched   # all 10 native parser checks must pass
./build-dll.ps1                   # requires both regression gate reports
```

GHC 9.6.3, Cabal 3.10.2.0 and Winlibs 13.1.0 UCRT/POSIX sources and hashes are in `native-backport/toolchain-downloads.json`; OpenSSL and the locally supplied Perl modules are in `build-inputs.json`. The `parser-regression` package, Cabal local configuration and scoped build scripts are retained there. Preserve the upstream pinned Git dependencies and secure Hackage verification. These are recorded build steps, not an automatic toolchain installer. A rebuild on another machine may produce a different binary hash; review it and repeat acceptance before deliberately updating the dependency lock. Do not bypass a hash mismatch.

The native library and protocol originate from [the complete stable source](https://github.com/simplex-chat/simplex-chat/tree/b3907c9de7a2c596075762bf05ba3e1d2532aff1), with the one documented parser backport. Tor bundle source provenance and component licenses are included in its `docs` directory. The application, installer and website sources are in this repository; there are no hidden application modules or external NuGet packages.

## Historical 0.10.4 build and test commands

```powershell
./tools/build.ps1 -OutputDirectory dist-v0.10.4 -SelfContained
./tools/build.ps1 -OutputDirectory tests/security-runtime -EnableQa
$env:PRIVATECHAT_QA_WORKER = (Resolve-Path ./dist-v0.10.4/PrivateChat.exe).Path
./tools/dotnet/dotnet.exe ./tests/security-runtime/PrivateChat.dll --ui-security-test
./tools/dotnet/dotnet.exe ./tests/security-runtime/PrivateChat.dll --self-test --network --public-relays
./tools/dotnet/dotnet.exe ./tests/security-runtime/PrivateChat.dll --file-test --network
./tools/dotnet/dotnet.exe ./tests/security-runtime/PrivateChat.dll --file-ui-test
./tools/dotnet/dotnet.exe ./tests/security-runtime/PrivateChat.dll --text-file-test
./tools/dotnet/dotnet.exe ./tests/security-runtime/PrivateChat.dll --cache-test
Remove-Item Env:PRIVATECHAT_QA_WORKER
```

The environment override is compiled only into QA builds. Never distribute the QA build. Tests create synthetic profiles and require working Tor connectivity. A timeout is incomplete evidence, not a network pass. Read `tests/security-runtime/ui-security-qa.json` and `qa-result.json`; do not copy an older release's results onto a new binary.

### Bridge regression

Build the production distribution above first. This probe requires a path containing spaces to catch transport-token parsing regressions. The initial helper-only commands are useful even when the network is unavailable; they are not delivery passes. Both `--network` runs must pass before packaging 0.10.4.

```powershell
./tools/dotnet/dotnet.exe build ./tests/bridge-repair/Probe.csproj -c Release -o './tests/bridge-repair/bin/Space Install'
Copy-Item ./dist-v0.10.4/runtime './tests/bridge-repair/bin/Space Install' -Recurse
Copy-Item ./dist-v0.10.4/runtime-manifest.json './tests/bridge-repair/bin/Space Install'
$env:PRIVATECHAT_QA_WORKER = (Resolve-Path ./dist-v0.10.4/PrivateChat.exe).Path
./tools/dotnet/dotnet.exe './tests/bridge-repair/bin/Space Install/Probe.dll' obfs4
./tools/dotnet/dotnet.exe './tests/bridge-repair/bin/Space Install/Probe.dll' snowflake
./tools/dotnet/dotnet.exe './tests/bridge-repair/bin/Space Install/Probe.dll' obfs4 --network
./tools/dotnet/dotnet.exe './tests/bridge-repair/bin/Space Install/Probe.dll' snowflake --network
Remove-Item Env:PRIVATECHAT_QA_WORKER
```

Reports are written to a unique directory under the probe's `runs` folder. Retain incomplete runs and record their reason; select only fresh successful network reports for the local `bridges.obfs4` and `bridges.snowflake` release fields. Those reports must name the current worker assembly hash. The probe observes connection tables, not full packet or DNS events.

For slow bootstrap, `--slow-network` allows 600 seconds instead of 240. `--directory-cache <previous-run/tor>` copies only public certificates, consensus and microdescriptors into the new fixture; it does not copy guards, profiles or configuration. `--single-tested-relay` limits the synthetic endpoints to the first relay that passed the existing native connectivity check. These options and cold-start/relay failures must be disclosed in the local evidence, not presented as all-relay or cold-start coverage.

## Package a reviewed build

Install the [official Inno Setup compiler](https://jrsoftware.org/isdl.php) into `tools/inno-setup` after verifying its publisher signature; respect its licensing terms. The preview installer was built with 7.1.0 x64. Its Chinese language file is included by that compiler.

`tools/package-installer.ps1` is a release gate: it expects a local `dist-v0.10.4/security-qa.json` recording version, `exeSha256`, `assemblySha256`, and the actual passed `nativeAndNetwork`, `ui`, `files` (network requested), `fileUi`, `textFiles` and `cache` reports. Assemble that record only after reviewing the new run, along with the additional lifecycle and release-build checks described in `installer/TESTING.md`. Local reports can contain machine paths and must not be committed or uploaded.

```powershell
./tools/package-installer.ps1 -Version 0.10.4
./tests/installer-test.ps1 -Version 0.10.4
./tools/build-download-page.ps1 -Version 0.10.4
node ./tools/preview-download.mjs
```

The installer test refuses to run when the product is already installed. It installs into an isolated directory, temporarily creates a real desktop shortcut and per-user uninstall registration, then removes those through the actual uninstaller. Use a clean test account if needed. It verifies silent installation, bundled runtime/native core, in-use blocking, same-version reinstall and preservation of synthetic data on uninstall; it does not replace testing the interactive wizard on another PC.

Only publish the installer, `SHA256SUMS.txt` and reviewed release notes. `package-local.json`, `installer-qa-local.json`, compiler logs and staging directories are local evidence. `docs` contains the GitHub Pages site; it points to a versioned release so a beta is never silently presented as a stable build. The local preview binds only to loopback and displays an unpublished banner.
