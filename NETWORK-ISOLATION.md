# Windows network isolation — 0.10.4

The chat worker now runs in a Windows AppContainer with **zero capabilities**. Windows denies its direct IPv4/IPv6 Internet connections and system DNS queries. The existing mandatory SOCKS configuration remains a separate control. AppContainer creation or policy checks failing aborts startup; there is no ordinary-process fallback.

## Process boundary

- The UI remains a normal, unelevated process. It brokers only selected local TXT files and uses inherited anonymous pipes to communicate with the worker.
- The core has no Internet or private-network capability and cannot create child processes. Its startup verifies AppContainer status, zero capabilities, and the expected package SID derived from the installation directory and Windows user.
- The pinned Tor and, when selected, lyrebird executables run in the same package with the `internetClient` capability. Windows permits communication within this package. No global loopback exemption is added.
- Tor and lyrebird have explicit process/thread DACLs that grant control to the parent user and SYSTEM, without granting the package peer access for injection or handle duplication.
- Children are created suspended and assigned to a kill-on-close job before their initial thread resumes. Worker and transport jobs remain separate. A transport exit ends its job and signals the existing core/lock guard.
- obfs4/Snowflake startup uses inherited pipes, the standard PT environment and VERSION/CMETHOD handshake. Tor then uses the validated loopback SOCKS5 transport endpoint. This avoids Tor's global named-pipe launcher, which cannot run inside AppContainer. Upstream binaries remain unmodified and hash-pinned.

The OS boundary restricts the core to the application's package, **not to one specific TCP port or to the Tor wire protocol**. Same-package transport sockets, including the bridge helper, are inside this boundary. The application's strict network configuration and Tor lifecycle guard enforce the intended Tor route. This is defense against unintended direct egress, not proof that a compromised core and transport together cannot circumvent Tor. It is not a firewall for every process on Windows or for the parent UI's shell/file-picker components.

## Files and local privileges

The installer remains per-user and does not require elevation. No Windows firewall rules, profiles, shared DNS service configuration or global loopback exemptions are modified.

Runtime directories grant the installation-specific package read/execute access. Explicit application data directories grant it modify access. Existing user ACLs and integrity labels are retained; no access is granted to Everyone or All Application Packages. Network and linked paths are rejected before granting access. The package identity is shared by the profiles opened from this installation; it is not a separate sandbox identity per chat profile.

Selected input files and export destinations are not granted to the core. The UI validates local TXT paths and contents, passes bytes through the inherited pipe, and writes an authenticated export to a new local file with its Internet-zone marker. No plaintext staging file is introduced. Plaintext is present in process memory and managed IPC buffers while handling a file; physical memory erasure is not guaranteed.

AppContainer profile metadata is per user. Application removal continues to preserve chat data. This version does not erase the OS-created AppContainer profile metadata as part of uninstall.

## Fail-closed checks

Before launching a worker or transport, the app checks that BFE and Windows Firewall services are running, that domain/private/public firewall profiles are enabled, and that this package has no global loopback exemption. The unlocked UI rechecks every five seconds and locks on failure. This polling is a secondary safety check, not protection against an administrator disabling or modifying Windows enforcement between checks.

Missing sandbox support, denied ACL changes, inaccessible policy state and failed process creation all stop startup. Launching the production executable directly with `--worker` outside the expected restricted token fails before the native core loads.

## Verification

`tests/network-isolation/Program.cs` exercises the production sandbox launcher, reads the actual production worker token, and compares unrestricted positive controls against the restricted process. It checks:

- direct IPv4/IPv6 TCP denied by Windows;
- DNS query with cache/hosts bypass denied, while the unrestricted query succeeds;
- unrelated IPv4/IPv6 loopback TCP listeners cannot be reached;
- UDP sent to owned external-package listeners is not delivered, while unrestricted datagrams arrive;
- SOCKS5 negotiation with the package's Tor succeeds;
- injection/handle-duplication access to Tor or the selected bridge helper is denied;
- child-process creation and reading an ungranted user file are denied;
- direct sockets and DNS stay blocked with Tor stopped and the native worker deliberately left alive.

This socket probe is separate from actual native-core text/TXT delivery tests. The bridge delivery probe runs from an installation path containing spaces. Packaging requires current production-binary hashes in both bridge and OS-isolation reports. Failed experiments are not delivery passes.

Tests on the developer's Windows machine are not an independent audit, exhaustive packet capture, or acceptance on all supported Windows versions. Bootstrap failures and relay availability remain network-dependent. Tor and bridge bootstrap/DNS/STUN traffic legitimately uses their granted network permission; these events must not be confused with a core direct-connection leak.

## References

- [Microsoft: Launch an AppContainer](https://learn.microsoft.com/en-us/windows/win32/secauthz/implementing-an-appcontainer)
- [Microsoft: AppContainer isolation](https://learn.microsoft.com/en-us/windows/win32/secauthz/appcontainer-isolation)
- [Microsoft: loopback configuration and memory ownership](https://learn.microsoft.com/en-us/windows/win32/api/networkisolation/nf-networkisolation-networkisolationgetappcontainerconfig)
- [Tor: pluggable transport environment](https://spec.torproject.org/pt-spec/configuration-environment.html)
- [Tor: parent/transport protocol](https://spec.torproject.org/pt-spec/ipc.html)
