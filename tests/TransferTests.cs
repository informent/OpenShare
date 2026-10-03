using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Net.Sockets;
using OpenShare;
using System.Text.Json;
using System.Diagnostics;
if (args.Length == 2 && args[0] == "--receiver")
{
    using var childSession = new ReceiveSession();
    using var childListener = new TcpListener(IPAddress.Loopback, 0);
    childListener.Start();
    Console.WriteLine(childSession.Code("127.0.0.1", ((IPEndPoint)childListener.LocalEndpoint).Port));
    using var childTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    await TransferEngine.ReceiveAsync(childListener, args[1], childSession, (_, _) => Task.FromResult(true), cancellationToken: childTimeout.Token);
    return;
}
var root = Path.Combine(Path.GetTempPath(), "openshare-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
using var session = new ReceiveSession();
try
{
    foreach (var size in new[] { 0, 100_000, 2_000_000 })
    {
        var source = Path.Combine(root, $"payload-{size}.bin");
        File.WriteAllBytes(source, Enumerable.Range(0, size).Select(i => (byte)(i % 251)).ToArray());
        var destination = Path.Combine(root, "received");
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var receive = TransferEngine.ReceiveAsync(listener, destination, session, (_, _) => Task.FromResult(true), cancellationToken: timeout.Token);
        var send = TransferEngine.SendAsync(source, session.Code("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port), cancellationToken: timeout.Token);
        try { await Task.WhenAll(receive, send); }
        catch { Console.Error.WriteLine(receive.Exception); throw; }
        var header = await receive;
        var sentReceipt = await send;
        if (sentReceipt.Direction != "Sent" || header.Direction != "Received" || sentReceipt.Sha256 != header.Sha256 || sentReceipt.Length != header.Length) throw new Exception("Transfer receipts disagree.");
        if (!File.ReadAllBytes(Path.Combine(destination, header.Name)).SequenceEqual(File.ReadAllBytes(source))) throw new Exception("Content mismatch.");
    }
    var historyPath = Path.Combine(root, "history", "receipts.json");
    for (var i = 0; i < 105; i++) TransferHistory.Append(new("Sent", DateTimeOffset.UtcNow, $"file-{i}.bin", i, new string('A', 64)), historyPath, 100);
    var history = TransferHistory.Load(historyPath);
    if (history.Count != 100 || history[0].Name != "file-5.bin" || File.ReadAllText(historyPath).Contains("openshare1|")) throw new Exception("Bounded private transfer history failed.");
    await Reject("collision", new TransferHeader("existing.txt", 0, new string('0', 64)), Array.Empty<byte>(), true);
    await Reject("traversal", new TransferHeader("../outside.txt", 0, new string('0', 64)), Array.Empty<byte>());
    await Reject("negative", new TransferHeader("file.txt", -1, new string('0', 64)), Array.Empty<byte>());
    await Reject("tamper", new TransferHeader("file.txt", 3, new string('0', 64)), new byte[] { 1, 2, 3 });
    await Reject("truncated", new TransferHeader("file.txt", 8, new string('0', 64)), new byte[] { 1 });
    await Reject("oversized-header", null, Array.Empty<byte>());
    await Reject("disk-space", new TransferHeader("huge.bin", long.MaxValue, new string('0', 64)), Array.Empty<byte>());
    await Reject("reserved-name", new TransferHeader("CON.txt", 0, new string('0', 64)), Array.Empty<byte>());
    foreach (var name in new[] { "NUL", "AUX.log", "COM1.txt", "LPT9", "trail.", "trail ", "file:stream", "..\\escape.bin" })
        await Reject("unsafe-" + Guid.NewGuid().ToString("N"), new TransferHeader(name, 0, new string('0', 64)), Array.Empty<byte>());
    using (var listener = new TcpListener(IPAddress.Loopback, 0))
    {
        listener.Start();
        using var cancel = new CancellationTokenSource();
        var destination = Path.Combine(root, "cancelled");
        var receiving = TransferEngine.ReceiveAsync(listener, destination, session, (_, _) => Task.FromResult(true), cancellationToken: cancel.Token);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var stream = await Connect(client);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new TransferHeader("incomplete.bin", 10000, new string('0', 64)));
        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length));
        await stream.WriteAsync(bytes);
        await stream.WriteAsync(new byte[] { 1 });
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((!Directory.Exists(destination) || Directory.GetFiles(destination).Length == 0) && DateTime.UtcNow < deadline) await Task.Delay(20);
        if (!Directory.Exists(destination) || Directory.GetFiles(destination).Length == 0) throw new Exception("Receiver did not begin writing.");
        cancel.Cancel();
        try { await receiving.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Cancelled transfer succeeded."); }
        catch (OperationCanceledException) { }
        if (Directory.GetFiles(destination).Length != 0) throw new Exception("Cancellation left a partial file.");
    }
    foreach (var wrongFingerprint in new[] { true, false })
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var destination = Path.Combine(root, wrongFingerprint ? "wrong-pin" : "wrong-secret");
        var receiving = TransferEngine.ReceiveAsync(listener, destination, session, (_, _) => Task.FromResult(true), cancellationToken: timeout.Token);
        var code = session.Code("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port);
        code = wrongFingerprint ? code with { Fingerprint = new string('0', 64) } : code with { Secret = new string('0', 64) };
        try { await TransferEngine.SendAsync(Path.Combine(root, "payload-100000.bin"), code, cancellationToken: timeout.Token); throw new Exception("Invalid pairing accepted."); }
        catch (Exception ex) when (ex is AuthenticationException or IOException) { }
        try { await receiving; throw new Exception("Receiver accepted invalid pairing."); }
        catch (Exception ex) when (ex is AuthenticationException or IOException) { }
        if (Directory.Exists(destination)) throw new Exception("Invalid pairing touched the destination.");
    }
    foreach (var invalid in new[] { "127.0.0.1:1234", "", "openshare1|localhost|1234|00|00", session.Code("127.0.0.1", 0).ToString() })
    {
        try { PairingCode.Parse(invalid); throw new Exception("Malformed pairing accepted."); }
        catch (FormatException) { }
    }
    using (var listener = new TcpListener(IPAddress.Loopback, 0))
    {
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var destination = Path.Combine(root, "declined");
        var asked = false;
        var receiving = TransferEngine.ReceiveAsync(listener, destination, session, (header, _) =>
        {
            asked = true;
            if (Directory.Exists(destination)) throw new Exception("Destination created before approval.");
            return Task.FromResult(false);
        }, cancellationToken: timeout.Token);
        try { await TransferEngine.SendAsync(Path.Combine(root, "payload-100000.bin"), session.Code("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port), cancellationToken: timeout.Token); throw new Exception("Declined transfer reported success."); }
        catch (IOException) { }
        try { await receiving; throw new Exception("Declined transfer accepted."); }
        catch (IOException ex) when (ex.Message.Contains("declined")) { }
        if (!asked || Directory.Exists(destination)) throw new Exception("Decline did not protect destination.");
    }
    using (var listener = new TcpListener(IPAddress.Loopback, 0))
    {
        listener.Start();
        using var stopSending = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var stopReceiving = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pendingApproval = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var destination = Path.Combine(root, "cancel-before-approval");
        var receiving = TransferEngine.ReceiveAsync(listener, destination, session, async (_, token) =>
        {
            pendingApproval.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return true;
        }, cancellationToken: stopReceiving.Token);
        var sending = TransferEngine.SendAsync(Path.Combine(root, "payload-2000000.bin"), session.Code("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port), cancellationToken: stopSending.Token);
        await pendingApproval.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stopSending.Cancel();
        try { await sending; throw new Exception("Cancelled sender succeeded."); }
        catch (OperationCanceledException) { }
        stopReceiving.Cancel();
        try { await receiving; throw new Exception("Cancelled approval succeeded."); }
        catch (OperationCanceledException) { }
        if (Directory.Exists(destination)) throw new Exception("Pending approval wrote to disk.");
    }
    var validCode = session.Code("127.0.0.1", 1234);
    if (PairingCode.Parse(validCode.ToString()) != validCode) throw new Exception("Pairing code round trip failed.");
    await SeparateProcessTransfer();
    using (var locked = new FileStream(Path.Combine(root, "payload-100000.bin"), FileMode.Open, FileAccess.Read, FileShare.None))
    {
        try { await TransferEngine.SendAsync(locked.Name, session.Code("127.0.0.1", 1)); throw new Exception("Locked source was accepted."); }
        catch (IOException) { }
    }
    using (var listener = new TcpListener(IPAddress.Loopback, 0))
    {
        listener.Start();
        var blockedFolder = Path.Combine(root, "not-a-directory");
        File.WriteAllText(blockedFolder, "preserve this file");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var receiving = TransferEngine.ReceiveAsync(listener, blockedFolder, session, (_, _) => Task.FromResult(true), cancellationToken: timeout.Token);
        try { await TransferEngine.SendAsync(Path.Combine(root, "payload-100000.bin"), session.Code("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port), cancellationToken: timeout.Token); throw new Exception("Invalid destination reported success."); }
        catch (IOException) { }
        try { await receiving; throw new Exception("Invalid destination accepted."); }
        catch (IOException) { }
        if (File.ReadAllText(blockedFolder) != "preserve this file") throw new Exception("Destination blocker was modified.");
    }
    Console.WriteLine("PASS: encrypted round trips, 16 unsafe transfers rejected, explicit decline, receive/send/approval cancellation, wrong certificate and secret rejection, pairing parsing, and separate-process 256 MiB Unicode transfer.");
}
finally { Directory.Delete(root, true); }

async Task Reject(string name, TransferHeader? header, byte[] payload, bool existing = false)
{
    var destination = Path.Combine(root, name); Directory.CreateDirectory(destination);
    if (existing) File.WriteAllText(Path.Combine(destination, "existing.txt"), "keep me");
    using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var receiving = TransferEngine.ReceiveAsync(listener, destination, session, (_, _) => Task.FromResult(true), cancellationToken: timeout.Token);
    using (var client = new TcpClient())
    {
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var stream = await Connect(client);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(header);
        try
        {
            await stream.WriteAsync(BitConverter.GetBytes(header is null ? int.MaxValue : bytes.Length));
            if (header is not null) { await stream.WriteAsync(bytes); await stream.WriteAsync(payload); }
            await stream.ShutdownAsync();
        }
        catch (IOException) { /* The receiver may close immediately on invalid metadata. Its result is asserted below. */ }
        try { await receiving; throw new Exception("Unsafe transfer accepted: " + name); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException) { }
    }
    if (Directory.GetFiles(destination).Length != (existing ? 1 : 0)) throw new Exception("Partial file remains: " + name);
    if (existing && File.ReadAllText(Path.Combine(destination, "existing.txt")) != "keep me") throw new Exception("Existing file changed.");
}
async Task<SslStream> Connect(TcpClient client)
{
    var stream = new SslStream(client.GetStream(), false, (_, cert, _, _) =>
        cert is not null && cert.GetCertHashString(HashAlgorithmName.SHA256) == session.Certificate.GetCertHashString(HashAlgorithmName.SHA256));
    await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "OpenShare" });
    await stream.WriteAsync(session.Secret);
    var acknowledgement = new byte[1];
    await stream.ReadExactlyAsync(acknowledgement);
    if (acknowledgement[0] != 1) throw new Exception("Pairing rejected.");
    return stream;
}

async Task SeparateProcessTransfer()
{
    var source = Path.Combine(root, "Unicode-\u65e5\u672c\u8a9e-\u00e9-large.bin");
    var destination = Path.Combine(root, "separate-process");
    await using (var file = File.Create(source))
    {
        var block = RandomNumberGenerator.GetBytes(1024 * 1024);
        for (var i = 0; i < 256; i++) await file.WriteAsync(block);
    }
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add("--receiver");
    start.ArgumentList.Add(destination);
    using var child = Process.Start(start) ?? throw new Exception("Receiver process did not start.");
    var stderr = child.StandardError.ReadToEndAsync();
    try
    {
        var code = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
        if (code is null) throw new Exception("Receiver exited before pairing: " + await stderr);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await TransferEngine.SendAsync(source, PairingCode.Parse(code), cancellationToken: timeout.Token);
        await child.WaitForExitAsync(timeout.Token);
        if (child.ExitCode != 0) throw new Exception("Receiver failed: " + await stderr);
        using var original = File.OpenRead(source);
        using var received = File.OpenRead(Path.Combine(destination, Path.GetFileName(source)));
        if (original.Length != received.Length || !SHA256.HashData(original).SequenceEqual(SHA256.HashData(received))) throw new Exception("Separate-process content mismatch.");
        Console.WriteLine("PASS: separate receiver process saved and verified 256 MiB with a Unicode filename.");
    }
    finally { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } }
}
