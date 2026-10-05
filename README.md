# Private Chat — Windows development preview 0.10.12

The installed 0.10.11 maintenance version added Windows CurrentUser DPAPI binding around the existing scrypt/AES key envelope, .NET 10.0.12 and patched OpenSSL 3.6.5. Older profiles require an explicit offline upgrade with their original password; the upgrade retains identity and messages. Reinstalling Windows or losing the user/system keys may make a bound profile unrecoverable. The detailed dependency decisions and outstanding upstream review items are in [SECURITY-MAINTENANCE-0.10.11.md](SECURITY-MAINTENANCE-0.10.11.md). Historical results below apply only to their stated builds; the completed 0.10.11 capture applies only to its recorded binary hash.

Version 0.10.12 backports the forwarded-message depth limit onto SimpleX 7.0.3 and bundles OpenSSL 3.5.9. Native regression, local security, database round-trip and Tor interoperability checks passed; see [the stable backport report](SECURITY-PARSER-BACKPORT-0.10.12.md). It does not inherit the older build's ETW/soak/sleep results.

A minimal Windows chat client around a locally patched SimpleX 7.0.3 native core. This is a development preview, not an independently audited messenger.

## 下载与安装

[浏览器下载页](https://dayejetty-code.github.io/private-chat-desktop/) · [GitHub Releases](https://github.com/dayejetty-code/private-chat-desktop/releases/tag/v0.10.12)

下载 `PrivateChat-0.10.12-Windows-x64-Setup.exe`，双击按中文向导安装，再打开桌面快捷方式。无需解压、另装 .NET 或管理员权限。支持 Windows x64，最低 Windows 10 2004，推荐仍受支持的 Windows 11。更新前退出应用；更新和卸载保留本地加密资料库。

0.10.12 使用稳定内核的解析器回移补丁，继续保留 Windows 用户绑定加密、TXT 匿名文件名、未读提示与连接修复，并移除全部备份入口。安装不会自动升级真实资料的密钥格式。另存备份与导出文件仍不随身份销毁。用户已反馈早期文字聊天在不同网络下的基础流程成功。此版真实跨电脑文件验收、新一轮 ETW/长测/睡眠测试、独立安全审计和代码签名尚未完成。散列校验不能代替独立发布者签名。

## Run

Use the installed desktop or Start menu shortcut. Create a local database with a long passphrase (minimum 16 characters). New profiles are bound to the current Windows user as well as the passphrase; reinstalling Windows or losing the user/system keys can permanently lose access. Existing profiles retain their original protection until explicitly upgraded. Backup creation and archive restore are absent. The passphrase is not saved; there is no recovery service. Wait for Tor to connect, then use **添加联系人** to generate or paste a full, one-time invitation link. Sending requires comparing and entering the contact security code through a trusted independent channel. This release uses the same data directory as 0.1; development tests use isolated synthetic profiles.

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

Not implemented in this preview: calls, groups, invitation QR scanning, password changes, automatic signed updates. The core uses its upstream preset independent relay operators. Existing full invitations from compatible clients can be accepted.

## Current Windows-user binding (0.10.11)

`PCKEY003` wraps the authenticated 100-byte `PCKEY002` envelope described below with Windows DPAPI CurrentUser, without the LocalMachine flag. Both the chat passphrase and the Windows user's protection keys are required. The stored envelope is bounded to 4104 bytes before allocation/derivation. DPAPI failure stops unlock; it cannot silently downgrade protection or create a replacement key. Returned native buffers are cleared before freeing. Older formats remain readable for an explicit offline upgrade that rotates the database key and preserves identity and history.

New or upgraded bound profiles require **0.10.11 or later**. The upgrade is not reversed by opening an older executable. Windows roaming/recovery and a compromised logged-in user or administrator limit the binding; it is not TPM sealing. Reinstalling Windows, deleting its user or losing system keys may permanently lose the chat data even with the password. See the maintenance record above for tests and outstanding dependency review items.

## Password wrapping and backup removal introduced in 0.10.10

Version 0.10.10 introduced an OS-generated 256-bit database secret, encoded as a 64-character hexadecimal SQLCipher passphrase. The 100-byte `PCKEY002` envelope contains an 8-byte format tag, 32-byte salt, 12-byte nonce, 32-byte encrypted secret and 16-byte AES-GCM tag. AES-256-GCM authenticates the complete header. Its wrapping key is derived with RFC 7914 scrypt: N=131072, r=8, p=1 (128 MiB cost, 192 MiB implementation limit). Work factors are fixed by the authenticated version, not accepted from file-controlled parameters. Derivation failure stops the operation; it never silently falls back to PBKDF2. The already hash-pinned OpenSSL library implements scrypt and is loaded by absolute path with restricted dependency search and configuration-file loading disabled. See [RFC 7914](https://www.rfc-editor.org/rfc/rfc7914.html) and the [OWASP work-factor guidance](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html#scrypt).

This strengthens resistance to offline password guessing; the AES key size and SimpleX end-to-end protocol are unchanged. It does not compensate for a predictable password or a compromised unlocked endpoint. New profiles require at least 16 characters; older passwords remain valid for compatibility. Passwords and keys are not saved in plaintext. Temporary byte buffers are cleared; managed strings, native internal copies and OS paging cannot be guaranteed zeroized.

Legacy direct-password profiles and `PCKEY001` (PBKDF2-HMAC-SHA256, 600,000 iterations) profiles remain readable. They are not silently upgraded. Open **数据销毁**, enter the existing password, then select **升级本地加密保护**. Upgrade stops chat and Tor, exports and validates both encrypted databases with a fresh random key, then installs them with a durable transaction. It preserves identity, contacts, messages and attachments. After commit, only this migration's old copy is overwritten, verified and removed. Interrupted committed cleanup retries at startup without restoring the old key; failures before commit retain rollback recovery. Existing older history, manual archives and exported TXT remain. New or upgraded profiles require **0.10.10 or later**; older versions cannot open PCKEY002.

Migration uses the existing restricted native core and SQLCipher encrypted export without a plaintext database. Unicode/space-containing legacy passwords are supported. Original database `user_version` values are preserved. Native SQL commands are transaction-wrapped, so attached databases are closed using `CloseStore`. The current key envelope is read through its verified handle with an exact 100-byte bound. Unknown formats and failed authentication cannot open the profile or create a replacement key.

Production builds exclude archive creation/extraction and backup creation, restore and deletion entrypoints and UI. Shared fixed-path staging and recovery helpers remain for safe key migration; legacy archive code is compiled only into QA for compatibility fixtures. There is no scheduled or manual backup operation in the delivered app. Retained older backups can still be used by compatible older software and are not invalidated by this change.

Identity destruction first attempts all reviewed local key envelopes, then overwrites and removes managed files. This is application-level cleanup, not certified media sanitization. Older SSD pages, pre-upgrade copies, filesystem journals, snapshots, paging/hibernation, copied keys and peer records remain outside its guarantee. No disk-wide commands or system recovery settings are changed.

## Local identity destruction (introduced in 0.10.7)

**数据销毁 → 检查本机资料** stops chat and Tor, checks the existing passphrase offline and lists the managed files. Reviewing the list does not delete files; opening and closing the encrypted store for validation can update database pages. After acknowledging the warning and typing **销毁账号与聊天数据**, one action deletes both identity databases and sidecars, contacts, chat history, attachment caches, Tor state, internal restore history/transactions and backup working copies. The single-profile lock remains held. Separately saved `.pcbackup` files, exported TXT and unrecognized root entries remain; retained backup plus its passphrase can still restore the old identity and history. Exported TXT remains readable independently.

The former **单独选择要删除的备份…** action is removed in 0.10.10. The app does not scan disks or delete separately saved archives during an update or identity destruction. Older files can be managed outside the app in Windows.

Deletion uses verified Windows file handles, rejects reparse points/hard links/network paths and checks file identity again before removal. A durable `.delete-pending` marker precedes profile deletion. Failure, cancellation or interruption leaves the profile blocked from unlocking or restore; review the remaining files and confirm cleanup again. Automatic restore rollback cannot resurrect a pending-deletion profile. There is no rollback or automatic retry of deletion, and no recycle-bin copy. Version 0.10.8 adds key-envelope invalidation as described above; this remains distinct from SSD sanitization or proof against forensic recovery. Peer copies, relay copies, OS snapshots and cloud/sync copies are outside this operation. See [NIST media sanitization guidance](https://csrc.nist.gov/pubs/sp/800/88/r2/final) for the distinct device-level problem.

Version 0.10.9 first attempts all reviewed keys even when one is occupied or Stop is requested. Stop is honored before bulk file work and between its chunks. Remaining managed files receive one complete zero overwrite through an exclusive write-through handle, a durable flush and full read-back verification before deletion. Sparse, compressed, EFS and additional-stream files, or filesystems that cannot enumerate streams, are refused without claiming they were cleared. An entry failure does not prevent attempts on other reviewed entries; any failure retains the pending marker. Progress shows verified logical bytes; final residual enumeration must succeed. Successful identity destruction closes the app, ending the current UI process.

Read-back verifies OS-visible file contents, not old physical pages, allocation slack, filesystem journals, snapshots, pagefiles or hibernation. Process exit is not proof of physical-memory sanitization. No free-space wipe, whole-disk command or system recovery setting is changed.

Local synthetic tests cover full identity removal, fresh identity initialization, retained backups/exports, partial failure, exact-file selection and confirmation UI. They do not establish storage-media sanitization, independent audit or new network-capture results.

## Unread and recovery (0.10.6)

Contact badges read native `chatStats.unreadCount`. A foreground, unobscured conversation marks only visible item IDs as read with the native `/_read chat items` command. Background windows, dialogs, locked views and off-screen history retain unread status. Read receipts stay disabled. There are no OS notification bodies or names in window titles.

Only terminal `sndErrorAuth` / `sndError` text items expose **重试发送**. Queued, sent, warning, received, deleted and file items do not. The serialized worker rereads the source item, safety code and strict Tor configuration before submitting a new native item. A custom `privatechat_send_attempts` table in the existing SQLCipher chat store commits an intent first and keeps a unique source ID; it stores no additional message text. Lost replies or failed post-send updates remain pending across restarts. **已核对记录** acknowledges only the displayed pending tokens without sending. This suppresses accidental local retries, not every form of duplicate delivery. TXT failures still require selecting the file again.

**连接修复** reads native connection statistics, reuses the existing Tor reconnect path, and only calls non-forced `/_sync` for supported `allowed` / `required` states. Healthy, unknown or already synchronizing connections are not forcibly reset. Changed safety codes require explicit verification. QA reports are under `tests/conversation-qa`; synthetic failure/required-sync fixtures must not be described as real packet loss or a reproduced ratchet compromise.

## Historical backup format (0.10.5–0.10.9; removed from production in 0.10.10)

The following describes older releases and QA fixtures only. **本地备份** was available before unlocking. Creating a `.pcbackup` uses the current database passphrase; restoring uses the passphrase at backup time. Backup/restore stops chat and Tor, retains the single-profile lock, and only opens a temporary copy in the restricted native worker. Neither operation starts an agent or Tor or uploads anything. A completed backup includes both encrypted databases, their SQLite sidecars, and local attachment folders. Tor state, unrelated logs, installed binaries and `instance.lock` are excluded. Finish/cancel active attachment tasks first. The limit is 2 GiB and 50,000 files; allow disk space for staged copies.

The outer envelope uses .NET AES-256-GCM with PBKDF2-HMAC-SHA256 (600,000 iterations), a random 32-byte salt and a random 8-byte nonce prefix plus a 32-bit chunk counter. Header, sequence and expected length are authenticated; an authenticated end record and EOF check reject truncation, reordering and appended data. Manifest and filenames are encrypted. Archives are streamed without compression or a plaintext intermediate archive. Completed exports are reread and authenticated before an atomic rename; existing exports are never overwritten.

Restore authenticates the whole archive before opening a staged SQLCipher profile, checks both databases, and converts legacy attachment paths to portable relative paths. Only fixed profile entries can be replaced. A durable local journal supports startup rollback after an interrupted restore. Original encrypted files remain under `.restore-history/<restore-id>/previous`; they are not automatically deleted or merged. If startup recovery fails, the app refuses to open the profile. This is not a guarantee against failing storage hardware or all power-loss behavior.

Choose a local unsynced folder or USB drive. UNC/network drives, reparse points and configured OneDrive roots are rejected. Other backup/sync software can still copy your chosen folder; the application cannot detect every provider. The default location is `%LOCALAPPDATA%\PrivateChatBackups`. Keep the backup passphrase separately. Backups preserve older content, including content you later delete in the app. They do not include TXT files you separately exported outside the profile. No scheduled or cloud backups are implemented.

Restore replaces the current profile; it does not merge messages. Close the source device first and do not run two copies of the same identity. An old snapshot may require contact connection recovery; later messages cannot be reconstructed from that snapshot. See the upstream [migration explanation](https://simplex.chat/blog/20240323-simplex-network-privacy-non-profit-v5-6-quantum-resistant-e2e-encryption-simple-migration.html).

## Windows network isolation (0.10.4)

The worker has no Internet capability. Tor and the selected bridge helper have a separate network-capable token within the same package. Direct core sockets and system DNS are denied by Windows; no ordinary-process fallback or global loopback exemption is used. TXT selection/export is brokered through local pipes. See [NETWORK-ISOLATION.md](NETWORK-ISOLATION.md) for exact boundaries and validation.

## Historical Windows bridge startup repair (0.10.3)

The obfs4 and Snowflake helper now launches using a fixed relative executable path from the verified Tor runtime working directory. Tor's transport directive splits on whitespace instead of interpreting shell-style quotes, so quoting an absolute executable path prevented launch. A regression probe runs from a directory containing spaces. Runtime hash checks, mandatory Tor routing and process supervision remain enabled. Exact bootstrap and delivery outcomes are recorded in the local 0.10.3 test report; slow or unreachable upstream relays are not counted as successful delivery.

## Attachment cache maintenance (0.10.2)

This patch follows the 0.10.1 feature line and excludes the experimental 0.11 Tor-circuit viewer and control listener. Cancellation is available for pending tasks even when the contact is unverified or the UI is not network-ready. It requires an unlocked, running local core; locking ends that process. Sending, accepting and exporting keep their existing verification requirements.

The **附件缓存** panel counts encrypted files, `file-temp` and `file-assets`. New sends/receives are admitted by the serialized worker only if usage plus conservative pending-task reservations fit a 512 MB budget and the disk has headroom. Reservations are derived from native task metadata in the encrypted database, persist across restarts, and are released when tasks finish, fail or cancel. A task reserves four times its declared size plus 4 MB for padding/chunks/working copies; the full reserve is retained while active, so displayed usage plus reserve can exceed final storage. This is admission control, not an OS-enforced disk quota: previously queued jobs, legacy native protocol paths and unrelated disk writers are not automatically terminated.

**清理并锁定** confirms loss of completed local attachment copies, closes the owned core and waits for its exit before deleting eligible leaves. Active/shared/unknown files are retained conservatively; orphan temporary files are only eligible with no active task. Paths, file size and modification times are rechecked, and changed or occupied files are skipped. Chat databases, text history, original documents and exported copies are outside cleanup. Completed cards whose ciphertext is gone explain why export is unavailable. Save anything needed before cleanup. Filesystem deletion is not a forensic secure erase.

## Encrypted TXT attachments

After verifying a contact, use **发送 TXT** for a local `.txt` plain-text document. Both the source and normalized output must be at most 25 MB. The worker strictly decodes UTF-8 (optional BOM) or BOM-marked UTF-16 LE/BE, rejects invalid encodings and non-text control characters, and stages UTF-8 without a BOM through the native `chat_write_file` encryption API. It does not create a plaintext temporary file or change the original. Renamed PNG/ZIP/PDF/RTF test inputs are rejected; this is a bounded text policy, not a general file-format detector or antivirus. Other encodings should be saved as UTF-8 using a local text editor.

The core's XFTP protocol encrypts the attachment end to end through mandatory Tor. The recipient selects **接收文件**, then **另存为…**. Export authenticates the ciphertext and validates/normalizes the text before creating a `.txt` output. A malicious or older peer can send any bytes; a TXT filename alone is never sufficient for export. Non-TXT offers have no receive/export actions. There is no automatic preview, execution or open-file button. An upload-complete label means relay upload finished, not that the recipient downloaded it. A pending transfer can be cancelled; saved copies cannot be recalled. Expired transfers may require resending.

The sender stages ciphertext under a random cache name. Each new send uses a fresh `文档-<24 random hexadecimal characters>.txt` display name generated with the OS cryptographic random generator, independently of source name, path, content and time. The confirmation, encrypted message, file cards and default export filename share that label. The original filename is not sent and the source is not renamed. Existing messages and queued jobs retain their previous names. Incoming XFTP files have encrypted local sources, and export authenticates the entire file before creating the user-selected output. Export never overwrites an existing file or writes into the profile; remote/device/ADS paths and reparse-point paths are refused. Exported files receive Windows' Internet-zone marker. The core does not send the original local path to the contact. Safety-code verification is rechecked before sending or accepting files, and switching contacts or locking invalidates pending file dialogs.

Only the main text stream is read; original NTFS alternate data streams are not copied, and Office/PDF/image metadata containers are not accepted as attachment formats. Text can still contain names, addresses, links, invisible Unicode formatting or other personal information. These are preserved, as are tabs and line endings. Random filenames do not hide text, size, timing or names already sent by old/other clients. Exported files are ordinary plaintext outside the protected profile and are not removed on lock. Forced termination/power loss during export can leave a partial output. Conversion changes byte hashes for BOM/UTF-16 inputs even though text is preserved. Use the attachment-cache panel to inspect usage and clean eligible copies; the budget includes the three attachment directories and unfinished task estimates. Do not delete the profile to make room.

Before upgrading from 0.9.0, complete or cancel pending non-text transfers in that version. The new restriction governs newly selected attachments and exports; it does not purge jobs already queued in the core. Existing history and encrypted caches are preserved. An older pending attachment still exposes a cancel action where its native state allows it.

The manual-acceptance assertion applies to this XFTP file flow. Upstream legacy inline voice attachments may still be cached by the native core under its own rules; their local encryption setting is enabled before network startup, and this UI offers neither playback nor download/export of unsupported SMP attachments. The app does not claim that every native attachment type waits for manual consent. Optional call/group interfaces are absent, but native protocol parsers remain part of the dependency's attack surface.

File tests use synthetic data, including native local encryption/export, corrupt-ciphertext rejection, two isolated endpoints sending both ways through Tor, offline reception, cancellation and failure of Tor. File UI checks cover the minimum, normal and wide layouts. These are first-party checks, not an independent audit or physical cross-PC file acceptance. Exact results for the locally packaged build are recorded in its `security-qa.json`; QA output must not be distributed.

## Security limits

End-to-end encryption does not protect an already compromised/unlocked endpoint, recipients who save messages, or every network traffic-correlation attack. The parent UI holds decrypted text while unlocked. Process termination is not a guarantee of physical memory erasure; OS paging/crash dumps and external screenshot/clipboard software remain outside this prototype's boundary. The application itself is unsigned and has not undergone an independent security review.

Runtime hashes are embedded in the managed application; replacing the external manifest with an empty/edited list is rejected. Core DLL dependencies are loaded from the pinned core directory and System32, excluding the current directory/PATH from that load. These checks are not an independent publisher signature: an attacker able to replace the application can change the checks too. The original baseline SimpleX MSI was checked against its release SHA-256; the current locally built core has separate source, patch and binary hash records. The Tor expert bundle was checked against the published SHA-256 and its OpenPGP signature, with the primary key fingerprint verified against Tor's official documentation. Builds recheck its archive hash and extract it afresh.

The core now has a Windows AppContainer boundary with zero network capabilities. BFE and Windows Firewall must remain enabled. The OS restricts connections to the application package; the existing strict SOCKS policy selects Tor within that package. See NETWORK-ISOLATION.md for the transport/GUI boundary, positive-control socket and DNS tests, and remaining limits. This is not exhaustive packet capture or protection against an administrator changing Windows policy. Cross-machine file acceptance and independent security review remain outstanding.

## Source structure

| File | Responsibility |
|---|---|
| `src/MainWindow.xaml` | Restrained WPF interface with bounded reading and unlock layouts |
| `src/MainWindow.xaml.cs` | Unlock, conversations, invitations, safety-code verification, UI lifecycle |
| `src/MainWindow.Relays.cs` | Relay settings dialog, test-before-enable gating and sensitive-field lifecycle |
| `src/MainWindow.Files.cs` | File selection, manual receive/export, progress cards and stale-action guards |
| `src/FileCache.cs` / `src/MainWindow.Cache.cs` | Persistent task reservations, bounded cache inventory and offline cleanup UI |
| `src/FileTransfer.cs` | File protocol states, limits, path validation and native command construction |
| `src/TextDocument.cs` | Strict text decoding, UTF-8 normalization, limits and safe user-facing rejection reasons |
| `src/RelaySettings.cs` | SMP validation, encrypted core configuration, Tor policy checks and readback |
| `src/HistoryPage.cs` | Bounded cursor pagination with older/newer/latest navigation |
| `src/MessageViewport.cs` | Preserve the visible message anchor during refresh |
| `src/CoreWorker.cs` | Native SimpleX C ABI in a separate child process |
| `src/CoreClient.cs` | Parent IPC client and strict network policy |
| `src/TorService.cs` | Private Tor process, bridge modes and bootstrap state |
| `src/TorRoutes.cs` | SAFECOOKIE-authenticated, bounded read-only circuit and stream snapshots |
| `src/MainWindow.TorRoutes.cs` | Actual node panel, manual refresh and lifecycle clearing |
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
./tools/build.ps1 -OutputDirectory dist-v0.10.6 -SelfContained
./tools/build.ps1 -OutputDirectory tests/security-runtime -EnableQa
$env:PRIVATECHAT_QA_WORKER = (Resolve-Path ./dist-v0.10.6/PrivateChat.exe).Path
./tools/dotnet/dotnet.exe ./tests/security-runtime/PrivateChat.dll --ui-security-test
./tools/dotnet/dotnet.exe ./tests/security-runtime/PrivateChat.dll --self-test --network --public-relays
./tools/dotnet/dotnet.exe ./tests/security-runtime/PrivateChat.dll --file-test --network
./tools/dotnet/dotnet.exe ./tests/security-runtime/PrivateChat.dll --file-ui-test
./tools/dotnet/dotnet.exe ./tests/security-runtime/PrivateChat.dll --text-file-test
./tools/dotnet/dotnet.exe ./tests/security-runtime/PrivateChat.dll --conversation-test
./tools/dotnet/dotnet.exe ./tests/security-runtime/PrivateChat.dll --conversation-ui-test
./tools/dotnet/dotnet.exe ./tests/security-runtime/PrivateChat.dll --conversation-test --network
Remove-Item Env:PRIVATECHAT_QA_WORKER
```

The network test creates two synthetic local identities, routes both through Tor and tests an invitation, correct/incorrect safety codes, bidirectional text delivery, offline delivery, and retention of mandatory SOCKS after Tor is stopped. It scans synthetic profile files for the test password and message markers in UTF-8/UTF-16 before and after stopping clients. This limited marker scan is not proof that every possible fragment is absent from all machine storage. A completed bootstrap alone does not pass these checks. QA outputs stay in `tests/security-runtime`; test databases use random, unsaved passwords and are separate from the user's data. UI tests inspect Copy/Cut event data without overwriting the user's clipboard.

The distributed build contains neither the self-test implementation nor any screenshot/test-password launch modes. QA builds must not be distributed. The last complete installer baseline is recorded in `dist-v0.10.4/security-qa.json`; 0.10.5 backup evidence is under `tests/backup-qa`, and 0.10.6 local candidate evidence is under `tests/conversation-qa`. The root `安全加固与测试报告.txt` describes an older build. Historical reports describe their respective versions only.

The upstream links below identify the v7.0.3 sources used for the native ABI and JSON protocol. `reference/LICENSE` preserves the core license. The current core is locally built from commit `b3907c9de7a2c596075762bf05ba3e1d2532aff1` with the PR #7614 backport in `tests/security-fixes-0.10.12/native-backport/parser-fix.patch`; it is not an unmodified upstream release DLL. Its build inputs, hashes and validation are recorded in `SECURITY-PARSER-BACKPORT-0.10.12.md`.

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

1. Password change, broader upgrade/rollback compatibility, and independent review of the version 2 key envelope and migration flow. Backup/restore was introduced in 0.10.5 and removed in 0.10.10; key-upgrade interruption tests remain distinct from physical power-loss testing.
2. Contact deletion/blocking and broader attachment retry UX. Unread indicators, guarded failed-text retry and connection repair are implemented in 0.10.6; attachment cache management is also present. Groups and calls are optional for a usable chat scope.
3. Physical cross-PC file acceptance and broader network/compatibility coverage, including bridge modes, custom authenticated relays and actual sleep/resume. User-reported basic text acceptance on different networks is recorded separately in `installer/TESTING.md`. Sustained use and larger contact/message stores need validation.
4. Independent security review and process-scoped DNS/UDP/TCP leak tests. Current configuration assertions and sampled TCP observations do not cover all egress paths.
5. Signed distribution and a verified update process. The candidate bundles .NET 10.0.12 and OpenSSL 3.5.9; servicing still requires rebuilding. The forwarded-message depth backport is validated in the candidate, while broader upstream changes remain separate review work. See `SECURITY-PARSER-BACKPORT-0.10.12.md`.

The selected stable base is SimpleX 7.0.3. Its original parser lacks [forwarded-message depth limiting](https://github.com/simplex-chat/simplex-chat/pull/7614/files); the current default build includes that verified local backport. The native test reproduces acceptance of nested forwards before the patch and expected rejection afterward, without demonstrating a remote exploit or chat-content leak. Other development-branch batch/remote-access changes were not included or declared resolved. Remote access and groups are absent from the UI, which alone does not prove every parser path unreachable. Tor remains pinned to 0.4.9.13.
