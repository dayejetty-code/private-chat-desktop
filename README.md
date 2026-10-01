# Private Chat — Windows development preview 0.8.1

A minimal Windows chat client around the unmodified SimpleX 7.0.3 native core. This is a development preview, not an independently audited messenger.

## 下载与安装

[浏览器下载页](https://dayejetty-code.github.io/private-chat-desktop/) · [GitHub Releases](https://github.com/dayejetty-code/private-chat-desktop/releases/tag/v0.8.1)

下载 `PrivateChat-0.8.1-Windows-x64-Setup.exe`，双击按中文向导安装，再打开桌面快捷方式。无需解压、另装 .NET 或管理员权限。支持 Windows x64，最低 Windows 10 2004，推荐仍受支持的 Windows 11。更新前退出应用；更新和卸载保留本地加密资料库。

这是供测试的一对一文字聊天应用，尚未完成跨电脑验收、独立安全审计或代码签名。请先使用虚构消息。散列校验不能代替独立发布者签名。

## Run

Open `dist-v0.8.1/PrivateChat.exe`. Create a local database with a long passphrase (minimum 12 characters). The passphrase is not saved; there is no recovery service. Wait for Tor to connect, then use **添加联系人** to generate or paste a full, one-time invitation link. Sending requires comparing and entering the contact security code through a trusted independent channel. This release uses the same data directory as 0.1; development tests use isolated synthetic profiles.

The app uses `%LOCALAPPDATA%\PrivateChatDesktop` for its encrypted databases and its own Tor state. It does not reuse another SimpleX application's database. `--data <absolute-directory>` selects an independent local profile for testing.

If Tor cannot connect, select an included obfs4 or Snowflake bridge mode and reconnect. The application never offers a direct-to-relay fallback. A bootstrap status is not proof that every relay is reachable or a message was delivered.

The default relay-entry preference is `.onion`. If invitations time out despite a completed Tor bootstrap, switch **中继入口** to **公共域名（经 Tor）** and reconnect. This still uses mandatory Tor SOCKS and pinned TLS to the relay, but the Tor exit can observe the relay destination. The successful network acceptance run used public relay hostnames through Tor; onion relay connections timed out on some attempts in this environment.

## Implemented boundary

- One-to-one text messaging and full invitation links; no global registration.
- A fresh incognito profile per newly invited/accepted contact, supplied by SimpleX.
- SimpleX double-ratchet encryption and its native encrypted database implementation.
- Tor SOCKS is configured and read back before the chat core starts. The Tor process starts only after local privacy preferences have been applied.
- SOCKS always-on, per-entity transport isolation, encrypted local files setting, disabled contact receipts and automatic group-member acceptance.
- Native core executes in a supervised child process. The unlock passphrase travels over its inherited stdin pipe, not command arguments or a listening socket. Process jobs end core/Tor children when locking or closing.
- Automatic lock after five minutes of inactivity, on Windows session lock, and on suspend/resume notifications.
- No remote web content, telemetry, link previews, notification contents, or persisted application logs.
- Both ordinary Copy/Cut and application copy buttons set Windows clipboard-history/cloud-sync exclusion flags. An ownership marker allows best-effort clearing after 30 seconds and on lock without reading or clearing unrelated clipboard text. This does not control third-party clipboard software.
- Sensitive text boxes disable undo history and text drag/drop. Switching recipient clears the outgoing draft.
- Unexpected Tor process exit immediately disposes the guarded native core, then locks the UI. Loss of the core response pipe also terminates the core and locks the UI. Abrupt parent termination closes Windows kill-on-close jobs.
- Each send re-reads the native connection safety code to catch revoked/stale verification before submitting the message. This is not an atomic cryptographic guarantee against every change racing the send.
- The main window requests Windows capture exclusion and refuses unlock if setting it fails. This is limited to supported Windows capture APIs, not cameras or compromised endpoints.
- WER no-heap reporting is set for the UI and core, and .NET diagnostic attachment is disabled for the child core. Explicit UTF-8 native argument and returned-result buffers are wiped before freeing; managed strings and the core's internal copies are not guaranteed to be erased.

Not implemented in this preview: attachments, calls, groups, invitation QR scanning, password changes, backup/restore UI, automatic signed updates. The core uses its upstream preset independent relay operators. Existing full invitations from compatible clients can be accepted.

## Security limits

End-to-end encryption does not protect an already compromised/unlocked endpoint, recipients who save messages, or every network traffic-correlation attack. The parent UI holds decrypted text while unlocked. Process termination is not a guarantee of physical memory erasure; OS paging/crash dumps and external screenshot/clipboard software remain outside this prototype's boundary. The application itself is unsigned and has not undergone an independent security review.

Runtime hashes are embedded in the managed application; replacing the external manifest with an empty/edited list is rejected. Core DLL dependencies are loaded from the pinned core directory and System32, excluding the current directory/PATH from that load. These checks are not an independent publisher signature: an attacker able to replace the application can change the checks too. SimpleX's MSI was checked against its release SHA-256. The Tor expert bundle was checked against the published SHA-256 and its OpenPGP signature, with the primary key fingerprint verified against Tor's official documentation. Builds recheck its archive hash and extract it afresh.

No OS firewall/AppContainer boundary enforces egress yet. Network observations are bounded TCP snapshots, not packet captures covering DNS/UDP. Memory dumps, swap, hibernation and forensic disk recovery have not been exhaustively tested. Windows capture/clipboard API behavior is not protection against malware running as the same user or administrator. Cross-machine and bridge-mode acceptance remain outstanding.

## Source structure

| File | Responsibility |
|---|---|
| `src/MainWindow.xaml` | Restrained WPF interface with bounded reading and unlock layouts |
| `src/MainWindow.xaml.cs` | Unlock, conversations, invitations, safety-code verification, UI lifecycle |
| `src/MainWindow.Relays.cs` | Relay settings dialog, test-before-enable gating and sensitive-field lifecycle |
| `src/RelaySettings.cs` | SMP validation, encrypted core configuration, Tor policy checks and readback |
| `src/HistoryPage.cs` | Bounded cursor pagination with older/newer/latest navigation |
| `src/MessageViewport.cs` | Preserve the visible message anchor during refresh |
| `src/CoreWorker.cs` | Native SimpleX C ABI in a separate child process |
| `src/CoreClient.cs` | Parent IPC client and strict network policy |
| `src/TorService.cs` | Private Tor process, bridge modes and bootstrap state |
| `src/RuntimeSecurity.cs` | Runtime integrity verification and child-process job ownership |
| `src/PrivateClipboard.cs` | Privacy flags, ownership and timed clearing for text copying |
| `src/SelfTest.cs` | Native storage, lifecycle and optional real network acceptance checks |
| `tests/SecurityTests.cs` | Tampering, policy, process-crash and WPF lifecycle regression tests |
| `tests/HistoryTests.cs` | Pagination boundaries, real encrypted 225-item traversal and WPF scroll anchors |
| `tests/RelayTests.cs` | Relay input, configuration persistence, certificate rejection and real Tor connectivity |
| `tests/AuditRegressionTests.cs` | Delayed safety-code responses, power notifications and broken IPC in isolated WPF sessions |
| `tools/build.ps1` | Build and assemble the local Windows executable |

## Build and test

See [BUILD.md](BUILD.md) for fresh-checkout dependency acquisition and installer packaging. The workspace-local .NET SDK lives in `tools/dotnet`; it is not installed globally. No external .NET packages are required by the application source.

```powershell
./tools/build.ps1 -OutputDirectory dist-v0.8.1 -SelfContained
./tools/build.ps1 -OutputDirectory tests/security-runtime -EnableQa
./tools/dotnet/dotnet.exe ./tests/security-runtime/PrivateChat.dll --self-test
./tools/dotnet/dotnet.exe ./tests/security-runtime/PrivateChat.dll --ui-security-test
./tools/dotnet/dotnet.exe ./tests/security-runtime/PrivateChat.dll --self-test --network --public-relays
```

The network test creates two synthetic local identities, routes both through Tor and tests an invitation, correct/incorrect safety codes, bidirectional text delivery, offline delivery, and retention of mandatory SOCKS after Tor is stopped. It scans synthetic profile files for the test password and message markers in UTF-8/UTF-16 before and after stopping clients. This limited marker scan is not proof that every possible fragment is absent from all machine storage. A completed bootstrap alone does not pass these checks. QA outputs stay in `tests/security-runtime`; test databases use random, unsaved passwords and are separate from the user's data. UI tests inspect Copy/Cut event data without overwriting the user's clipboard.

The distributed build contains neither the self-test implementation nor any screenshot/test-password launch modes. QA builds must not be distributed. Current local evidence is recorded in `dist-v0.8.1/security-qa.json`. The root `安全加固与测试报告.txt` describes an older build.

The upstream links below identify the exact v7.0.3 sources used to determine the native ABI and JSON protocol. `reference/LICENSE` preserves the core license. Bundled native binaries originate from the upstream release; they were not rebuilt from source here.

## Upstream sources

- [SimpleX v7.0.3 release](https://github.com/simplex-chat/simplex-chat/releases/tag/v7.0.3)
- [SimpleX C API](https://github.com/simplex-chat/simplex-chat/blob/v7.0.3/src/Simplex/Chat/Mobile.hs)
- [SimpleX command protocol](https://github.com/simplex-chat/simplex-chat/blob/v7.0.3/src/Simplex/Chat/Library/Commands.hs)
- [Tor expert bundle](https://download.torproject.org/tor/)
- [Tor signature verification](https://support.torproject.org/tor-browser/getting-started/verifying-tor-browser/)
- [Windows clipboard privacy formats](https://learn.microsoft.com/en-us/windows/win32/dataxchg/clipboard-formats)

Application source is distributed under AGPL-3.0; see `LICENSE`. SimpleX and other bundled components retain their own notices. The app is an independent experimental client and is not an official SimpleX or Tor release.

## Local 0.3 improvements

The self-contained Windows x64 build includes .NET 8.0.31. Extract the whole directory and run PrivateChat.exe; no separate .NET install is needed. This paragraph describes the earlier portable preview; use the installer linked above for downloads. Bundled runtime security updates require rebuilding the application.

Added in-app usage help, a 90-second Tor waiting hint, distinct database-directory access errors, and accurate clipboard failure feedback. Tor bootstrap readiness is labeled separately from contact connectivity and message delivery. Current test evidence is in dist-v0.8.1/security-qa.json; older reports describe 0.2 only.

## Local 0.4 conversation fixes

Contact switches immediately remove the previous conversation. A request revision prevents late responses from replacing a newer response or restoring a conversation after switching/locking. Incoming refreshes preserve the scroll offset while the reader is above the bottom. Delivery labels distinguish relay acceptance, partial delivery, failed authentication, warning and invalid hash receipts. In 0.4 only the latest 100 items were displayed. Version 0.5 adds cursor pagination in both directions, with at most 100 visible items per page. History pages remain stable during incoming updates, and Return to latest resumes automatic updates. Synthetic native tests traverse 225 encrypted local items in both directions; WPF viewport tests verify stable reading anchors when a latest-page boundary moves.

## Custom SMP relays (0.6)

Open 消息中继设置 after unlocking. Edit full smp:// addresses, one per line (up to 8 for a custom list), test through Tor, then enable the tested list. Editing invalidates test results. The native parser checks address syntax; native configuration validation checks server constraints. Configuration and optional access passwords are stored by the core in the encrypted database, not a separate plaintext preferences file. Restore presets reenables all bundled preset SMP servers and removes custom entries; the initial core selection may have enabled only a subset. Existing contact queues are not migrated. Sending to peers may still use their chosen relays. XFTP and chat-relay configuration are preserved.

A successful connectivity test is temporary evidence, not a trust endorsement or availability guarantee. Runtime failures do not silently switch to direct networking or automatically replace the configured relay list.

## Quiet interface and wide-window layout (0.8)

Neutral light gray surfaces, dark typography, modest corner radii, and blue reserved for primary actions and small selection accents. Message bubbles use soft neutral/pale-blue fills. The photographic rose, blur, translucent materials, directional highlights and shadows from 0.7 have been removed from the interface. The unused generated asset is excluded from the public source tree and application.

The unlock composition is centered and capped at 860 device-independent pixels, with a 360-pixel form. A single 840-pixel reading column contains the conversation header, history navigation, messages and composer. The sidebar stays 248 pixels wide. Enlarging the window now adds outer whitespace rather than stretching the input field or separating messages across the entire screen. Small unlock forms remain scrollable.

The regression renderer reproduces the old wide-window failure, then validates the new layout at minimum, standard, 1080p, 2K and ultrawide client sizes, including a wide-to-small resize sequence. 150% raster output is also inspected; this is not a physical multi-monitor DPI test. Screenshots contain synthetic contacts and messages only. The separate existing UI/security suite uses isolated encrypted profiles and the final release worker. Actual test evidence is in dist-v0.8.1/security-qa.json. No new network acceptance or independent audit is claimed for this visual update.

## Review and lifecycle fixes (0.8.1, 2026-10-01)

This is a source review and local regression pass, not an independent security audit. It fixes three concrete lifecycle weaknesses without changing the database format or the accepted visual design:

- Delayed safety-code results can no longer reopen a closed dialog or overwrite a later conversation/dialog action. Errors from old modal operations are also discarded.
- Windows suspend and resume notifications queue a high-priority lock, clear sensitive UI fields and stop the core/Tor. Regression tests inject the real handler notifications; physical sleep/hibernation on multiple PCs remains unverified. This does not promise memory erasure or protection of hibernation files.
- A broken core response pipe now closes the supervised core and signals the UI to lock. An additional disposal check prevents already-queued commands from being written after shutdown. The old compiled release left the core running in the injected pipe-failure scenario; the new compiled release terminates it and issues one unexpected-exit notification.

Regression evidence stays under `tests/audit-before`, `tests/audit-after` and `tests/v0.8.1-qa`. All profiles are synthetic; the user's actual database and clipboard are not used. Native/network tests apply the UI's receipt/file privacy defaults on both endpoints. The current release report records the result; do not infer a network pass from a completed Tor bootstrap.

Remaining work before recommending regular use:

1. Encrypted backup/restore with tested recovery and interrupted-restore handling; password change and upgrade/rollback recovery. There is currently no recovery UI.
2. Unread indicators and contact deletion/blocking; clearer recovery for failed or ambiguously submitted messages. Adding attachments, groups and calls is optional for a usable text-chat scope.
3. Acceptance on two physical Windows computers and different networks, including offline/restart delivery, bridge modes, custom authenticated relays and actual sleep/resume. Sustained use and larger contact/message stores need validation.
4. Independent security review and process-scoped DNS/UDP/TCP leak tests. Current configuration assertions and sampled TCP observations do not cover all egress paths.
5. Signed distribution and a verified update process. The bundled runtime requires rebuilding to receive fixes. [.NET 8 support ends on 2026-11-10](https://github.com/dotnet/core/blob/main/release-notes/releases-index.json); migration to a supported LTS runtime needs compatibility testing before then.

Upstream dependencies were checked against official release information. SimpleX 7.0.3 is the current stable release; [7.1.0-beta.5](https://github.com/simplex-chat/simplex-chat/releases/tag/v7.1.0-beta.5) contains additional parser/remote-access hardening. For example, [forwarded-message depth limiting](https://github.com/simplex-chat/simplex-chat/pull/7614/files) is absent from the corresponding 7.0.3 parser. Remote access and groups are not exposed in this client's UI, but that alone does not establish that every core parser path is unreachable. Applicability and a compatible patched dependency remain a pre-release review item; no exploit or chat-content leak is claimed to have been reproduced. The stable native dependency is unchanged in this patch. Tor is already bundled at [0.4.9.13, the September security release](https://forum.torproject.org/t/security-release-0-4-9-13/22178).
