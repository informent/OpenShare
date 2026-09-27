# OpenShare 0.3.0 preview

This grouped reliability update adds receiver consent, storage safeguards, usable progress feedback and executable-level regression tests.

- Review the filename, size and destination before accepting an incoming file. Approval defaults to No and expires automatically.
- Preflight disk space with a 16 MiB reserve; reject Windows device names and preserve existing files.
- Cancel sending as well as receiving. Hash large sources before connecting to avoid receiver idle timeouts.
- Show transferred size, approximate speed, estimated remaining time and the verification stage.
- Correct a clipped Send button and provide scrolling at smaller window sizes.
- Write privacy-minimized diagnostic logs under Downloads/GITHUB/OpenShare/Logs.
- Test 256 MiB encrypted transfers between separate processes, Unicode names, locked sources, blocked destinations, and failure cleanup.
- Exercise the packaged app's file picker, pairing, acceptance, rejection and timeout through automated UI tests.

Windows x64 preview. Local engine and packaged UI tests passed. Two-physical-device testing, an exhaustive DPI/accessibility review and an independent security audit remain unverified. Browser/phone peers, folders and resumable transfers are not implemented. Treat pairing codes as passwords. The app is unsigned and may trigger Windows SmartScreen. See VALIDATION.md for exact coverage and limits.
