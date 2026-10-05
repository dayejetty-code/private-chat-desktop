## 0.10.4 Windows AppContainer egress boundary

See NETWORK-ISOLATION.md for the implementation and threat boundary. The OS probe uses positive controls and actual UDP receivers, tests DNS cache-bypass failure, unrelated local proxies, IPv4/IPv6 direct sockets, process injection/child creation, and repeats egress checks after Tor stops while the core stays alive. Actual native-core messaging and TXT delivery remain separate acceptance tests. Both bridge modes must complete delivery using the current production worker; bootstrap alone is insufficient. Installer tests now launch the actual installed worker through the same AppContainer launcher and retain the bundled-runtime, in-use upgrade, reopen and data-preservation checks.

AppContainer and its ACLs are installation/user-specific, not per-contact isolation. Windows filtering services and all firewall profiles must be enabled; a global loopback exemption for this package is rejected. No system-wide rules or DNS settings are changed. Socket tests are first-party, local Windows tests, not an independent audit or a new comprehensive administrator ETW capture.
# Private Chat test scope

## 0.10.3 Windows bridge startup

The regression probe first reproduced failure to launch the bundled transport helper from an installation directory containing spaces. Both obfs4 and Snowflake use the same launch directive. The fix uses a fixed, whitespace-free relative path with the verified Tor runtime as the process working directory. This matches the whitespace tokenization in [Tor's transport configuration parser](https://tpo.pages.torproject.net/core/doc/tor/config_8c_source.html); shell quotes in the executable token are not removed by that parser.

The bridge probe checks actual helper creation, initial protocol negotiation, Tor bootstrap, strict native network-policy readback, safety-code agreement, bidirectional text, TXT/XFTP content hashes, guarded shutdown and child cleanup. It uses synthetic profiles, public relay hostnames over Tor and the final production core worker. One early obfs4 run established the Tor channel but timed out at a preset message relay; subsequent probes use the existing native relay test/save path before contact creation. Failures are retained separately from successful runs. Local connection-table sampling is not a new administrator ETW/DNS audit and does not prove universal absence of leaks. Exact outcomes are recorded in the release's local security-qa.json.

Earlier 0.10.2 ETW findings remain evidence for that historical build and scope only. This patch does not add a firewall boundary or the reverted 0.11 circuit viewer. Installation checks and release argument rejection run separately against the resulting package.

Cold bootstrap attempts also exceeded a 240-second limit while downloading Tor directory data. Later fixtures reused only downloaded public directory files and allowed up to 600 seconds. The focused obfs4 delivery fixture used one relay that passed its connection test after the wider fixture encountered relay timeouts. This is scoped functional coverage, not a claim that every built-in relay or fresh network environment works reliably.

## 0.10.2 cancellation and cache maintenance

This maintenance build follows 0.10.1 and excludes the 0.11 Tor circuit viewer/control listener. Regression fixtures use only isolated synthetic profiles. Cache checks exercise the final production worker: counting temporary/assets files, refusing over-budget admissions, persisted reservations across restart, terminal/unknown task states, and cleanup protection for active/shared files, changed files, external exports and encrypted databases. Cleanup runs after the owned worker exits. It is file removal, not secure erase, and the budget is admission control rather than an OS disk quota.

File UI checks exercise cancel availability independently of verification/readiness/network state while retaining send/receive/export gates, missing-copy labels and clearing cache details on lock. Actual Tor/XFTP tests run the UI cancellation handler against an unverified synthetic contact, a not-ready UI state and an unusable SOCKS route. Cancellation still needs a running local core: the first test stopped the agent entirely and got chatNotStarted, which is distinct from an unreachable network. Existing encryption, safety-code, text, file and lifecycle suites also run against the final worker. Exact outcomes belong to this build's local security-qa.json; installer results are recorded separately. These first-party checks are not independent review or physical cross-PC acceptance.

The historical 0.11 results below do not describe this build.

## 0.11.0 actual Tor circuit inspection (2026-10-03)

The new panel queries only the application's own loopback Tor control listener, using SAFECOOKIE mutual authentication and an expected-process-ID check. Parser/authentication tests cover endpoint validation, invalid proofs, wrong PID, multiline framing, size bounds, empty circuits, missing directory data, node order, stream association and removal of SOCKS authentication fields from display models. UI checks cover minimum/normal/wide layouts, closing, cancellation on lock and clearing node details. Live network checks exercise authenticated snapshots against actual Tor connections, cached node addresses, an inheritance-protected cookie-directory ACL permitting only the current Windows account, rejection of unauthenticated queries and wrong-cookie rejection. Results are recorded in the local build report after each run.

The panel samples circuits, not individual messages. It cannot show the recipient's nodes, prove delivery, or establish absence of bypass traffic. Addresses are Tor's published consensus data and GeoIP codes are local estimates. Node snapshots are not saved by the app; Tor retains its ordinary state/cache files. The controller adds a cookie-authenticated loopback listener; same-user malware and administrator access remain outside this application's protections. Bridge/onion variants are covered by display/parser cases, not a new comprehensive real-network acceptance run for every transport. Core worker tests use the final production worker; UI and controller tests use the QA build of the same source with the pinned runtime. Installer validation is recorded separately after packaging.

Final 0.11.0 checks passed: 21 route/authentication assertions, 76 native/network assertions including real circuit inspection and cookie ACLs, 74 UI/security assertions, 60 text assertions, 50 file assertions including Tor/XFTP, and 30 file/route UI assertions. All six QA launch switches were rejected by the production executable with exit code 64. Tests use synthetic profiles on one Windows PC. Installer validation is recorded separately after packaging.

## 0.10.1 anonymous outgoing filenames (2026-10-03)

Each newly selected TXT attachment receives a fresh cryptographically random `文档-<24 hexadecimal characters>.txt` label before confirmation. That same label is used by the send command, message cards and default export name. Original names are not included in the outgoing message; local source paths/names and bytes are not changed. The command builder rejects non-anonymous labels. Old messages, previously queued jobs and filenames received from older/other clients retain their existing names.

Checks cover rejection of original outgoing labels, source preservation, no source name/path in the outgoing payload, file cards at three sizes, actual Tor/XFTP reception of the confirmed label and a new label on repeated/offline delivery of the same source. Random filenames do not anonymize text, hide size/timing or replace independent security review. Exact final-worker results and installer validation are recorded in the accompanying local release report. The application enforces Tor for core chat/file connections; comprehensive continuous DNS/UDP/TCP egress validation across all bridge modes remains pending.

Final 0.10.1 worker checks passed: 60 text-document assertions, 71 native/network assertions, 74 UI/security assertions, 50 file assertions including real Tor/XFTP transfer, and 24 file-UI assertions. All five QA launch switches were rejected by the production executable with exit code 64. Only synthetic profiles on one Windows PC were used; installer validation is recorded separately after packaging.

## 0.10.0 text-only attachment preview (2026-10-03)

New uploads accept only .txt. The selection dialog and the worker enforce the suffix; the worker validates content and normalizes supported encodings to UTF-8 before directly writing an encrypted cache with the pinned core API. No plaintext staging file is used. Export validates authenticated peer bytes before creating a .txt output. Text content, names, Unicode formatting and line endings remain; this does not automatically anonymize documents.

Synthetic tests cover UTF-8 with/without BOM, BOM-marked UTF-16 LE/BE, invalid/truncated encodings, input and expanded-output size limits, disguised PNG/ZIP/PDF/RTF input, prohibited control bytes, original-file preservation, alternate data streams not being copied, no staged output on rejected uploads and no non-TXT exports. The actual network check includes a deliberately malformed attachment from the native protocol, to confirm a declared TXT name does not bypass the receiver's content check. Exact final-worker results are recorded in the local release report. Old counts below refer only to the historical builds.

Final 0.10.0 worker checks passed: 56 text-document assertions, 71 existing native/network assertions, 74 UI/security assertions, 49 file assertions including actual Tor/XFTP transfer, and 20 file-UI assertions. All five QA launch switches were rejected by the production executable with exit code 64. Tests used synthetic profiles on one Windows PC; network passes are not physical cross-device acceptance or comprehensive egress capture. Installer validation is recorded separately after packaging.

Existing native queues are preserved: complete or cancel old non-text transfers before upgrading. The UI does not accept/export non-TXT offers, but the upstream core may still process its previously queued jobs and legacy inline voice rules. This is a new-upload/export restriction, not an exhaustive ban on every native binary protocol path. Cross-PC file acceptance and independent security review remain pending.

## 0.9.0 encrypted file preview (2026-10-02)

The new file flow uses the pinned native core's XFTP protocol through mandatory Tor, with a 25 MB file limit and manual receive/export. QA uses random synthetic profiles, never the user's chat profile. The local release report records checks against the packaged worker: existing native/network and UI/security regressions, authenticated local file encryption/export, malformed paths and filenames, oversized offers, ciphertext tampering before export, Windows zone markers, real bidirectional XFTP delivery, offline receipt, cancellation and Tor failure. SHA-256 comparisons check that exported test bytes match the source. UI checks cover 960, 1440 and 2560 pixel widths and clearing file cards on lock.

This does not establish independent security assurance or physical cross-device file acceptance. The manual-receive check covers XFTP files; upstream inline voice attachments may be cached by the core under its separate rules, with local-file encryption enabled. No playback, automatic preview or execution is exposed here. Saved exports are plaintext; a forced process/power interruption can leave a partial export. File metadata is retained and malicious payloads are not scanned by this app. Cache management, comprehensive egress capture, long-duration transfers, large contact stores and interactive file-picker testing on a second PC remain pending. Transient relay/bootstrap timeouts are incomplete runs, never counted as passes.

Final 0.9.0 worker checks passed: 71 existing native/network assertions, 74 UI/security assertions, 48 file assertions including actual Tor/XFTP transfer, and 16 file-UI assertions. The first final-worker native/network run timed out at Tor bootstrap; an unchanged retry passed. All four QA launch switches were rejected by the production executable. Installer validation is recorded separately after packaging.

## 0.8.1 baseline

The application is a development preview, not an independently audited messenger.

Application checks on 2026-10-01: 73 UI/security assertions and 71 native/network assertions passed. These include encrypted store reopening, wrong-password rejection, runtime integrity, safety-code checks, mandatory Tor settings, custom relay certificate rejection, bidirectional Unicode text delivery, offline delivery and fail-closed lifecycle behavior. Two synthetic profiles on the same Windows PC were used. The first Tor bootstrap timed out; the retry completed. TCP snapshots observed only loopback destinations for the chat workers at the sampled times; this is not continuous DNS/UDP/TCP leak testing.

In the automated checks above, suspend/resume notifications were injected into the actual UI handlers; physical sleep and hibernation were not tested. Those checks did not include physical cross-device acceptance. No independent audit, code signing or automatic verified updates have been completed. Encrypted backup/restore UI, unread indicators and contact blocking are still pending. A passed test is not a guarantee of confidentiality against compromised devices, recipients retaining messages or traffic-correlation attacks.

Installer validation is recorded separately in the source repository and accompanying release notes. Installation changes program files and shortcuts; the encrypted profile remains in `%LOCALAPPDATA%\PrivateChatDesktop`. It must not be removed by the uninstaller.

## 用户反馈：不同网络手工测试（2026-10-02）

来源：用户在项目对话中报告，以下项目均成功。此记录是用户手工测试反馈，不是开发工具重新执行或独立见证的结果，不计入上述 73/71 项自动检查。

- 网络连接成功。
- 建立联系人连接成功。
- 双方安全码核验成功；用户观察到双方显示的安全码相同。
- 双向消息收发成功。
- 离线消息补收成功。
- 断网后重连成功。
- 锁屏与重启测试成功。

用户明确说明使用不同网络。具体安装版本/文件散列、设备和系统版本、网络类型、中继入口与网桥模式、消息数量、等待时间和复测次数未记录。锁屏与重启的成功反馈不等同于物理睡眠/休眠及所有故障时序已验证。本轮补充了基础使用流程在不同网络下可用的反馈，尚不能替代完整兼容性、长期稳定性和专项安全测试。

双方在同一联系人连接上显示相同安全码是预期行为：该码用于比对双方连接公钥的指纹，并非各自的资料库口令或私钥。程序通过原生核心读取并核验安全码；现有原生网络测试明确检查双方代码一致，并检查错误代码会被拒绝。实际核验时应通过另一个可信渠道取得对方显示的代码，不能仅复制自己屏幕上的代码来代替比对。参见 [SimpleX 官方安全说明](https://simplex.chat/docs/guide/privacy-security.html) 和 [连接说明](https://simplex.chat/docs/guide/making-connections.html)。

本记录不修改已经发布的 0.8.1 安装包，也不把用户反馈转写为独立安全审计结论。
