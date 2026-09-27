# OpenShare

OpenShare sends files directly between devices on the same network. It has no account, cloud upload, or required third-party service.

## Current workflow

1. On the receiving device, choose a folder and start receiving.
2. Share the displayed port code with the sender.
3. On the sending device, choose a file and enter the receiver address as `IP:code`.
4. OpenShare transfers the file directly and verifies it with SHA-256 before reporting success.

The first release is Windows-focused. Both devices must be able to connect to the receiver over the local network, and Windows Firewall may ask for permission.

OpenShare is not affiliated with Apple, Microsoft, Google, or any cloud provider.

## Build and test

```powershell
dotnet run --project tests/TransferTests.csproj
dotnet build OpenShare.csproj -c Release
dotnet publish OpenShare.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```
