# OpenShare 0.3.0 validation

## Verified locally on Windows, September 27, 2026

- Encrypted transfers of empty, 100,000-byte and 2,000,000-byte files.
- A 256 MiB file with a Unicode filename sent to a separate receiver process, with exact size and SHA-256 comparison.
- Rejection of incorrect certificate fingerprints, incorrect pairing secrets and malformed pairing codes.
- Rejection of traversal, reserved Windows device names, invalid lengths, oversized headers, insufficient advertised storage capacity, corrupted and truncated payloads.
- Preservation of existing files, locked-source failure, blocked-destination failure and partial-file cleanup.
- Cancellation while receiving, sending and awaiting approval.
- Two packaged app instances operated through Windows UI Automation: native file selection, pairing, acceptance, decline and approval expiry.
- Acceptance requires a matching saved-file hash. Decline and expiry require no saved file. Expired prompts must dismiss themselves.
- Actual rendered-window inspection found a clipped Send button; the layout now uses bounded scrolling instead of a fixed remaining-height panel.

## Limits, not passes

- Two physical PCs, separate Wi-Fi networks, firewall profiles and routed networks have not been exercised. Same-PC processes are not a substitute for that coverage.
- No independent security audit, exhaustive DPI/accessibility audit or code-signing certificate.
- The 256 MiB test does not prove multi-gigabyte performance. Free-space checks cannot reserve disk capacity against other applications.
- The single-file workflow does not support browser/mobile peers, folders or transfer resumption.
- A peer can interrupt a listening session by connecting and failing authentication. Restart receiving to issue a fresh code.
- Approval expires after 20 seconds. The native dialog is deliberately conservative and defaults to No.

No manual user testing is needed to run the automated suite. Evidence should be updated only after the relevant checks actually pass.
