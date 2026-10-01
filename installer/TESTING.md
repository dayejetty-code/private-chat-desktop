# Private Chat 0.8.1 test scope

The application is a development preview, not an independently audited messenger.

Application checks on 2026-10-01: 73 UI/security assertions and 71 native/network assertions passed. These include encrypted store reopening, wrong-password rejection, runtime integrity, safety-code checks, mandatory Tor settings, custom relay certificate rejection, bidirectional Unicode text delivery, offline delivery and fail-closed lifecycle behavior. Two synthetic profiles on the same Windows PC were used. The first Tor bootstrap timed out; the retry completed. TCP snapshots observed only loopback destinations for the chat workers at the sampled times; this is not continuous DNS/UDP/TCP leak testing.

Suspend/resume notifications were injected into the actual UI handlers; physical sleep and hibernation were not tested. No independent audit, physical cross-device acceptance, code signing or automatic verified updates have been completed. Encrypted backup/restore UI, unread indicators and contact blocking are still pending. A passed test is not a guarantee of confidentiality against compromised devices, recipients retaining messages or traffic-correlation attacks.

Installer validation is recorded separately in the source repository and accompanying release notes. Installation changes program files and shortcuts; the encrypted profile remains in `%LOCALAPPDATA%\PrivateChatDesktop`. It must not be removed by the uninstaller.
