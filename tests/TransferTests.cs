using System.Net;
using System.Net.Sockets;
using OpenShare;
using System.Text.Json;
var root = Path.Combine(Path.GetTempPath(), "openshare-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    foreach (var size in new[] { 0, 100_000, 2_000_000 })
    {
        var source = Path.Combine(root, $"payload-{size}.bin");
        File.WriteAllBytes(source, Enumerable.Range(0, size).Select(i => (byte)(i % 251)).ToArray());
        var destination = Path.Combine(root, "received");
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var receive = TransferEngine.ReceiveAsync(listener, destination, cancellationToken: timeout.Token);
        await TransferEngine.SendAsync(source, "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, cancellationToken: timeout.Token);
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
        var receiving = TransferEngine.ReceiveAsync(listener, destination, cancellationToken: cancel.Token);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new TransferHeader("incomplete.bin", 10000, new string('0', 64)));
        await client.GetStream().WriteAsync(BitConverter.GetBytes(bytes.Length));
        await client.GetStream().WriteAsync(bytes);
        await client.GetStream().WriteAsync(new byte[] { 1 });
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((!Directory.Exists(destination) || Directory.GetFiles(destination).Length == 0) && DateTime.UtcNow < deadline) await Task.Delay(20);
        if (!Directory.Exists(destination) || Directory.GetFiles(destination).Length == 0) throw new Exception("Receiver did not begin writing.");
        cancel.Cancel();
        try { await receiving.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Cancelled transfer succeeded."); }
        catch (OperationCanceledException) { }
        if (Directory.GetFiles(destination).Length != 0) throw new Exception("Cancellation left a partial file.");
    }
    Console.WriteLine("PASS: 3 verified round trips, 6 rejected unsafe transfers, and active-transfer cancellation with partial-file cleanup.");
}
finally { Directory.Delete(root, true); }

async Task Reject(string name, TransferHeader? header, byte[] payload, bool existing = false)
{
    var destination = Path.Combine(root, name); Directory.CreateDirectory(destination);
    if (existing) File.WriteAllText(Path.Combine(destination, "existing.txt"), "keep me");
    using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var receiving = TransferEngine.ReceiveAsync(listener, destination, cancellationToken: timeout.Token);
    using (var client = new TcpClient())
    {
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var stream = client.GetStream();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(header);
        await stream.WriteAsync(BitConverter.GetBytes(header is null ? int.MaxValue : bytes.Length));
        if (header is not null) { await stream.WriteAsync(bytes); await stream.WriteAsync(payload); }
        client.Client.Shutdown(SocketShutdown.Send);
        try { await receiving; throw new Exception("Unsafe transfer accepted: " + name); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException) { }
    }
    if (Directory.GetFiles(destination).Length != (existing ? 1 : 0)) throw new Exception("Partial file remains: " + name);
    if (existing && File.ReadAllText(Path.Combine(destination, "existing.txt")) != "keep me") throw new Exception("Existing file changed.");
}
