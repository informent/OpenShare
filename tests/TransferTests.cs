using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Net.Sockets;
using OpenShare;
using System.Text.Json;
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
        var receive = TransferEngine.ReceiveAsync(listener, destination, session, cancellationToken: timeout.Token);
        var send = TransferEngine.SendAsync(source, session.Code("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port), cancellationToken: timeout.Token);
        try { await Task.WhenAll(receive, send); }
        catch { Console.Error.WriteLine(receive.Exception); throw; }
        var header = await receive;
        if (!File.ReadAllBytes(Path.Combine(destination, header.Name)).SequenceEqual(File.ReadAllBytes(source))) throw new Exception("Content mismatch.");
    }
    await Reject("collision", new TransferHeader("existing.txt", 0, new string('0', 64)), Array.Empty<byte>(), true);
    await Reject("traversal", new TransferHeader("../outside.txt", 0, new string('0', 64)), Array.Empty<byte>());
    await Reject("negative", new TransferHeader("file.txt", -1, new string('0', 64)), Array.Empty<byte>());
    await Reject("tamper", new TransferHeader("file.txt", 3, new string('0', 64)), new byte[] { 1, 2, 3 });
    await Reject("truncated", new TransferHeader("file.txt", 8, new string('0', 64)), new byte[] { 1 });
    await Reject("oversized-header", null, Array.Empty<byte>());
    using (var listener = new TcpListener(IPAddress.Loopback, 0))
    {
        listener.Start();
        using var cancel = new CancellationTokenSource();
        var destination = Path.Combine(root, "cancelled");
        var receiving = TransferEngine.ReceiveAsync(listener, destination, session, cancellationToken: cancel.Token);
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
        var receiving = TransferEngine.ReceiveAsync(listener, destination, session, cancellationToken: timeout.Token);
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
    var validCode = session.Code("127.0.0.1", 1234);
    if (PairingCode.Parse(validCode.ToString()) != validCode) throw new Exception("Pairing code round trip failed.");
    Console.WriteLine("PASS: 3 encrypted round trips, 6 unsafe transfers rejected, cancellation cleanup, wrong certificate and secret rejected, and 5 pairing parser checks.");
}
finally { Directory.Delete(root, true); }

async Task Reject(string name, TransferHeader? header, byte[] payload, bool existing = false)
{
    var destination = Path.Combine(root, name); Directory.CreateDirectory(destination);
    if (existing) File.WriteAllText(Path.Combine(destination, "existing.txt"), "keep me");
    using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var receiving = TransferEngine.ReceiveAsync(listener, destination, session, cancellationToken: timeout.Token);
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
