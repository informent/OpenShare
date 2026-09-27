# OpenShare

OpenShare sends files directly between devices on the same network. It has no account, cloud upload, or required third-party service.

## Current workflow

1. On the receiving device, choose a folder and start receiving.
2. Select and copy one complete pairing code from the receiving panel. Share it privately with the sender.
3. On the sending device, choose a file and paste that pairing code.
4. The receiver reviews the filename, size, and destination, then accepts or declines within 20 seconds. No file is written before acceptance.
5. OpenShare transfers the file directly and verifies it with SHA-256 before reporting success. Use Cancel sending or Stop receiving to interrupt a transfer.

This Windows preview requires both devices to connect over the local network. Windows Firewall may ask for permission. Transfers use TLS with the receiver's SHA-256 certificate fingerprint pinned in the pairing code. A random session secret is checked before any file metadata is accepted. There is no plaintext fallback and no certificate is installed in your trust store. Browser and phone support are not implemented yet.

Keep pairing codes private. Anyone with the current code can send one file; this does not prove their personal identity. Each receiving session creates a fresh certificate and secret. When several network addresses are listed, use the code for the network shared by both devices. A loopback address (`127.0.0.1`) works only on the same PC. Both peers must run this updated protocol; old `IP:port` codes are rejected.

The development branch adds receive approval, a disk-space preflight with a 16 MiB reserve, and send cancellation. These changes are not in the 0.2.0 release. Free space can change after preflight; storage failures still abort the transfer and remove partial data.

Validation includes encrypted loopback transfers, wrong certificate and secret rejection, malformed codes, unsafe filenames, tampered/truncated data, overwrite protection, decline without disk writes, and cancellation cleanup. This is not an independent security audit or proof of two-device network compatibility. Browser/phone support and a full UI interaction review remain future work.

Incoming files are staged until their hash is verified. Existing files are never overwritten, and incomplete transfers are removed. The sender reports success only after the receiver confirms verification and saving. This protocol requires both peers to use the updated version.

OpenShare is not affiliated with Apple, Microsoft, Google, or any cloud provider.

## Build and test

```powershell
dotnet run --project tests/TransferTests.csproj
dotnet build OpenShare.csproj -c Release
dotnet publish OpenShare.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```
