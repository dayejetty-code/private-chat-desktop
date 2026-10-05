# 0.10.11 dependency and local-key maintenance

Review date: 2026-10-05. This is a first-party maintenance record, not an independent audit or installer release approval. Network conclusions belong to the final, hash-matched report under `tests/network-stability-0.10.11/runs`; a running collector is not a pass.

## Dependencies

| Component | Candidate | Decision and remaining boundary |
| --- | --- | --- |
| .NET SDK / desktop runtime | 10.0.401 / 10.0.12 | Replaces SDK 8.0.425 / runtime 8.0.31. Microsoft release-metadata SHA-512 verified before extracting the SDK. The application bundles its runtime, so servicing requires rebuilding the application. |
| OpenSSL libcrypto | 3.6.5 | Replaces upstream bundled 3.0.15 with the MSYS2 Windows x64 build. The package SHA-256 was checked against the distributor's HTTPS package record; the extracted DLL is independently pinned in `dependencies.lock.json`. This is a distributor build, not an OpenSSL-project Windows binary or a claim of Authenticode signing. |
| Tor expert bundle | 15.0.24 / Tor 0.4.9.13 | Retains the already pinned security release and its existing signature verification record; rechecks bundled hashes. |
| SimpleX native core | 7.0.3 | Retains the current stable Windows core. 7.0.4 and 7.1.0-beta.6 are marked prerelease in the GitHub API. No blind database/protocol upgrade to a prerelease. |

OpenSSL guarantees API/ABI compatibility within major version 3, but that does not replace application tests. Its 3.6 branch is supported only until **2026-11-01**; a compatible patched 3.5 LTS distribution/build remains a near-term maintenance requirement. 3.0 is out of ordinary support. The September 29 advisories list fixes in 3.6.5; not every listed vulnerable API is necessarily reached by this application's libcrypto use. Updating the dependency is not evidence that an exploit or message disclosure occurred.

SimpleX follow-up review identified the forwarded-message recursion bound in PR #7614, plus additional batch and remote-access limits in subsequent development. The group and remote-access UI is absent here, but native parsers still exist. No exploit was reproduced, and this review does not establish those paths are unreachable. A compatible stable patched native core or an independently validated backport remains a release-review item. Do not label this build free of known upstream review concerns.

The NuGet vulnerability query has no external application PackageReferences to assess. It does **not** scan manually bundled SimpleX, OpenSSL, Tor, the .NET runtime or all transitive native source dependencies. Native checks and provenance are recorded separately; this is not an exhaustive SBOM vulnerability certification.

## Windows user binding

`PCKEY003` uses Windows DPAPI CurrentUser (UI forbidden; never LocalMachine) around the existing authenticated `PCKEY002` scrypt/AES-256-GCM envelope. The inner envelope still requires the chat password. DPAPI failure does not create a replacement key, downgrade protection, or unlock a database. Inputs are size-bounded; native returned buffers are cleared before freeing.

Legacy password-only, PCKEY001 and PCKEY002 profiles remain readable. An explicit offline upgrade with the original password rotates the database key and binds the new envelope to the Windows user. The already-tested transaction preserves identity/history and clears only its own committed temporary copy. Real user data is not automatically migrated by development tests. New or upgraded bound profiles require 0.10.11 or later; application backup creation, restore and deletion remain absent.

Binding is not TPM sealing or hardware-exclusive storage. Windows roaming/domain recovery and stolen system credentials/DPAPI keys are relevant exceptions. Same-user malware or an administrator controlling the unlocked machine may obtain decrypted secrets. Reinstalling Windows, deleting the user or losing system keys can make data permanently inaccessible even with the chat password. Old copies and exports are not retroactively upgraded.

## Completed validation, 2026-10-05

Candidate assembly SHA-256: `306E18546DABD7A6090EF76CD0B537B4DDA178148111EB60E8AD9456E56A2B8E`. The production binary was not rebuilt during the accepted run. Evidence is under `tests/network-stability-0.10.11/runs/20261005-195921`; `final-results.json`, `user-profile-review.json` and `evidence-sha256.json` must be read together.

| Check | Observed result |
| --- | --- |
| Local regression | 565 checks across 16 suites passed; production rejects all 9 tested QA entry flags. |
| OS isolation | 71 checks passed across direct Tor, obfs4 and Snowflake, including denied direct IPv4/IPv6, DNS and unrelated loopback access, with Tor stopped as well. |
| Network/stability scenarios | 103 checks passed: controlled transport interruption during TXT upload, queued-message recovery, Tor termination, both bridge modes, and file-content integrity. |
| Continuous observation | 3600.855 seconds, 57 successful bidirectional message rounds; maximum observed round trip 5.728 seconds. A TXT transfer also passed after the soak. |
| Actual sleep/resume | Windows Suspend at 21:09:46 and Resume at 21:12:16 Beijing time. Four production-UI checks passed: encrypted synthetic profile opens, lock/draft clearing/old-child termination, profile reopen and Tor reconnect. Separate Windows power events retained. |
| Completed ETW capture | 19:59:32–21:13:05 Beijing time; 98,471 filtered events, all five DNS/TCP/UDP positive controls, zero processing/parse errors, zero reported lost events, zero unexpected core-egress events. Capture covers the full scenario and sleep intervals. |
| End state | Test processes exited, temporary keep-awake request released, firewall remained enabled. Desktop shortcut remains `Private Chat 0.10.10.lnk`, targeting 0.10.10. |

The final report records `networkAndStabilityChecksPassed: true`. Its aggregate status remains `partial-or-review-required` because the original **whole-profile unchanged** gate is deliberately not waived: all 11 original files still have identical content hashes, but a 100-byte `PCKEY002` file appeared at 20:13:35. The user confirmed opening or operating the normal application around that time. This is consistent with user activity; the originating process was not independently observed. The new file was preserved and the baseline was not rewritten. It is not the new `PCKEY003` Windows-bound format, so the user's current profile has not been confirmed upgraded to Windows binding. The test harness did not unlock or migrate the real profile.

Production core process handles stayed in a narrow range (Alice 271 to 269, peak 272; Bob 267 to 268, peak 270), with 12 threads each. Alice private memory rose from 50.9 to 56.6 MiB, peak 60.0 MiB; Bob went from 53.8 to 52.6 MiB, peak 54.8 MiB. The test-only fault proxy's handles rose from 274 to 497, peak 503; the cause remains a harness follow-up. Do not generalize this hour of observation into a claim that every process is free of resource leaks.

Earlier runs remain available: `20261005-190222` has sleep evidence but incomplete protocol/scenario coverage; `20261005-191805` was interrupted by a reproduced status-file rename/read contention error. Bounded atomic-write retries passed a 44-check regression before the fresh run. Interval acceptance checks passed 14 cases, and analyzer rejection/health checks passed 10 cases. A quoting error in the final report script was fixed after capture; all suite PowerShell sources parse successfully. Report validation now distinguishes a key file's format from its mere presence.

Twenty synthetic legacy archive fixtures remain under `tests/hardening-qa/qa`: automatic approval rejected their deletion with `blocked by policy` and no further reason. No deletion occurred. These are generated QA samples, not user backup copies, and application backup features remain removed.

These results are first-party tests on one Windows computer. The transport cut was simulated, not a physical network-adapter disconnection. ETW covered declared synthetic processes and metadata, not message contents or all computer traffic; bridge DNS/UDP is separately identified. The soak exercised native cores and transport, not an hour of interactive UI use. There was no second-device Windows-binding test, independent security audit, code signing, installer acceptance or public release. The dependency follow-ups above remain open.

## Primary/distributor references

- [.NET release metadata](https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json)
- [.NET support and self-contained servicing policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
- [MSYS2 OpenSSL package and SHA-256](https://packages.msys2.org/packages/mingw-w64-x86_64-openssl)
- [OpenSSL September 29 advisories](https://openssl-library.org/news/secadv/20260929.txt)
- [OpenSSL compatibility and support policy](https://openssl-library.org/policies/releasestrat/)
- [Tor 0.4.9.13 security release](https://forum.torproject.org/t/security-release-0-4-9-13/22178)
- [SimpleX releases](https://github.com/simplex-chat/simplex-chat/releases)
- [SimpleX forwarded-message bound](https://github.com/simplex-chat/simplex-chat/pull/7614/files)
- [Windows CryptProtectData and roaming boundary](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata)
