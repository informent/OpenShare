# OpenShare 0.2.0 preview

This grouped update replaces the initial plaintext protocol with encrypted, privately paired transfers. Both devices need this version.

- TLS 1.2/1.3 with receiver certificate fingerprint verification and a fresh 256-bit pairing secret per receiving session.
- Receiver acknowledgement only after saving and SHA-256 verification.
- No overwriting existing files; temporary data removed after failed or cancelled transfers.
- Stop receiving, bounded connection setup and idle reads, and selectable pairing codes for available IPv4 interfaces.
- Expanded automated tests cover encrypted transfers, incorrect fingerprints and secrets, unsafe metadata, damaged data, cancellation, and pairing-code validation.

Windows x64 preview. Local tests passed, including published-app window creation and graceful shutdown. Two-physical-device testing and a full UI interaction review are still pending. Browser/phone support, disk-space preflight, and per-file receive approval are not implemented. Treat pairing codes as passwords. The app is unsigned and may trigger Windows SmartScreen.
